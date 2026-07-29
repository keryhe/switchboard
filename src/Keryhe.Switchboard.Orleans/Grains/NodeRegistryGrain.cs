using Orleans;
using Orleans.Runtime;

namespace Keryhe.Switchboard.Orleans.Grains;

[GenerateSerializer]
public sealed class NodeRegistryState
{
    [Id(0)]
    public Dictionary<string, string> InternalUrlsByNodeId { get; init; } = new();

    /// <summary>Phase 4 plan decision D27, finding 5 — each node's own known hub names (union of
    /// its <c>IHubRegistry.GetAllHubs()</c> and <c>ILocalTransportRegistry.GetKnownHubNames()</c>,
    /// same set <c>ObserverHeartbeatService</c> already computes for its own subscribe pass),
    /// republished alongside <see cref="InternalUrlsByNodeId"/> so the cluster-wide hub directory
    /// exists even for a node that has no <c>InternalUrl</c> configured.</summary>
    [Id(1)]
    public Dictionary<string, List<string>> HubNamesByNodeId { get; init; } = new();
}

public sealed class NodeRegistryGrain(
    [PersistentState("node-registry", SwitchboardOrleansExtensions.StorageProviderName)] IPersistentState<NodeRegistryState> state)
    : Grain, INodeRegistryGrain
{
    public async Task RegisterAsync(string nodeId, string? internalUrl, IReadOnlyList<string> hubNames)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(internalUrl))
        {
            changed |= state.State.InternalUrlsByNodeId.Remove(nodeId);
        }
        else if (!state.State.InternalUrlsByNodeId.TryGetValue(nodeId, out var existingUrl) || existingUrl != internalUrl)
        {
            state.State.InternalUrlsByNodeId[nodeId] = internalUrl;
            changed = true;
        }

        var hubNamesList = hubNames.ToList();
        if (!state.State.HubNamesByNodeId.TryGetValue(nodeId, out var existingHubs) || !existingHubs.SequenceEqual(hubNamesList))
        {
            state.State.HubNamesByNodeId[nodeId] = hubNamesList;
            changed = true;
        }

        if (changed)
        {
            await state.WriteStateAsync();
        }
    }

    public async Task UnregisterAsync(string nodeId)
    {
        var changed = state.State.InternalUrlsByNodeId.Remove(nodeId);
        changed |= state.State.HubNamesByNodeId.Remove(nodeId);

        if (changed)
        {
            await state.WriteStateAsync();
        }
    }

    public Task<string?> GetInternalUrlAsync(string nodeId) =>
        Task.FromResult(state.State.InternalUrlsByNodeId.GetValueOrDefault(nodeId));

    public Task<IReadOnlyList<string>> GetAllNodesAsync() =>
        Task.FromResult<IReadOnlyList<string>>(
            state.State.InternalUrlsByNodeId.Keys.Union(state.State.HubNamesByNodeId.Keys).ToList());

    public Task<IReadOnlyList<string>> GetAllHubNamesAsync() =>
        Task.FromResult<IReadOnlyList<string>>(
            state.State.HubNamesByNodeId.Values.SelectMany(names => names).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList());
}
