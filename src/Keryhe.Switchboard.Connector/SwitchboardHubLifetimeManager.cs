using Keryhe.Switchboard.Connector.Dispatch;
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
/// through this type.
/// </summary>
/// <remarks>
/// Mixed-protocol fan-out (plan decision D7): a broadcast/group/user send's recipient set may mix
/// JSON and MessagePack clients, and the Connector cannot know that mix in advance, so
/// <see cref="ServerEnvelope.Payloads"/> carries one correctly-encoded copy per protocol the
/// service supports — the service picks the entry matching each target's own negotiated
/// protocol. Targeted sends (<c>Clients.Client</c>/<c>Clients.Caller</c>/<c>Clients.Clients</c>)
/// address exactly one known connection, so they look its protocol up via
/// <see cref="InboundDispatcher.GetHubProtocol"/> and send a single correctly-encoded payload
/// instead.
/// </remarks>
public sealed class SwitchboardHubLifetimeManager<THub>(
    ConnectorConnectionPoolRegistry poolRegistry,
    HubRouteNameRegistry hubRouteNameRegistry,
    InboundDispatcher inboundDispatcher) : HubLifetimeManager<THub> where THub : Hub
{
    private static readonly IHubProtocol Json = new JsonHubProtocol();
    private static readonly IHubProtocol MessagePack = new MessagePackHubProtocol();

    private string HubName => hubRouteNameRegistry.GetName(typeof(THub));

    public override Task OnConnectedAsync(HubConnectionContext connection) => Task.CompletedTask;

    public override Task OnDisconnectedAsync(HubConnectionContext connection) => Task.CompletedTask;

    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.Broadcast,
            HubName = HubName,
            HubProtocol = "json",
            Payload = BuildFrame(Json, methodName, args),
            Payloads = BuildAllProtocolFrames(methodName, args),
        }, cancellationToken);

    public override Task SendAllExceptAsync(string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.Broadcast,
            HubName = HubName,
            HubProtocol = "json",
            Payload = BuildFrame(Json, methodName, args),
            Payloads = BuildAllProtocolFrames(methodName, args),
            ExcludedConnectionIds = excludedConnectionIds,
        }, cancellationToken);

    public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        var protocolName = inboundDispatcher.GetHubProtocol(connectionId) ?? "json";
        return SendEnvelopeAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.SendToConnection,
            ConnectionId = connectionId,
            HubProtocol = protocolName,
            Payload = BuildFrame(Resolve(protocolName), methodName, args),
        }, cancellationToken);
    }

    public override async Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        foreach (var connectionId in connectionIds)
        {
            await SendConnectionAsync(connectionId, methodName, args, cancellationToken);
        }
    }

    public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.SendToGroup,
            HubName = HubName,
            GroupName = groupName,
            HubProtocol = "json",
            Payload = BuildFrame(Json, methodName, args),
            Payloads = BuildAllProtocolFrames(methodName, args),
        }, cancellationToken);

    public override async Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        var payloads = BuildAllProtocolFrames(methodName, args);
        foreach (var groupName in groupNames)
        {
            await SendEnvelopeAsync(new ServerEnvelope
            {
                Type = ServerEnvelopeType.SendToGroup,
                HubName = HubName,
                GroupName = groupName,
                HubProtocol = "json",
                Payload = payloads["json"],
                Payloads = payloads,
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
            Payload = BuildFrame(Json, methodName, args),
            Payloads = BuildAllProtocolFrames(methodName, args),
            ExcludedConnectionIds = excludedConnectionIds,
        }, cancellationToken);

    public override Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.SendToUser,
            HubName = HubName,
            UserId = userId,
            HubProtocol = "json",
            Payload = BuildFrame(Json, methodName, args),
            Payloads = BuildAllProtocolFrames(methodName, args),
        }, cancellationToken);

    public override async Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        var payloads = BuildAllProtocolFrames(methodName, args);
        foreach (var userId in userIds)
        {
            await SendEnvelopeAsync(new ServerEnvelope
            {
                Type = ServerEnvelopeType.SendToUser,
                HubName = HubName,
                UserId = userId,
                HubProtocol = "json",
                Payload = payloads["json"],
                Payloads = payloads,
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

    private static IReadOnlyDictionary<string, byte[]> BuildAllProtocolFrames(string methodName, object?[] args) =>
        new Dictionary<string, byte[]>
        {
            ["json"] = BuildFrame(Json, methodName, args),
            ["messagepack"] = BuildFrame(MessagePack, methodName, args),
        };

    private static IHubProtocol Resolve(string hubProtocol) => hubProtocol switch
    {
        "messagepack" => MessagePack,
        _ => Json,
    };

    private static byte[] BuildFrame(IHubProtocol protocol, string methodName, object?[] args)
    {
        var message = new InvocationMessage(methodName, args);
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        protocol.WriteMessage(message, writer);
        return writer.WrittenMemory.ToArray();
    }
}
