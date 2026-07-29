using Keryhe.Switchboard.Core.Models;

namespace Keryhe.Switchboard.Core;

/// <summary>Cluster-wide connection counts for one hub (Phase 4 plan decision D27, finding 6) —
/// resolved without transferring the full connection-id list just to count it.</summary>
public sealed class HubStats
{
    public required int ClientConnectionCount { get; init; }
    public required int ServerConnectionCount { get; init; }
}

/// <summary>One connection's summary for the management connections listing (03-protocol.md Part 3).</summary>
public sealed class ConnectionSummary
{
    public required string ConnectionId { get; init; }
    public string? UserId { get; init; }
    public required string Transport { get; init; }
    public required DateTimeOffset ConnectedAt { get; init; }
    public required IReadOnlyList<string> Groups { get; init; }

    public static ConnectionSummary FromState(ClientConnectionState state) => new()
    {
        ConnectionId = state.ConnectionId,
        UserId = state.UserId,
        Transport = state.Transport.ToString(),
        ConnectedAt = state.ConnectedAt,
        Groups = state.Groups.Keys.ToList(),
    };
}

/// <summary>
/// A page of a hub's connections (Phase 4 plan decision D27). <see cref="NextContinuationToken"/>
/// is opaque — callers must not parse or construct one, only pass back what a previous page
/// returned. <see cref="TotalCount"/> is the hub's full cluster-wide membership count (one id-list
/// call), not the number of items on this page.
/// </summary>
public sealed class ConnectionsPage
{
    public required IReadOnlyList<ConnectionSummary> Connections { get; init; }
    public required int TotalCount { get; init; }
    public string? NextContinuationToken { get; init; }
}

/// <summary>
/// Cluster-wide operator reads for the management API (Phase 4 plan decision D27) — substituted
/// per deployment mode like every other Phase 3 interface (<c>LocalClusterInventory</c> for
/// single-node, <c>OrleansClusterInventory</c> for clustered), so
/// <c>Keryhe.Switchboard.Management</c> never references <c>Keryhe.Switchboard.Orleans</c>
/// directly and both deployment modes stay real rather than one rotting while attention is on the
/// other.
/// </summary>
public interface IClusterInventory
{
    /// <summary>Every hub name known anywhere in the cluster — union of server-connection-driven
    /// and client-connection-driven sources (finding 5: neither <c>IHubRegistry.GetAllHubs()</c> nor
    /// <c>ILocalTransportRegistry.GetKnownHubNames()</c> is cluster-wide on its own, and a hub known
    /// only via clients on one node must still be visible from every other node).</summary>
    Task<IReadOnlyList<string>> GetAllHubNamesAsync(CancellationToken ct);

    /// <summary>Cluster-wide connection counts for one hub.</summary>
    Task<HubStats> GetHubStatsAsync(string hubName, CancellationToken ct);

    /// <summary>
    /// A page of this hub's connections, cluster-wide. Resolves full connection state for at most
    /// <paramref name="limit"/> connections, never the whole hub — <c>IConnectionRegistry.GetAllAsync</c>'s
    /// own doc comment already warns it is one grain call per connection in clustered mode, and this
    /// is the mandatory pagination that keeps a large hub's listing from doing that per request
    /// (plan decision D27).
    /// </summary>
    Task<ConnectionsPage> GetConnectionsAsync(string hubName, string? continuationToken, int limit, CancellationToken ct);
}

/// <summary>
/// Opaque skip-index continuation token shared by <c>LocalClusterInventory</c> and
/// <c>OrleansClusterInventory</c> so both implementations page identically — connection ids are
/// sorted ordinally before slicing in both, so the token is stable across requests as long as
/// membership doesn't change mid-pagination (the same eventual-consistency caveat any live
/// paginated listing has; not a hard requirement per plan decision D27).
/// </summary>
public static class ConnectionsPageToken
{
    public static int ParseSkip(string? token) => int.TryParse(token, out var skip) && skip > 0 ? skip : 0;

    public static string? Format(int skip, int total) => skip < total ? skip.ToString() : null;
}
