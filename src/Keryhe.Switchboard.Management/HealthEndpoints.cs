using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Management.Models;
using Microsoft.AspNetCore.Http;

namespace Keryhe.Switchboard.Management;

/// <summary>
/// <c>GET /api/v1/health</c> (03-protocol.md Part 3) — the authenticated, detailed counterpart to
/// the public, cached <c>/healthz</c> (Phase 3 Slice 7), which stays byte-identical and untouched
/// by this phase (plan decision D27). Unlike <c>/healthz</c>, this may do real cluster I/O per
/// request: it is called at human/dashboard rates, never load-balancer probe rates, so the
/// cached-value discipline <c>IReadinessProbe</c> exists for does not apply here.
/// </summary>
public static class HealthEndpoints
{
    public static async Task<IResult> GetAsync(
        IClusterInventory clusterInventory,
        IReadinessProbe readinessProbe,
        CancellationToken ct)
    {
        var hubNames = await clusterInventory.GetAllHubNamesAsync(ct);

        var serverConnections = new Dictionary<string, int>();
        var clientConnectionsTotal = 0;

        foreach (var hubName in hubNames)
        {
            var stats = await clusterInventory.GetHubStatsAsync(hubName, ct);
            serverConnections[hubName] = stats.ServerConnectionCount;
            clientConnectionsTotal += stats.ClientConnectionCount;
        }

        var response = new ManagementHealthResponse(
            readinessProbe.IsReady ? "healthy" : "unhealthy",
            serverConnections,
            clientConnectionsTotal);

        return Results.Ok(response);
    }
}
