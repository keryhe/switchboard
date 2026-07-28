using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Orleans.Grains;
using Keryhe.Switchboard.Protocol;
using Orleans;

namespace Keryhe.Switchboard.Orleans;

/// <summary>
/// Orleans-backed <see cref="IServerConnectionSelector"/> (plan decision D18, Phase 3 Slice 4) —
/// cluster-wide least-loaded assignment via <see cref="IHubGrain.AssignServerConnectionAsync"/>,
/// replacing <c>RoundRobinServerConnectionSelector</c>'s node-local pick under
/// <c>UseOrleansCluster</c>. The assigned connection may live on any node; callers resolve local vs
/// remote themselves via <see cref="ServerConnectionRef.TryParse"/>.
/// </summary>
public sealed class OrleansServerConnectionSelector(IGrainFactory grainFactory) : IServerConnectionSelector
{
    public async Task<string?> AssignConnectionAsync(string hubName, string requestingNodeId, CancellationToken ct)
    {
        var assignment = await grainFactory.GetGrain<IHubGrain>(hubName).AssignServerConnectionAsync(requestingNodeId);
        return assignment is null ? null : ServerConnectionRef.Format(assignment.NodeId, assignment.ServerConnectionId);
    }

    public Task ReleaseConnectionAsync(string hubName, string serverConnectionRef, CancellationToken ct)
    {
        if (!ServerConnectionRef.TryParse(serverConnectionRef, out var nodeId, out var serverConnectionId))
        {
            return Task.CompletedTask;
        }

        return grainFactory.GetGrain<IHubGrain>(hubName).ReleaseServerConnectionAsync(nodeId, serverConnectionId);
    }
}
