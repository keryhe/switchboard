using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Orleans.Grains;
using Orleans;

namespace Keryhe.Switchboard.Orleans;

/// <summary>
/// Orleans-backed <see cref="IClusterInventory"/> (Phase 4 plan decision D27). The hub directory
/// comes from <see cref="INodeRegistryGrain"/> (finding 5 — neither <c>IHubRegistry.GetAllHubs()</c>
/// nor <c>ILocalTransportRegistry.GetKnownHubNames()</c> is cluster-wide alone); counts come from
/// <see cref="IHubGrain.GetStatsAsync"/> (finding 6 — no full connection-id transfer just to count
/// them); the connections page resolves <see cref="IHubGrain.GetConnectionIdsAsync"/> once (already
/// cluster-wide — every registration goes through this one grain regardless of which node accepted
/// it) and then <see cref="IConnectionRegistry.GetAsync"/> for at most <c>limit</c> ids, never the
/// whole hub.
/// </summary>
public sealed class OrleansClusterInventory(IGrainFactory grainFactory, IConnectionRegistry connectionRegistry) : IClusterInventory
{
    public async Task<IReadOnlyList<string>> GetAllHubNamesAsync(CancellationToken ct) =>
        await grainFactory.GetGrain<INodeRegistryGrain>(NodeRegistryGrainKey.Value).GetAllHubNamesAsync();

    public async Task<HubStats> GetHubStatsAsync(string hubName, CancellationToken ct)
    {
        var stats = await grainFactory.GetGrain<IHubGrain>(hubName).GetStatsAsync();
        return new HubStats { ClientConnectionCount = stats.ClientConnectionCount, ServerConnectionCount = stats.ServerConnectionCount };
    }

    public async Task<ConnectionsPage> GetConnectionsAsync(string hubName, string? continuationToken, int limit, CancellationToken ct)
    {
        var ids = (await grainFactory.GetGrain<IHubGrain>(hubName).GetConnectionIdsAsync())
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var skip = ConnectionsPageToken.ParseSkip(continuationToken);
        var pageIds = ids.Skip(skip).Take(limit).ToList();

        var connections = new List<ConnectionSummary>(pageIds.Count);
        foreach (var id in pageIds)
        {
            ct.ThrowIfCancellationRequested();
            var state = await connectionRegistry.GetAsync(id, ct);
            if (state is not null)
            {
                connections.Add(ConnectionSummary.FromState(state));
            }
        }

        return new ConnectionsPage
        {
            Connections = connections,
            TotalCount = ids.Count,
            NextContinuationToken = ConnectionsPageToken.Format(skip + pageIds.Count, ids.Count),
        };
    }
}
