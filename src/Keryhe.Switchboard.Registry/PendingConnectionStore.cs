using System.Collections.Concurrent;

namespace Keryhe.Switchboard.Registry;

public sealed record PendingConnection(
    string ConnectionToken,
    string ConnectionId,
    string HubName,
    string? UserId,
    IReadOnlyDictionary<string, string>? Claims,
    DateTimeOffset ExpiresAt);

/// <summary>
/// TTL'd map from the connectionToken minted at step-2 negotiate to the pending connection it
/// identifies, bridging the gap between negotiate and the transport upgrade (see plan D4). A
/// transport upgrade presenting an unknown or expired token must get 404/401, never a new
/// connection, so entries are removed on first (successful) consumption and reaped on expiry.
/// Async — not because the in-memory implementation needs it, but because the Orleans
/// implementation (plan decision D19, Phase 3 Slice 1) is grain-backed and a synchronous facade
/// over a grain call is a sync-over-async deadlock risk in request-handling code. Mirrors
/// <see cref="IConnectionRegistry"/>'s own "async from day one" posture (ADR-002) for the same reason.
/// </summary>
public interface IPendingConnectionStore
{
    Task AddAsync(PendingConnection pending, CancellationToken ct = default);

    /// <summary>Removes and returns the entry if present and not expired; otherwise returns null (including for expired-but-not-yet-reaped entries).</summary>
    Task<PendingConnection?> TryConsumeAsync(string connectionToken, CancellationToken ct = default);

    /// <summary>Evicts all expired entries. Intended to be called periodically by a background
    /// reaper. Returns the number reaped, so the caller can record
    /// <c>signalr.pending_connections.expired</c> (Phase 4 plan decision D28) without this store
    /// needing an OpenTelemetry/metrics dependency of its own.</summary>
    Task<int> ReapExpiredAsync(CancellationToken ct = default);
}

public sealed class InMemoryPendingConnectionStore(TimeProvider timeProvider) : IPendingConnectionStore
{
    private readonly ConcurrentDictionary<string, PendingConnection> _pending = new();

    public InMemoryPendingConnectionStore() : this(TimeProvider.System)
    {
    }

    public Task AddAsync(PendingConnection pending, CancellationToken ct = default)
    {
        _pending[pending.ConnectionToken] = pending;
        return Task.CompletedTask;
    }

    public Task<PendingConnection?> TryConsumeAsync(string connectionToken, CancellationToken ct = default)
    {
        if (!_pending.TryRemove(connectionToken, out var pending))
        {
            return Task.FromResult<PendingConnection?>(null);
        }

        return Task.FromResult(pending.ExpiresAt > timeProvider.GetUtcNow() ? pending : null);
    }

    public Task<int> ReapExpiredAsync(CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        var reaped = 0;
        foreach (var (token, pending) in _pending)
        {
            if (pending.ExpiresAt <= now && _pending.TryRemove(token, out _))
            {
                reaped++;
            }
        }

        return Task.FromResult(reaped);
    }
}
