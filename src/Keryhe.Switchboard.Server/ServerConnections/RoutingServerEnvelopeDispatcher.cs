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
    IOptions<SwitchboardOptions> options,
    ILogger<RoutingServerEnvelopeDispatcher> logger) : IServerEnvelopeDispatcher
{
    private readonly string _nodeId = options.Value.NodeId;


    public async ValueTask DispatchAsync(string serverConnectionId, ServerEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case ServerEnvelopeType.SendToConnection:
                await router.RouteToConnectionAsync(envelope.ConnectionId!, envelope.Payload!, envelope.HubProtocol!, ct);
                break;

            case ServerEnvelopeType.Broadcast:
                await router.BroadcastAsync(
                    envelope.HubName!,
                    envelope.Payload!,
                    envelope.HubProtocol!,
                    envelope.Payloads,
                    envelope.ExcludedConnectionIds?.ToHashSet(),
                    ct);
                break;

            case ServerEnvelopeType.SendToGroup:
                await router.SendToGroupAsync(
                    envelope.HubName!,
                    envelope.GroupName!,
                    envelope.Payload!,
                    envelope.HubProtocol!,
                    envelope.Payloads,
                    envelope.ExcludedConnectionIds?.ToHashSet(),
                    ct);
                break;

            case ServerEnvelopeType.SendToUser:
                await router.SendToUserAsync(envelope.HubName!, envelope.UserId!, envelope.Payload!, envelope.HubProtocol!, envelope.Payloads, ct);
                break;

            case ServerEnvelopeType.AddToGroup:
                await connectionRegistry.AddToGroupAsync(envelope.ConnectionId!, envelope.GroupName!, ct);
                if (localTransportRegistry.Get(envelope.ConnectionId!) is not null)
                {
                    localTransportRegistry.AddToGroup(envelope.ConnectionId!, envelope.GroupName!);
                }
                else
                {
                    // Not local — cluster-wide server-connection assignment (plan decision D18)
                    // means the app server that sent this AddToGroup may not share a node with the
                    // connection it names. Group membership is node-local state
                    // (ILocalTransportRegistry, plan decision D14), so whichever node actually has
                    // this connection must be the one whose index gets mutated (Phase 3 Slice 7 fix).
                    await backplane.PublishAddToGroupAsync(envelope.ConnectionId!, envelope.GroupName!, _nodeId, ct);
                }

                break;

            case ServerEnvelopeType.RemoveFromGroup:
                await connectionRegistry.RemoveFromGroupAsync(envelope.ConnectionId!, envelope.GroupName!, ct);
                if (localTransportRegistry.Get(envelope.ConnectionId!) is not null)
                {
                    localTransportRegistry.RemoveFromGroup(envelope.ConnectionId!, envelope.GroupName!);
                }
                else
                {
                    await backplane.PublishRemoveFromGroupAsync(envelope.ConnectionId!, envelope.GroupName!, _nodeId, ct);
                }

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
}
