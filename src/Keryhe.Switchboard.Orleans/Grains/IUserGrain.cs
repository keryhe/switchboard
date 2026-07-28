using Orleans;

namespace Keryhe.Switchboard.Orleans.Grains;

/// <summary>Authoritative connection set for one user, keyed by <c>"hubName::userId"</c> (ADR-002
/// grain topology). Same "consulted for queries and cleanup, never for fan-out" posture as
/// <see cref="IGroupGrain"/> (plan decision D17).</summary>
[Alias("Keryhe.Switchboard.Orleans.Grains.IUserGrain")]
public interface IUserGrain : IGrainWithStringKey
{
    [Alias("AddConnection")]
    Task AddConnectionAsync(string connectionId);

    [Alias("RemoveConnection")]
    Task RemoveConnectionAsync(string connectionId);

    [Alias("GetConnectionIds")]
    Task<List<string>> GetConnectionIdsAsync();
}
