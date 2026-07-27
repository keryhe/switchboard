namespace Keryhe.Switchboard.Protocol;

/// <summary>Tracks, per hub, the set of physical app-server WebSocket connections registered for it.</summary>
public interface IHubRegistry
{
    Task RegisterServerConnectionAsync(ServerConnectionState state, CancellationToken ct);
    Task UnregisterServerConnectionAsync(string hubName, string serverConnectionId, CancellationToken ct);
    HubDescriptor? GetHub(string hubName);
    bool HasActiveServerConnection(string hubName);
    IEnumerable<HubDescriptor> GetAllHubs();
}
