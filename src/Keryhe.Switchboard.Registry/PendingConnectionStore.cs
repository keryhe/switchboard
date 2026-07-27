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
/// </summary>
public interface IPendingConnectionStore
{
    void Add(PendingConnection pending);

    /// <summary>Removes and returns the entry if present and not expired; otherwise returns null (including for expired-but-not-yet-reaped entries).</summary>
    PendingConnection? TryConsume(string connectionToken);

    /// <summary>Evicts all expired entries. Intended to be called periodically by a background reaper.</summary>
    void ReapExpired();
}

public sealed class InMemoryPendingConnectionStore(TimeProvider timeProvider) : IPendingConnectionStore
{
    private readonly ConcurrentDictionary<string, PendingConnection> _pending = new();

    public InMemoryPendingConnectionStore() : this(TimeProvider.System)
    {
    }

    public void Add(PendingConnection pending) => _pending[pending.ConnectionToken] = pending;

    public PendingConnection? TryConsume(string connectionToken)
    {
        if (!_pending.TryRemove(connectionToken, out var pending))
        {
            return null;
        }

        return pending.ExpiresAt > timeProvider.GetUtcNow() ? pending : null;
    }

    public void ReapExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (token, pending) in _pending)
        {
            if (pending.ExpiresAt <= now)
            {
                _pending.TryRemove(token, out _);
            }
        }
    }
}
