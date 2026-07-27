using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;

namespace Keryhe.Switchboard.Server.Routing;

/// <summary>
/// Central dispatch engine (04-design.md §5). Phase 1 fully implements client-message routing,
/// targeted send, and broadcast. Group/user fan-out (<see cref="SendToGroupAsync"/>,
/// <see cref="SendToUserAsync"/>) is deliberately a log-and-no-op in Phase 1 — the wire format is
/// exercised by the Connector (plan decision D3), but real routing to group/user members is a
/// Phase 2 deliverable. This is a scope decision, not an oversight: implementing it now would be
/// easy given <see cref="IConnectionRegistry"/> already tracks membership, but the plan explicitly
/// defers it to bound Phase 1's scope.
/// </summary>
public sealed class DefaultMessageRouter(
    IConnectionRegistry connectionRegistry,
    IHubRegistry hubRegistry,
    ILocalTransportRegistry localTransportRegistry,
    ILogger<DefaultMessageRouter> logger) : IMessageRouter
{
    public async ValueTask RouteClientMessageAsync(string connectionId, ReadOnlyMemory<byte> payload, string hubProtocol, CancellationToken ct)
    {
        var state = await connectionRegistry.GetAsync(connectionId, ct);
        if (state is null)
        {
            logger.LogWarning("RouteClientMessageAsync: unknown connection {ConnectionId}.", connectionId);
            return;
        }

        var serverConnection = hubRegistry.GetHub(state.HubName)?.ServerConnections.GetValueOrDefault(state.ServerConnectionId)?.Connection;
        if (serverConnection is null)
        {
            logger.LogWarning("RouteClientMessageAsync: server connection {ServerConnectionId} for {ConnectionId} is gone.", state.ServerConnectionId, connectionId);
            return;
        }

        await serverConnection.SendAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.ClientMessage,
            ConnectionId = connectionId,
            HubProtocol = hubProtocol,
            Payload = payload.ToArray(),
        }, ct);
    }

    public async ValueTask RouteToConnectionAsync(string connectionId, ReadOnlyMemory<byte> payload, string hubProtocol, CancellationToken ct)
    {
        var transport = localTransportRegistry.Get(connectionId);
        if (transport is null)
        {
            logger.LogWarning("RouteToConnectionAsync: no local transport for {ConnectionId} (may have disconnected or live on another node).", connectionId);
            return;
        }

        await transport.Output.Writer.WriteAsync(payload, ct);
    }

    public async ValueTask BroadcastAsync(string hubName, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlySet<string>? excludedConnectionIds, CancellationToken ct)
    {
        await foreach (var state in connectionRegistry.GetAllAsync(hubName, ct))
        {
            if (excludedConnectionIds?.Contains(state.ConnectionId) == true)
            {
                continue;
            }

            var transport = localTransportRegistry.Get(state.ConnectionId);
            if (transport is not null)
            {
                await transport.Output.Writer.WriteAsync(payload, ct);
            }
        }
    }

    public ValueTask SendToGroupAsync(string hubName, string groupName, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlySet<string>? excludedConnectionIds, CancellationToken ct)
    {
        logger.LogWarning(
            "SendToGroupAsync for group '{GroupName}' on hub '{HubName}' received but not routed: group/user fan-out is a Phase 2 deliverable (plan decision D3).",
            groupName, hubName);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendToUserAsync(string hubName, string userId, ReadOnlyMemory<byte> payload, string hubProtocol, CancellationToken ct)
    {
        logger.LogWarning(
            "SendToUserAsync for user '{UserId}' on hub '{HubName}' received but not routed: group/user fan-out is a Phase 2 deliverable (plan decision D3).",
            userId, hubName);
        return ValueTask.CompletedTask;
    }
}
