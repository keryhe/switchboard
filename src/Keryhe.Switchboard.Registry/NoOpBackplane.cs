using Keryhe.Switchboard.Core;

namespace Keryhe.Switchboard.Registry;

/// <summary>Phase 1/2: single node, nothing to fan out cross-node — every <c>DefaultMessageRouter</c>
/// call site already delivers to every local target before calling this (plan decision D14), so a
/// no-op here is behavior-preserving. Phase 3 replaces this with the Orleans observer backplane
/// (ADR-003).</summary>
public sealed class NoOpBackplane : IBackplane
{
    public Task PublishBroadcastAsync(string hubName, byte[] payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds, string originNodeId, CancellationToken ct) => Task.CompletedTask;
    public Task PublishGroupMessageAsync(string hubName, string groupName, byte[] payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds, string originNodeId, CancellationToken ct) => Task.CompletedTask;
    public Task PublishUserMessageAsync(string hubName, string userId, byte[] payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, string originNodeId, CancellationToken ct) => Task.CompletedTask;
    public Task PublishToConnectionAsync(string connectionId, byte[] payload, string hubProtocol, string originNodeId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Never actually reachable in single-node mode: <c>RoundRobinServerConnectionSelector</c>
    /// only ever assigns local server connections, so <c>ClientConnectionLifecycle</c>'s
    /// local-vs-remote check always takes the local branch.</summary>
    public Task PublishServerEnvelopeAsync(string hubName, string serverConnectionRef, byte[] serializedEnvelope, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Never actually reachable in single-node mode — see <see cref="PublishServerEnvelopeAsync"/>.</summary>
    public Task PublishCloseConnectionAsync(string connectionId, string? error, bool allowReconnect, string originNodeId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Never actually reachable in single-node mode — see <see cref="PublishServerEnvelopeAsync"/>.</summary>
    public Task PublishAddToGroupAsync(string connectionId, string groupName, string originNodeId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Never actually reachable in single-node mode — see <see cref="PublishServerEnvelopeAsync"/>.</summary>
    public Task PublishRemoveFromGroupAsync(string connectionId, string groupName, string originNodeId, CancellationToken ct) => Task.CompletedTask;
}
