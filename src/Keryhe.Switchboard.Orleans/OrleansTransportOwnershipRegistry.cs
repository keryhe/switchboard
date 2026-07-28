using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Orleans.Grains;
using Orleans;

namespace Keryhe.Switchboard.Orleans;

/// <summary>Orleans-backed <see cref="ITransportOwnershipRegistry"/> (plan decision D19, Phase 3
/// Slice 5) — delegates to <see cref="IConnectionTokenOwnerGrain"/>, keyed by
/// <c>connectionToken</c>.</summary>
public sealed class OrleansTransportOwnershipRegistry(IGrainFactory grainFactory) : ITransportOwnershipRegistry
{
    public Task ClaimAsync(string connectionToken, string nodeId, CancellationToken ct) =>
        grainFactory.GetGrain<IConnectionTokenOwnerGrain>(connectionToken).ClaimAsync(nodeId);

    public Task ReleaseAsync(string connectionToken, CancellationToken ct) =>
        grainFactory.GetGrain<IConnectionTokenOwnerGrain>(connectionToken).ReleaseAsync();

    public Task<string?> GetOwnerNodeIdAsync(string connectionToken, CancellationToken ct) =>
        grainFactory.GetGrain<IConnectionTokenOwnerGrain>(connectionToken).GetOwnerNodeIdAsync();
}
