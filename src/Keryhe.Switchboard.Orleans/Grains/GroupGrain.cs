using Orleans;
using Orleans.Runtime;

namespace Keryhe.Switchboard.Orleans.Grains;

[GenerateSerializer]
public sealed class GroupGrainState
{
    [Id(0)]
    public HashSet<string> ConnectionIds { get; set; } = [];
}

public sealed class GroupGrain(
    [PersistentState("group", SwitchboardOrleansExtensions.StorageProviderName)] IPersistentState<GroupGrainState> state)
    : Grain, IGroupGrain
{

    public async Task AddAsync(string connectionId)
    {
        if (state.State.ConnectionIds.Add(connectionId))
        {
            await state.WriteStateAsync();
        }
    }

    public async Task RemoveAsync(string connectionId)
    {
        if (state.State.ConnectionIds.Remove(connectionId))
        {
            await state.WriteStateAsync();
        }
    }

    public Task<List<string>> GetMembersAsync() => Task.FromResult(state.State.ConnectionIds.ToList());
}
