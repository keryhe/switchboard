using Keryhe.Switchboard.Core;

namespace Keryhe.Switchboard.Registry;

/// <summary>Phase 1: single node, nothing to fan out cross-node. Phase 3 replaces this with the Orleans observer backplane (ADR-003).</summary>
public sealed class NoOpBackplane : IBackplane
{
    public Task PublishBroadcastAsync(string hubName, byte[] payload, string hubProtocol, string[] excludedConnectionIds, CancellationToken ct) => Task.CompletedTask;
    public Task PublishGroupMessageAsync(string hubName, string groupName, byte[] payload, string hubProtocol, string[] excludedConnectionIds, CancellationToken ct) => Task.CompletedTask;
    public Task PublishUserMessageAsync(string hubName, string userId, byte[] payload, string hubProtocol, CancellationToken ct) => Task.CompletedTask;
    public Task PublishToConnectionAsync(string connectionId, byte[] payload, string hubProtocol, CancellationToken ct) => Task.CompletedTask;
}
