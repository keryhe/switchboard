using System.Collections.Concurrent;

namespace Keryhe.Switchboard.Protocol;

/// <summary>A live snapshot of all server connections registered for a given hub. Held in <see cref="IHubRegistry"/>.</summary>
public sealed class HubDescriptor
{
    public required string HubName { get; init; }

    public ConcurrentDictionary<string, ServerConnectionState> ServerConnections { get; } = new();

    public int ServerConnectionCount => ServerConnections.Count;
    public int ActiveServerConnectionCount => ServerConnections.Values.Count(s => s.Status == ServerConnectionStatus.Connected);
}
