namespace Keryhe.Switchboard.Core;

/// <summary>
/// Tracks which node currently holds a <c>connectionToken</c>'s live transport (plan decision D19,
/// Phase 3 Slice 5) — keyed by <c>connectionToken</c>, not <c>connectionId</c>, and deliberately
/// independent of <c>IConnectionRegistry</c>/<c>IConnectionGrain</c>: that registration only
/// completes once the SignalR handshake itself succeeds, but the very first request that can land on
/// the wrong node is the handshake's own <c>POST</c>, arriving the instant the establishing
/// <c>GET</c> returns. <see cref="ClaimAsync"/> is therefore called at establish time — before the
/// establishing request's response is sent — not after the handshake, so ownership is resolvable
/// for every request that could possibly need it.
/// </summary>
public interface ITransportOwnershipRegistry
{
    /// <summary>Records that <paramref name="nodeId"/> now holds <paramref name="connectionToken"/>'s
    /// transport. Called once, at establish time, by whichever node's <c>GET</c> won the establish
    /// race.</summary>
    Task ClaimAsync(string connectionToken, string nodeId, CancellationToken ct);

    /// <summary>Removes the claim on transport teardown — a stale claim would otherwise forward
    /// requests for a connection that no longer exists anywhere.</summary>
    Task ReleaseAsync(string connectionToken, CancellationToken ct);

    /// <summary>Null means no node currently claims this <c>connectionToken</c> — either it has
    /// never been established, or it already tore down.</summary>
    Task<string?> GetOwnerNodeIdAsync(string connectionToken, CancellationToken ct);
}
