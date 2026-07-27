namespace Keryhe.Switchboard.Core;

/// <summary>Cross-node fan-out. Phase 1: <c>NoOpBackplane</c> (single node). Phase 3: Orleans grain observers (ADR-003).</summary>
public interface IBackplane
{
    Task PublishBroadcastAsync(string hubName, byte[] payload, string hubProtocol, string[] excludedConnectionIds, CancellationToken ct);
    Task PublishGroupMessageAsync(string hubName, string groupName, byte[] payload, string hubProtocol, string[] excludedConnectionIds, CancellationToken ct);
    Task PublishUserMessageAsync(string hubName, string userId, byte[] payload, string hubProtocol, CancellationToken ct);
    Task PublishToConnectionAsync(string connectionId, byte[] payload, string hubProtocol, CancellationToken ct);
}
