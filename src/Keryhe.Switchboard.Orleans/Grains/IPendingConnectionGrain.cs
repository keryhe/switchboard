using Orleans;

namespace Keryhe.Switchboard.Orleans.Grains;

/// <summary>
/// TTL'd, one-shot pending-connection entry keyed by <c>connectionToken</c> (plan decision D19) —
/// the Orleans backing for <c>Keryhe.Switchboard.Registry.IPendingConnectionStore</c>. Without this,
/// a clustered deployment 401s on connect: step-2 negotiate mints the token on whichever node
/// receives it, and the transport upgrade that consumes it can legitimately land on a different
/// node behind the same load balancer.
/// </summary>
[Alias("Keryhe.Switchboard.Orleans.Grains.IPendingConnectionGrain")]
public interface IPendingConnectionGrain : IGrainWithStringKey
{
    [Alias("Add")]
    Task AddAsync(PendingConnectionRecord record);

    /// <summary>Removes and returns the entry if present and not expired; otherwise returns null.
    /// One-shot by design — the entire security value of <c>connectionToken</c> depends on a
    /// second consumption attempt never succeeding.</summary>
    [Alias("TryConsume")]
    Task<PendingConnectionRecord?> TryConsumeAsync(DateTimeOffset now);
}
