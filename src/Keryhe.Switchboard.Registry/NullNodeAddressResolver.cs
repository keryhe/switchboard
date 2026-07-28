using Keryhe.Switchboard.Core;

namespace Keryhe.Switchboard.Registry;

/// <summary>Single-node <see cref="INodeAddressResolver"/> (plan decision D19, Phase 3 Slice 5) —
/// never consulted in practice, since <see cref="LocalConnectionOwnershipResolver"/> never reports a
/// remote owner for the forward hop to look up an address for. Used when
/// <c>UseOrleansCluster = false</c>.</summary>
public sealed class NullNodeAddressResolver : INodeAddressResolver
{
    public Task<string?> GetInternalUrlAsync(string nodeId, CancellationToken ct) => Task.FromResult<string?>(null);
}
