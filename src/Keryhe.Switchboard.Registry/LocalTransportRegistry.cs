using System.Collections.Concurrent;
using Keryhe.Switchboard.Core;

namespace Keryhe.Switchboard.Registry;

/// <summary>In-process implementation of <see cref="ILocalTransportRegistry"/> (plan decision D14)
/// — one node's complete local view: transport handles plus hub/group/user membership and each
/// connection's negotiated hub protocol, so <c>DefaultMessageRouter</c> can fan out without ever
/// enumerating the distributed connection registry.</summary>
public sealed class LocalTransportRegistry : ILocalTransportRegistry
{
    private sealed class Entry(IClientTransport transport, string hubName, string? userId)
    {
        public IClientTransport Transport { get; } = transport;
        public string HubName { get; } = hubName;
        public string? UserId { get; } = userId;
        public string? HubProtocol { get; set; }
        public ConcurrentDictionary<string, byte> Groups { get; } = new();
    }

    private readonly ConcurrentDictionary<string, Entry> _connections = new();

    // hubName -> set of connectionIds
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _hubToConnections = new();

    // "{hubName}::{groupName}" -> set of connectionIds
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _groupToConnections = new();

    // "{hubName}::{userId}" -> set of connectionIds
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _userToConnections = new();

    public void Register(IClientTransport transport, string hubName, string? userId)
    {
        var entry = new Entry(transport, hubName, userId);
        _connections[transport.ConnectionId] = entry;

        _hubToConnections.GetOrAdd(hubName, _ => new ConcurrentDictionary<string, byte>())
            .TryAdd(transport.ConnectionId, 0);

        if (userId is not null)
        {
            _userToConnections.GetOrAdd(UserKey(hubName, userId), _ => new ConcurrentDictionary<string, byte>())
                .TryAdd(transport.ConnectionId, 0);
        }
    }

    public void Unregister(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var entry))
        {
            return;
        }

        if (_hubToConnections.TryGetValue(entry.HubName, out var hubMembers))
        {
            hubMembers.TryRemove(connectionId, out _);
        }

        foreach (var groupName in entry.Groups.Keys)
        {
            if (_groupToConnections.TryGetValue(GroupKey(entry.HubName, groupName), out var groupMembers))
            {
                groupMembers.TryRemove(connectionId, out _);
            }
        }

        if (entry.UserId is not null &&
            _userToConnections.TryGetValue(UserKey(entry.HubName, entry.UserId), out var userMembers))
        {
            userMembers.TryRemove(connectionId, out _);
        }
    }

    public IClientTransport? Get(string connectionId) => _connections.GetValueOrDefault(connectionId)?.Transport;

    public void SetHubProtocol(string connectionId, string hubProtocol)
    {
        if (_connections.TryGetValue(connectionId, out var entry))
        {
            entry.HubProtocol = hubProtocol;
        }
    }

    public string? GetHubProtocol(string connectionId) => _connections.GetValueOrDefault(connectionId)?.HubProtocol;

    public void AddToGroup(string connectionId, string groupName)
    {
        if (!_connections.TryGetValue(connectionId, out var entry))
        {
            return;
        }

        entry.Groups.TryAdd(groupName, 0);
        _groupToConnections.GetOrAdd(GroupKey(entry.HubName, groupName), _ => new ConcurrentDictionary<string, byte>())
            .TryAdd(connectionId, 0);
    }

    public void RemoveFromGroup(string connectionId, string groupName)
    {
        if (!_connections.TryGetValue(connectionId, out var entry))
        {
            return;
        }

        entry.Groups.TryRemove(groupName, out _);
        if (_groupToConnections.TryGetValue(GroupKey(entry.HubName, groupName), out var groupMembers))
        {
            groupMembers.TryRemove(connectionId, out _);
        }
    }

    public IEnumerable<LocalConnection> GetConnectionsForHub(string hubName) =>
        Resolve(_hubToConnections.GetValueOrDefault(hubName));

    public IEnumerable<string> GetKnownHubNames() => _hubToConnections.Keys;

    public IEnumerable<LocalConnection> GetGroupMembers(string hubName, string groupName) =>
        Resolve(_groupToConnections.GetValueOrDefault(GroupKey(hubName, groupName)));

    public IEnumerable<LocalConnection> GetUserConnections(string hubName, string userId) =>
        Resolve(_userToConnections.GetValueOrDefault(UserKey(hubName, userId)));

    private IEnumerable<LocalConnection> Resolve(ConcurrentDictionary<string, byte>? memberIds)
    {
        if (memberIds is null)
        {
            yield break;
        }

        foreach (var connectionId in memberIds.Keys)
        {
            if (_connections.TryGetValue(connectionId, out var entry))
            {
                yield return new LocalConnection(connectionId, entry.HubProtocol);
            }
        }
    }

    private static string GroupKey(string hubName, string groupName) => $"{hubName}::{groupName}";
    private static string UserKey(string hubName, string userId) => $"{hubName}::{userId}";
}
