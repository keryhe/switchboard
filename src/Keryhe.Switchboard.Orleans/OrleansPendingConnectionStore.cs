using Keryhe.Switchboard.Orleans.Grains;
using Keryhe.Switchboard.Registry;
using Orleans;

namespace Keryhe.Switchboard.Orleans;

/// <summary>
/// Orleans-backed <see cref="IPendingConnectionStore"/> (plan decision D19) — the state
/// substitution that lets step-2 negotiate and the transport upgrade that consumes its token land
/// on different nodes. Grain-keyed by <c>connectionToken</c> directly, so no separate index is
/// needed to find it.
/// </summary>
public sealed class OrleansPendingConnectionStore(IGrainFactory grainFactory, TimeProvider timeProvider) : IPendingConnectionStore
{
    public Task AddAsync(PendingConnection pending, CancellationToken ct = default)
    {
        var record = new PendingConnectionRecord
        {
            ConnectionId = pending.ConnectionId,
            HubName = pending.HubName,
            UserId = pending.UserId,
            Claims = pending.Claims is null ? null : new Dictionary<string, string>(pending.Claims),
            ExpiresAt = pending.ExpiresAt,
        };

        return grainFactory.GetGrain<IPendingConnectionGrain>(pending.ConnectionToken).AddAsync(record);
    }

    public async Task<PendingConnection?> TryConsumeAsync(string connectionToken, CancellationToken ct = default)
    {
        var record = await grainFactory.GetGrain<IPendingConnectionGrain>(connectionToken).TryConsumeAsync(timeProvider.GetUtcNow());
        if (record is null)
        {
            return null;
        }

        return new PendingConnection(connectionToken, record.ConnectionId, record.HubName, record.UserId, record.Claims, record.ExpiresAt);
    }

    /// <summary>
    /// No-op under Orleans, deliberately. Unlike the in-memory store, there is no efficient
    /// "enumerate every outstanding grain" primitive to sweep — and none is needed for correctness:
    /// <see cref="TryConsumeAsync"/> already treats an expired entry as absent regardless of whether
    /// anything has swept it. The in-memory reaper exists to bound process memory; an Orleans grain
    /// under memory storage is bounded by activation lifetime already, and durable-storage cleanup
    /// for the ADO.NET providers (Phase 3 Slice 6) is a real but separate concern — a SQL-level
    /// expiry sweep, not a per-grain scan — deferred to that slice rather than guessed at here.
    /// </summary>
    public Task ReapExpiredAsync(CancellationToken ct = default) => Task.CompletedTask;
}
