using Orleans;
using Orleans.Runtime;

namespace Keryhe.Switchboard.Orleans.Grains;

[GenerateSerializer]
public sealed class UserGrainState
{
    [Id(0)]
    public HashSet<string> ConnectionIds { get; set; } = [];
}

public sealed class UserGrain(
    [PersistentState("user", SwitchboardOrleansExtensions.StorageProviderName)] IPersistentState<UserGrainState> state)
    : Grain, IUserGrain
{

    public async Task AddConnectionAsync(string connectionId)
    {
        if (state.State.ConnectionIds.Add(connectionId))
        {
            await state.WriteStateAsync();
        }
    }

    public async Task RemoveConnectionAsync(string connectionId)
    {
        if (state.State.ConnectionIds.Remove(connectionId))
        {
            await state.WriteStateAsync();
        }
    }

    public Task<List<string>> GetConnectionIdsAsync() => Task.FromResult(state.State.ConnectionIds.ToList());
}
