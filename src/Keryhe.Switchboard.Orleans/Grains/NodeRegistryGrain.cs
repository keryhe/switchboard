using Orleans;
using Orleans.Runtime;

namespace Keryhe.Switchboard.Orleans.Grains;

[GenerateSerializer]
public sealed class NodeRegistryState
{
    [Id(0)]
    public Dictionary<string, string> InternalUrlsByNodeId { get; init; } = new();
}

public sealed class NodeRegistryGrain(
    [PersistentState("node-registry", SwitchboardOrleansExtensions.StorageProviderName)] IPersistentState<NodeRegistryState> state)
    : Grain, INodeRegistryGrain
{
    public async Task RegisterAsync(string nodeId, string internalUrl)
    {
        state.State.InternalUrlsByNodeId[nodeId] = internalUrl;
        await state.WriteStateAsync();
    }

    public async Task UnregisterAsync(string nodeId)
    {
        if (state.State.InternalUrlsByNodeId.Remove(nodeId))
        {
            await state.WriteStateAsync();
        }
    }

    public Task<string?> GetInternalUrlAsync(string nodeId) =>
        Task.FromResult(state.State.InternalUrlsByNodeId.GetValueOrDefault(nodeId));
}
