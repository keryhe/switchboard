using System.Buffers;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.Protocol.Framing;
using Keryhe.Switchboard.Registry;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// Handshake negotiation and the register/message-loop/teardown lifecycle for a client
/// connection, extracted out of the WebSocket-specific request handler (plan decision D10) so it
/// can eventually be driven by any <see cref="IFramedClientTransport"/> — not just one whose
/// entire lifetime is a single HTTP request, which is all a WebSocket upgrade ever was. SSE and
/// Long Polling (Slices 5/6) reuse this unchanged; only how bytes physically get in and out
/// differs per transport.
/// </summary>
public static class ClientConnectionLifecycle
{
    private const int SupportedHandshakeVersion = 1;

    /// <summary>
    /// Reads the first frame as a (always-JSON — finding 1) handshake request, switches
    /// <paramref name="transport"/>'s framing to the negotiated protocol, and writes the
    /// handshake response. On failure, an error has already been written and the transport closed;
    /// callers should stop immediately.
    /// </summary>
    public static async Task<string?> NegotiateHandshakeAsync(IFramedClientTransport transport, ReadOnlyMemory<byte> firstFrame)
    {
        var handshakeBuffer = new ReadOnlySequence<byte>(firstFrame);
        if (!HandshakeProtocol.TryParseRequestMessage(ref handshakeBuffer, out var handshakeRequest))
        {
            await transport.CloseAsync("Malformed handshake request.");
            return null;
        }

        if (handshakeRequest.Version != SupportedHandshakeVersion ||
            handshakeRequest.Protocol is not ("json" or "messagepack") ||
            (handshakeRequest.Protocol == "messagepack" && !transport.SupportsBinaryTransferFormat))
        {
            await transport.Output.Writer.WriteAsync(
                ClientFrameWriter.HandshakeError($"Requested protocol '{handshakeRequest.Protocol}' is not available."));
            await transport.CloseAsync("Handshake failed.");
            return null;
        }

        var protocol = handshakeRequest.Protocol;

        // The handshake response's own bytes are always JSON; only the WebSocket frame type it
        // goes out in follows the negotiated protocol — Framing must switch before this write.
        transport.Framing = protocol == "messagepack" ? MessagePackFraming.Instance : JsonFraming.Instance;
        await transport.Output.Writer.WriteAsync(ClientFrameWriter.HandshakeResponse());

        return protocol;
    }

