using Keryhe.Switchboard.Connector.ServerConnections;
using Keryhe.Switchboard.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace Keryhe.Switchboard.Connector;

/// <summary>
/// Outbound-only half of the Connector (04-design.md §8/§11): every <c>Clients.*</c> call becomes
/// an envelope sent to the service over the hub's <see cref="ConnectorConnectionPool"/>. Inbound
/// dispatch (<c>OnConnectedAsync</c>, argument binding, streaming, ...) runs over the synthetic
/// connection pipeline instead — see <see cref="Dispatch.InboundDispatcher"/> — and never goes
/// through this type. Group/user sends are emitted here per plan decision D3 even though the
/// service does not yet route them (that's a Phase 2 deliverable); emitting now exercises the wire
/// format so Phase 2 is a service-side-only change.
/// </summary>
public sealed class SwitchboardHubLifetimeManager<THub>(
    ConnectorConnectionPoolRegistry poolRegistry,
    HubRouteNameRegistry hubRouteNameRegistry) : HubLifetimeManager<THub> where THub : Hub
{
    private static readonly IHubProtocol JsonProtocol = new JsonHubProtocol();

    private string HubName => hubRouteNameRegistry.GetName(typeof(THub));

    public override Task OnConnectedAsync(HubConnectionContext connection) => Task.CompletedTask;

    public override Task OnDisconnectedAsync(HubConnectionContext connection) => Task.CompletedTask;

    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.Broadcast,
            HubName = HubName,
            HubProtocol = "json",
            Payload = BuildFrame(methodName, args),
        }, cancellationToken);

    public override Task SendAllExceptAsync(string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.Broadcast,
            HubName = HubName,
            HubProtocol = "json",
            Payload = BuildFrame(methodName, args),
            ExcludedConnectionIds = excludedConnectionIds,
        }, cancellationToken);

    public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.SendToConnection,
            ConnectionId = connectionId,
            HubProtocol = "json",
            Payload = BuildFrame(methodName, args),
        }, cancellationToken);

    public override async Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        var frame = BuildFrame(methodName, args);
        foreach (var connectionId in connectionIds)
        {
            await SendEnvelopeAsync(new ServerEnvelope
            {
                Type = ServerEnvelopeType.SendToConnection,
                ConnectionId = connectionId,
                HubProtocol = "json",
                Payload = frame,
            }, cancellationToken);
        }
    }

    public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.SendToGroup,
            HubName = HubName,
            GroupName = groupName,
            HubProtocol = "json",
            Payload = BuildFrame(methodName, args),
        }, cancellationToken);

    public override async Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        var frame = BuildFrame(methodName, args);
        foreach (var groupName in groupNames)
        {
            await SendEnvelopeAsync(new ServerEnvelope
            {
                Type = ServerEnvelopeType.SendToGroup,
                HubName = HubName,
                GroupName = groupName,
                HubProtocol = "json",
                Payload = frame,
            }, cancellationToken);
        }
    }

    public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.SendToGroup,
            HubName = HubName,
            GroupName = groupName,
            HubProtocol = "json",
            Payload = BuildFrame(methodName, args),
            ExcludedConnectionIds = excludedConnectionIds,
        }, cancellationToken);

    public override Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.SendToUser,
            HubName = HubName,
            UserId = userId,
            HubProtocol = "json",
            Payload = BuildFrame(methodName, args),
        }, cancellationToken);

    public override async Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        var frame = BuildFrame(methodName, args);
        foreach (var userId in userIds)
        {
            await SendEnvelopeAsync(new ServerEnvelope
            {
                Type = ServerEnvelopeType.SendToUser,
                HubName = HubName,
                UserId = userId,
                HubProtocol = "json",
                Payload = frame,
            }, cancellationToken);
        }
    }

    public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.AddToGroup,
            ConnectionId = connectionId,
            GroupName = groupName,
        }, cancellationToken);

    public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.RemoveFromGroup,
            ConnectionId = connectionId,
            GroupName = groupName,
        }, cancellationToken);

    private Task SendEnvelopeAsync(ServerEnvelope envelope, CancellationToken ct) =>
        poolRegistry.SendAsync(HubName, envelope, ct).AsTask();

    private static byte[] BuildFrame(string methodName, object?[] args)
    {
        var message = new InvocationMessage(methodName, args);
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        JsonProtocol.WriteMessage(message, writer);
        return writer.WrittenMemory.ToArray();
    }
}
