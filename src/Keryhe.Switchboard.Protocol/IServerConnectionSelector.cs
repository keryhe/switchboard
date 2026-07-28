namespace Keryhe.Switchboard.Protocol;

/// <summary>
/// Assigns a client connection to a server connection. Async and node-qualified-string-returning
/// as of Phase 3 Slice 4 (plan decision D18) — assignment is cluster-wide least-loaded, so the
/// picked connection is no longer guaranteed to be local, and there is no live
/// <see cref="ServerConnectionState"/> object to hand back for one that isn't (a live
/// <see cref="IServerConnection"/> is only ever meaningful on the node that physically owns the
/// socket). The returned string is <see cref="Core.ServerConnectionRef"/>-formatted; callers that
/// need the live object look it up locally via <see cref="IHubRegistry.GetHub"/> only after
/// confirming the node part matches their own.
/// </summary>
public interface IServerConnectionSelector
{
    /// <summary>Increments the assignment's logical count atomically with the pick — null if no
    /// server connection is reachable from <paramref name="requestingNodeId"/> anywhere in the
    /// cluster (negotiate/connect 503). "Reachable" excludes a connection on a node with no active
    /// backplane subscription right now, even if the connection itself is registered — otherwise
    /// the assignment would be handed out, then silently undeliverable (plan decision D18).</summary>
    Task<string?> AssignConnectionAsync(string hubName, string requestingNodeId, CancellationToken ct);

    /// <summary>Decrements the assignment's logical count. A no-op if the reference is malformed or
    /// already gone — release is best-effort cleanup, not a correctness-bearing operation.</summary>
    Task ReleaseConnectionAsync(string hubName, string serverConnectionRef, CancellationToken ct);
}