    /// <summary>
    /// Registers the connection, announces it to the assigned app server, feeds every subsequent
    /// inbound frame to the router (absorbing hub-level Ping per plan decision D13), and tears
    /// down on completion. Runs until <paramref name="frames"/> completes or faults; the caller
    /// owns the transport's own lifetime (e.g. the WebSocket request awaiting this call).
    /// </summary>
    public static async Task RunAsync(
        ClientConnection connection,
        PendingConnection pending,
        IAsyncEnumerator<ReadOnlyMemory<byte>> frames,
        TransportType transportType,
        IConnectionRegistry connectionRegistry,
        ILocalTransportRegistry localTransportRegistry,
        ClientConnectionManager connectionManager,
        IHubRegistry hubRegistry,
        IServerConnectionSelector serverConnectionSelector,
        IBackplane backplane,
        string serverConnectionRef,
        string localNodeId,
        IMessageRouter router,
        TimeSpan keepAliveInterval,
        SwitchboardTracing tracing,
        CancellationToken ct)
    {
        var state = new ClientConnectionState
        {
            ConnectionId = connection.ConnectionId,
            ConnectionToken = connection.ConnectionToken,
            HubName = connection.HubName,
            UserId = connection.UserId,
            Transport = transportType,
            ServerConnectionId = serverConnectionRef,
            ConnectedAt = DateTimeOffset.UtcNow,
        };

        // Always-on (plan decision D26), scoped to just the establish steps — registration through
        // the app server's OpenConnection acknowledgment — rather than the connection's whole
        // (potentially hours-long) lifetime, which a "connect" span shouldn't model.
        using (tracing.StartClientConnectActivity(connection.HubName, connection.ConnectionId, localNodeId))
        {
            await connectionRegistry.RegisterAsync(state, ct);
            localTransportRegistry.Register(connection.Transport, connection.HubName, connection.UserId);
            localTransportRegistry.SetHubProtocol(connection.ConnectionId, connection.HubProtocol);
            connectionManager.Register(connection);

            await connectionRegistry.SetProtocolAsync(connection.ConnectionId, connection.HubProtocol, ct);

            await SendToAssignedServerConnectionAsync(hubRegistry, backplane, connection.HubName, serverConnectionRef, localNodeId, new ServerEnvelope
            {
                Type = ServerEnvelopeType.OpenConnection,
                ConnectionId = connection.ConnectionId,
                HubProtocol = connection.HubProtocol,
                UserId = pending.UserId,
                Claims = pending.Claims,
            }, ct);
        }

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pingLoop = RunKeepAlivePingLoopAsync(connection, keepAliveInterval, pingCts.Token);

        try
        {
            while (await frames.MoveNextAsync())
            {
                var frame = frames.Current;
                connection.LastSeen = DateTimeOffset.UtcNow;

                // The service owns client keep-alive (04-design.md §3/§6, plan decision D13): a
                // hub-level Ping is absorbed here rather than forwarded, so it never reaches the
                // app server as a client_message.
                if (HubMessageClassifier.IsPing(connection.HubProtocol, new ReadOnlySequence<byte>(frame)))
                {
                    continue;
                }

                await router.RouteClientMessageAsync(connection.ConnectionId, frame, connection.HubProtocol, ct);
            }
        }
        finally
        {
            await pingCts.CancelAsync();
            try
            {
                await pingLoop;
            }
            catch (OperationCanceledException)
            {
            }

            // Local/registry cleanup runs before notifying the app server, not after — found by a
            // Phase 3 Orleans e2e test racing on exactly this ordering (Phase 3 plan §Slice 1
            // gate): with an in-memory registry, UnregisterAsync completes synchronously fast
            // enough that the old send-then-cleanup order was never observably wrong, but a
            // registry with real latency (a grain call) can leave the connection still resolvable
            // via IConnectionRegistry.GetAsync for a window after close_connection was already
            // sent and observed by the app server. Tearing down first removes that window
            // entirely, and nothing depends on close_connection arriving before cleanup finishes.
            await serverConnectionSelector.ReleaseConnectionAsync(connection.HubName, serverConnectionRef, CancellationToken.None);
            connectionManager.Unregister(connection);
            localTransportRegistry.Unregister(connection.ConnectionId);
            await connectionRegistry.UnregisterAsync(connection.ConnectionId, CancellationToken.None);

            await SendToAssignedServerConnectionAsync(hubRegistry, backplane, connection.HubName, serverConnectionRef, localNodeId, new ServerEnvelope
            {
                Type = ServerEnvelopeType.CloseConnection,
                ConnectionId = connection.ConnectionId,
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Delivers a service→app-server envelope (open/close — <c>client_message</c> goes through
    /// <c>DefaultMessageRouter.RouteClientMessageAsync</c> instead, on the same hot path as every
    /// other client-originated message) to whichever node <paramref name="serverConnectionRef"/>
    /// names (plan decision D18, Phase 3 Slice 4). A local match writes directly to the live
    /// <see cref="IServerConnection"/>; a remote one serializes the envelope exactly as it would go
    /// over the wire to an app server and publishes it once via the backplane, mirroring
    /// <c>DefaultMessageRouter.RouteToConnectionAsync</c>'s own local-hit-or-backplane shape one
    /// level up. A missing local connection (already gone) or a malformed reference is dropped
    /// silently — this call never blocks connect/disconnect on a race it cannot itself resolve.
    /// </summary>
    private static async Task SendToAssignedServerConnectionAsync(
        IHubRegistry hubRegistry,
        IBackplane backplane,
        string hubName,
        string serverConnectionRef,
        string localNodeId,
        ServerEnvelope envelope,
        CancellationToken ct)
    {
        if (!ServerConnectionRef.TryParse(serverConnectionRef, out var ownerNodeId, out var serverConnectionId))
        {
            return;
        }

        if (ownerNodeId == localNodeId)
        {
            var serverConnection = hubRegistry.GetHub(hubName)?.ServerConnections.GetValueOrDefault(serverConnectionId)?.Connection;
            if (serverConnection is not null)
            {
                await serverConnection.SendAsync(envelope, ct);
            }

            return;
        }

        var writer = new ArrayBufferWriter<byte>();
        ServerEnvelopeSerializer.Write(writer, envelope);
        await backplane.PublishServerEnvelopeAsync(hubName, serverConnectionRef, writer.WrittenMemory.ToArray(), ct);
    }

    /// <summary>
    /// The service, not the app server, owns client keep-alive (04-design.md §3/§6): every
    /// negotiated client connection needs a periodic hub-level Ping or a real client's own
    /// <c>serverTimeoutInMilliseconds</c> (30s by default) elapses with nothing received and it
    /// tears down the connection as dead — reconnecting needlessly even though the connection was
    /// simply idle. Runs for the lifetime of the connection, independent of whether the app server
    /// or the client itself is sending anything.
    /// </summary>
    private static async Task RunKeepAlivePingLoopAsync(ClientConnection connection, TimeSpan keepAliveInterval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(keepAliveInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            await connection.Transport.Output.Writer.WriteAsync(ClientFrameWriter.Ping(connection.HubProtocol), ct);
        }
    }
}
