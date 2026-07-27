using System.Text;
using System.Text.Json;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;

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
    IHubRegistry hubRegistry,
    ILogger<RoutingServerEnvelopeDispatcher> logger) : IServerEnvelopeDispatcher
{
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
                    envelope.ExcludedConnectionIds?.ToHashSet(),
                    ct);
                break;

            case ServerEnvelopeType.SendToGroup:
                await router.SendToGroupAsync(
                    envelope.HubName!,
                    envelope.GroupName!,
                    envelope.Payload!,
                    envelope.HubProtocol!,
                    envelope.ExcludedConnectionIds?.ToHashSet(),
                    ct);
                break;

            case ServerEnvelopeType.SendToUser:
                await router.SendToUserAsync(envelope.HubName!, envelope.UserId!, envelope.Payload!, envelope.HubProtocol!, ct);
                break;

            case ServerEnvelopeType.AddToGroup:
                await connectionRegistry.AddToGroupAsync(envelope.ConnectionId!, envelope.GroupName!, ct);
                break;

            case ServerEnvelopeType.RemoveFromGroup:
                await connectionRegistry.RemoveFromGroupAsync(envelope.ConnectionId!, envelope.GroupName!, ct);
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
        if (transport is not null)
        {
            var closeFrame = JsonSerializer.SerializeToUtf8Bytes(new { type = 7, error });
            var framed = new System.Buffers.ArrayBufferWriter<byte>();
            JsonFrameProtocol.WriteFrame(framed, closeFrame);
            await transport.Output.Writer.WriteAsync(framed.WrittenMemory, ct);
            await transport.CloseAsync(error);
        }

        var state = await connectionRegistry.GetAsync(connectionId, ct);
        if (state is not null)
        {
            hubRegistry.GetHub(state.HubName)?.ServerConnections.GetValueOrDefault(state.ServerConnectionId)?.DecrementLogicalCount();
        }

        await connectionRegistry.UnregisterAsync(connectionId, ct);
        localTransportRegistry.Unregister(connectionId);
    }
}
