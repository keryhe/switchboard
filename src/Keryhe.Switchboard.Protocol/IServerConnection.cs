namespace Keryhe.Switchboard.Protocol;

/// <summary>A physical WebSocket connection from an app server, carrying multiplexed <see cref="Protocol.ServerEnvelope"/> traffic.</summary>
public interface IServerConnection
{
    string ConnectionId { get; }
    string HubName { get; }
    int LogicalConnectionCount { get; }
    ValueTask SendAsync(ServerEnvelope envelope, CancellationToken ct);
    IAsyncEnumerable<ServerEnvelope> ReadAllAsync(CancellationToken ct);
}
