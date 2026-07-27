using System.Collections.Concurrent;
using Keryhe.Switchboard.Protocol;

namespace Keryhe.Switchboard.Registry;

/// <summary>Weighted round-robin: picks the active connection with the lowest current logical connection count.</summary>
public sealed class RoundRobinServerConnectionSelector(IHubRegistry hubRegistry) : IServerConnectionSelector
{
    private readonly ConcurrentDictionary<string, int> _cursors = new();

    public ServerConnectionState? SelectConnection(string hubName)
    {
        var descriptor = hubRegistry.GetHub(hubName);
        if (descriptor is null)
        {
            return null;
        }

        var candidates = descriptor.ServerConnections.Values
            .Where(s => s.Status == ServerConnectionStatus.Connected)
            .OrderBy(s => s.LogicalConnectionCount)
            .ToList();

        return candidates.Count == 0 ? null : candidates[0];
    }
}
