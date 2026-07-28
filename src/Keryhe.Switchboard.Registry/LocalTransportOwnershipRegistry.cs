using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Registry;

/// <summary>Single-node <see cref="ITransportOwnershipRegistry"/> (plan decision D19, Phase 3 Slice
/// 5) — every connectionToken this process could possibly know about is, by definition, local to
/// it, so there is nothing to claim/release and no remote owner to ever report. Used when
/// <c>UseOrleansCluster = false</c>.</summary>
public sealed class LocalTransportOwnershipRegistry(IOptions<SwitchboardOptions> options) : ITransportOwnershipRegistry
{
    public Task ClaimAsync(string connectionToken, string nodeId, CancellationToken ct) => Task.CompletedTask;

    public Task ReleaseAsync(string connectionToken, CancellationToken ct) => Task.CompletedTask;

    public Task<string?> GetOwnerNodeIdAsync(string connectionToken, CancellationToken ct) =>
        Task.FromResult<string?>(options.Value.NodeId);
}
