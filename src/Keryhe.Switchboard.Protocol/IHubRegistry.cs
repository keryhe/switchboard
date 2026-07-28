namespace Keryhe.Switchboard.Protocol;

/// <summary>Tracks, per hub, the set of physical app-server WebSocket connections registered for it.</summary>
public interface IHubRegistry
{
    Task RegisterServerConnectionAsync(ServerConnectionState state, CancellationToken ct);
    Task UnregisterServerConnectionAsync(string hubName, string serverConnectionId, CancellationToken ct);
    HubDescriptor? GetHub(string hubName);

    /// <summary>Async as of Phase 3 Slice 4 (plan decision D18) — negotiate's 503 check becomes a
    /// cluster-wide question ("does *any* node have a server connection for this hub") rather than a
    /// node-local one, so this can no longer be answered synchronously from node-local state alone.</summary>
    Task<bool> HasActiveServerConnectionAsync(string hubName, CancellationToken ct);

    IEnumerable<HubDescriptor> GetAllHubs();
}
