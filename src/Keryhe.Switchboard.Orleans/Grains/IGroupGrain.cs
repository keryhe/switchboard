using Orleans;

namespace Keryhe.Switchboard.Orleans.Grains;

/// <summary>Authoritative member set for one group, keyed by <c>"hubName::groupName"</c> (ADR-002
/// grain topology). Consulted for management queries and disconnect cleanup — never on the fan-out
/// path, which publishes by group name and lets each node resolve membership from its own local
/// index (plan decision D17, Phase 3 Slice 3).</summary>
[Alias("Keryhe.Switchboard.Orleans.Grains.IGroupGrain")]
public interface IGroupGrain : IGrainWithStringKey
{
    [Alias("Add")]
    Task AddAsync(string connectionId);

    [Alias("Remove")]
    Task RemoveAsync(string connectionId);

    [Alias("GetMembers")]
    Task<List<string>> GetMembersAsync();
}
