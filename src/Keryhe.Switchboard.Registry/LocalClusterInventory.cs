using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;

namespace Keryhe.Switchboard.Registry;

/// <summary>
/// Single-node <see cref="IClusterInventory"/> (Phase 4 plan decision D27) — this node IS the
/// cluster, so every read is answered from node-local state with no cross-node aggregation needed.
/// </summary>
public sealed class LocalClusterInventory(
    IConnectionRegistry connectionRegistry,
    IHubRegistry hubRegistry,
    ILocalTransportRegistry localTransportRegistry) : IClusterInventory
{
    public Task<IReadOnlyList<string>> GetAllHubNamesAsync(CancellationToken ct)
    {
        IReadOnlyList<string> names = hubRegistry.GetAllHubs().Select(h => h.HubName)
            .Union(localTransportRegistry.GetKnownHubNames())
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult(names);
    }

    public Task<HubStats> GetHubStatsAsync(string hubName, CancellationToken ct)
    {
        var clientCount = localTransportRegistry.GetConnectionsForHub(hubName).Count();
        var serverCount = hubRegistry.GetHub(hubName)?.ServerConnectionCount ?? 0;
        return Task.FromResult(new HubStats { ClientConnectionCount = clientCount, ServerConnectionCount = serverCount });
    }

    public async Task<ConnectionsPage> GetConnectionsAsync(string hubName, string? continuationToken, int limit, CancellationToken ct)
    {
        var ids = localTransportRegistry.GetConnectionsForHub(hubName)
            .Select(c => c.ConnectionId)
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
