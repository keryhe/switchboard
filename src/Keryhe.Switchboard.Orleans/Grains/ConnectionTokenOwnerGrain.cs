using Orleans;
using Orleans.Runtime;

namespace Keryhe.Switchboard.Orleans.Grains;

[GenerateSerializer]
public sealed class ConnectionTokenOwnerState
{
    [Id(0)]
    public string? NodeId { get; set; }
}

public sealed class ConnectionTokenOwnerGrain(
    [PersistentState("connection-token-owner", SwitchboardOrleansExtensions.StorageProviderName)] IPersistentState<ConnectionTokenOwnerState> state)
    : Grain, IConnectionTokenOwnerGrain
{
    public async Task ClaimAsync(string nodeId)
    {
        state.State.NodeId = nodeId;
        await state.WriteStateAsync();
    }

    public async Task ReleaseAsync()
    {
        state.State.NodeId = null;
        await state.WriteStateAsync();
    }

    public Task<string?> GetOwnerNodeIdAsync() => Task.FromResult(state.State.NodeId);
}
