using Orleans;

namespace Keryhe.Switchboard.Orleans.Grains;

/// <summary>Owns one client connection's state, keyed by <c>connectionId</c> (ADR-002 grain
/// topology). Orchestrates its own membership cleanup on <see cref="UnregisterAsync"/> — removing
/// itself from every hub/group/user grain it joined — rather than pushing that fan-out onto the
/// caller, matching Orleans' own idiom of grain-to-grain calls for anything grain-local.
/// Interface and every method carry <see cref="AliasAttribute"/> so a rename is not a wire-breaking
/// change (plan decision D20) — same discipline as <c>ServerEnvelope</c>'s append-only
/// <c>[Key(n)]</c> contract.</summary>
[Alias("Keryhe.Switchboard.Orleans.Grains.IConnectionGrain")]
public interface IConnectionGrain : IGrainWithStringKey
{
    [Alias("Register")]
    Task RegisterAsync(ConnectionRecord record);

    [Alias("Get")]
    Task<ConnectionRecord?> GetAsync();

    /// <summary>Lean lookup for cross-node targeted sends (plan decision D17, Phase 3 Slice 3) —
    /// <see cref="Keryhe.Switchboard.Orleans.OrleansObserverBackplane.PublishToConnectionAsync"/>
    /// needs only the owning node and hub, not the full <see cref="ConnectionRecord"/>. Null means
    /// this connection is not currently registered (already disconnected, or never existed) — the
    /// caller logs one warning and drops the message rather than throwing.</summary>
    [Alias("GetOwnerNode")]
    Task<ConnectionLocation?> GetOwnerNodeAsync();

    [Alias("SetHubProtocol")]
    Task SetHubProtocolAsync(string hubProtocol);

    [Alias("AddToGroup")]
    Task AddToGroupAsync(string groupName);

    [Alias("RemoveFromGroup")]
    Task RemoveFromGroupAsync(string groupName);

    [Alias("Unregister")]
    Task UnregisterAsync();

    /// <summary>
    /// Same group/user cleanup and record clearing as <see cref="UnregisterAsync"/>, but
    /// deliberately never calls back into <c>IHubGrain.UnregisterConnectionAsync</c> — for use only
    /// by <c>HubGrain.UnregisterServerConnectionAsync</c> (plan decision D18, Phase 3 Slice 4), which
    /// is already the hub grain and removes the connectionId from its own state directly. Calling
    /// the ordinary <see cref="UnregisterAsync"/> from that path would call back into the same hub
    /// grain instance that is still executing the call that led here — a real call cycle, verified
    /// to deadlock (Orleans grains process one call at a time; the grain can never reach the
    /// incoming call while blocked awaiting the chain that produces it) even with the hub grain
    /// marked <c>[Reentrant]</c>.
    /// </summary>
    [Alias("UnregisterWithoutHubCallback")]
    Task UnregisterWithoutHubCallbackAsync();
}
