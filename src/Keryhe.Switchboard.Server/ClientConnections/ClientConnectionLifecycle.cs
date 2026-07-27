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
        IConnectionRegistry connectionRegistry,
        ILocalTransportRegistry localTransportRegistry,
        ClientConnectionManager connectionManager,
        ServerConnectionState serverConnectionState,
        IMessageRouter router,
        TimeSpan keepAliveInterval,
        CancellationToken ct)
    {
        var state = new ClientConnectionState
        {
            ConnectionId = connection.ConnectionId,
            ConnectionToken = connection.ConnectionToken,
            HubName = connection.HubName,
            UserId = connection.UserId,
            Transport = TransportType.WebSockets,
            TransportHandle = connection.Transport,
            ServerConnectionId = serverConnectionState.ConnectionId,
            ConnectedAt = DateTimeOffset.UtcNow,
        };

        await connectionRegistry.RegisterAsync(state, ct);
        localTransportRegistry.Register(connection.Transport);
        connectionManager.Register(connection);
        serverConnectionState.IncrementLogicalCount();

        await connectionRegistry.SetProtocolAsync(connection.ConnectionId, connection.HubProtocol, ct);

        await serverConnectionState.Connection.SendAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.OpenConnection,
            ConnectionId = connection.ConnectionId,
            HubProtocol = connection.HubProtocol,
            UserId = pending.UserId,
            Claims = pending.Claims,
        }, ct);

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

            await serverConnectionState.Connection.SendAsync(new ServerEnvelope
            {
                Type = ServerEnvelopeType.CloseConnection,
                ConnectionId = connection.ConnectionId,
            }, CancellationToken.None);

            serverConnectionState.DecrementLogicalCount();
            connectionManager.Unregister(connection);
            localTransportRegistry.Unregister(connection.ConnectionId);
            await connectionRegistry.UnregisterAsync(connection.ConnectionId, CancellationToken.None);
        }
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
