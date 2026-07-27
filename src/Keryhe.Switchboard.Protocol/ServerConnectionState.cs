namespace Keryhe.Switchboard.Protocol;

public enum ServerConnectionStatus { Connected, Degraded, Reconnecting, Disconnected }

/// <summary>Represents a physical WebSocket connection from an app server to this service.</summary>
public sealed class ServerConnectionState
{
    public required string ConnectionId { get; init; }
    public required string HubName { get; init; }
    public required string AppServerId { get; init; }

    public required IServerConnection Connection { get; init; }

    public int LogicalConnectionCount => _logicalCount;
    private int _logicalCount;
    public void IncrementLogicalCount() => Interlocked.Increment(ref _logicalCount);
    public void DecrementLogicalCount() => Interlocked.Decrement(ref _logicalCount);

    public ServerConnectionStatus Status { get; set; } = ServerConnectionStatus.Connected;
    public required DateTimeOffset ConnectedAt { get; init; }
    public DateTimeOffset LastPingSent { get; set; }
    public DateTimeOffset LastPongReceived { get; set; }
}
