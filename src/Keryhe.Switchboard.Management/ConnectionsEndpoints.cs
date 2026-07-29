using Keryhe.Switchboard.Core;
using Microsoft.AspNetCore.Http;

namespace Keryhe.Switchboard.Management;

/// <summary>
/// <c>GET /api/v1/hubs/{hubName}/connections</c> (03-protocol.md Part 3). Mandatorily paginated
/// (Phase 4 plan decision D27) — <c>IClusterInventory.GetConnectionsAsync</c>'s own doc comment
/// explains why an unpaginated version of this endpoint would be a self-inflicted outage on the
/// first cluster with real traffic.
/// </summary>
public static class ConnectionsEndpoints
{
    /// <summary>Requests above this are clamped, not rejected — an operator asking for 100000 gets
    /// the hard cap rather than a 400, since the cap exists to protect the service, not to police
    /// the caller.</summary>
    private const int MaxLimit = 1000;
    private const int DefaultLimit = 100;

    public static async Task<IResult> ListAsync(
        string hubName,
        int? limit,
        string? continuationToken,
        IClusterInventory clusterInventory,
        CancellationToken ct)
    {
        var effectiveLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var page = await clusterInventory.GetConnectionsAsync(hubName, continuationToken, effectiveLimit, ct);
        return Results.Ok(page);
    }
}
