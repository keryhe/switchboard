using System.Collections.Concurrent;
using Keryhe.Switchboard.Protocol;

namespace Keryhe.Switchboard.Registry;

public sealed class InMemoryHubRegistry : IHubRegistry
{
    private readonly ConcurrentDictionary<string, HubDescriptor> _hubs = new();

    public Task RegisterServerConnectionAsync(ServerConnectionState state, CancellationToken ct)
    {
        var descriptor = _hubs.GetOrAdd(state.HubName, name => new HubDescriptor { HubName = name });
        descriptor.ServerConnections[state.ConnectionId] = state;
        return Task.CompletedTask;
    }

    public Task UnregisterServerConnectionAsync(string hubName, string serverConnectionId, CancellationToken ct)
    {
        if (_hubs.TryGetValue(hubName, out var descriptor))
        {
            descriptor.ServerConnections.TryRemove(serverConnectionId, out _);
        }

        return Task.CompletedTask;
    }

    public HubDescriptor? GetHub(string hubName) => _hubs.GetValueOrDefault(hubName);

    public bool HasActiveServerConnection(string hubName) =>
        _hubs.TryGetValue(hubName, out var descriptor) && descriptor.ActiveServerConnectionCount > 0;

    public IEnumerable<HubDescriptor> GetAllHubs() => _hubs.Values;
}
