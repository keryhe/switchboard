using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Orleans.Grains;
using Orleans;

namespace Keryhe.Switchboard.Orleans;

/// <summary>Orleans-backed <see cref="INodeAddressResolver"/> (plan decision D19, Phase 3 Slice 5) —
/// delegates to <see cref="INodeRegistryGrain"/>, which <see cref="NodeRegistryPublisherService"/>
/// keeps populated with every live node's <c>InternalUrl</c>.</summary>
public sealed class OrleansNodeAddressResolver(IGrainFactory grainFactory) : INodeAddressResolver
{
    public Task<string?> GetInternalUrlAsync(string nodeId, CancellationToken ct) =>
        grainFactory.GetGrain<INodeRegistryGrain>(NodeRegistryGrainKey.Value).GetInternalUrlAsync(nodeId);
}
