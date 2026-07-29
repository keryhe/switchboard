using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.Protocol.Framing;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Server.ServerConnections;

/// <summary>
/// Turns envelopes arriving from an app server into router calls / connection lifecycle actions
/// (03-protocol.md §2.3). Registered in place of <see cref="LoggingServerEnvelopeDispatcher"/> once
/// the router is available (Slice 4).
/// </summary>
public sealed class RoutingServerEnvelopeDispatcher(
    IMessageRouter router,
    IConnectionRegistry connectionRegistry,
    ILocalTransportRegistry localTransportRegistry,
    IServerConnectionSelector serverConnectionSelector,
    IBackplane backplane,
    IGroupMembershipService groupMembership,
    IOptions<SwitchboardOptions> options,
    SwitchboardMetrics metrics,
    ILogger<RoutingServerEnvelopeDispatcher> logger) : IServerEnvelopeDispatcher
{
    private readonly string _nodeId = options.Value.NodeId;


    public async ValueTask DispatchAsync(string serverConnectionId, ServerEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case ServerEnvelopeType.SendToConnection:
                await RecordOutboundAsync(envelope.HubName, () => router.RouteToConnectionAsync(envelope.ConnectionId!, envelope.Payload!, envelope.HubProtocol!, ct));
                break;

            case ServerEnvelopeType.Broadcast:
                await RecordOutboundAsync(envelope.HubName, () => router.BroadcastAsync(
                    envelope.HubName!,
                    envelope.Payload!,
                    envelope.HubProtocol!,
                    envelope.Payloads,
                    envelope.ExcludedConnectionIds?.ToHashSet(),
                    ct));
                break;

            case ServerEnvelopeType.SendToGroup:
                await RecordOutboundAsync(envelope.HubName, () => router.SendToGroupAsync(
                    envelope.HubName!,
                    envelope.GroupName!,
                    envelope.Payload!,
                    envelope.HubProtocol!,
                    envelope.Payloads,
                    envelope.ExcludedConnectionIds?.ToHashSet(),
                    ct));
                break;

            case ServerEnvelopeType.SendToUser:
                await RecordOutboundAsync(envelope.HubName, () => router.SendToUserAsync(envelope.HubName!, envelope.UserId!, envelope.Payload!, envelope.HubProtocol!, envelope.Payloads, ct));
                break;

            case ServerEnvelopeType.AddToGroup:
                // Delegates to IGroupMembershipService (Phase 4 plan decision D23) so the
                // management API's group endpoints share this exact code path rather than
                // reimplementing the cross-node forward the Phase 3 Slice 7 fix depends on.
                await groupMembership.AddToGroupAsync(envelope.ConnectionId!, envelope.GroupName!, ct);
                break;

            case ServerEnvelopeType.RemoveFromGroup:
                await groupMembership.RemoveFromGroupAsync(envelope.ConnectionId!, envelope.GroupName!, ct);
                break;

            case ServerEnvelopeType.CloseConnection:
                await CloseClientConnectionAsync(envelope.ConnectionId!, envelope.Error, ct);
                break;

            default:
                logger.LogWarning("Unexpected envelope type {EnvelopeType} from server connection {ServerConnectionId}.", envelope.Type, serverConnectionId);
                break;
        }
    }

    private async Task CloseClientConnectionAsync(string connectionId, string? error, CancellationToken ct)
    {
        var transport = localTransportRegistry.Get(connectionId);
        if (transport is null)
        {
            // Not local — cluster-wide server-connection assignment (plan decision D18) means the
            // app server that sent this CloseConnection may not share a node with the client it
            // names. Whichever node the client actually lives on does the full teardown below via
            // its own IHubObserver.OnCloseConnection; a null owner lookup (already gone) is a no-op.
            await backplane.PublishCloseConnectionAsync(connectionId, error, allowReconnect: false, _nodeId, ct);
            return;
        }

        var state = await connectionRegistry.GetAsync(connectionId, ct);

        // Encoded in the connection's own negotiated protocol (state.HubProtocol) so a
        // MessagePack client receives a MessagePack-framed Close, not JSON.
        var closeFrame = ClientFrameWriter.Close(state?.HubProtocol ?? "json", error);
        await transport.Output.Writer.WriteAsync(closeFrame, ct);
        await transport.CloseAsync(error);

        if (state is not null)
        {
            await serverConnectionSelector.ReleaseConnectionAsync(state.HubName, state.ServerConnectionId, ct);
        }

        await connectionRegistry.UnregisterAsync(connectionId, ct);
        localTransportRegistry.Unregister(connectionId);
    }

    /// <summary>Wraps a router call with <c>signalr.messages.routed</c>{direction=outbound} and
    /// <c>signalr.message.outbound_duration</c> (plan decision D25) — envelope received from an app
    /// server to the router call returning, which covers both a local transport write and (for a
    /// non-local target) the backplane publish that hands delivery to the owning node.</summary>
    private async ValueTask RecordOutboundAsync(string? hubName, Func<ValueTask> routeAsync)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await routeAsync();
        metrics.MessagesRouted.Add(1,
            new KeyValuePair<string, object?>("direction", "outbound"),
            new KeyValuePair<string, object?>("hub", hubName ?? "unknown"));
        metrics.MessageOutboundDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
    }
}
