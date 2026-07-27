using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;

namespace Keryhe.Switchboard.Registry;

public sealed class InMemoryConnectionRegistry : IConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ClientConnectionState> _connections = new();

    // "{hubName}::{groupName}" -> set of connectionIds
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _groupToConnections = new();

    // "{hubName}::{userId}" -> set of connectionIds
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _userToConnections = new();

    public Task RegisterAsync(ClientConnectionState state, CancellationToken ct)
    {
        _connections[state.ConnectionId] = state;

        if (state.UserId is not null)
        {
            var userKey = UserKey(state.HubName, state.UserId);
            _userToConnections.GetOrAdd(userKey, _ => new ConcurrentDictionary<string, byte>())
                .TryAdd(state.ConnectionId, 0);
        }

        return Task.CompletedTask;
    }

    public Task SetProtocolAsync(string connectionId, string hubProtocol, CancellationToken ct)
    {
        if (_connections.TryGetValue(connectionId, out var state))
        {
            state.HubProtocol = hubProtocol;
        }

        return Task.CompletedTask;
    }

    public Task UnregisterAsync(string connectionId, CancellationToken ct)
    {
        if (!_connections.TryRemove(connectionId, out var state))
        {
            return Task.CompletedTask;
        }

        foreach (var groupName in state.Groups.Keys)
        {
            if (_groupToConnections.TryGetValue(GroupKey(state.HubName, groupName), out var members))
            {
                members.TryRemove(connectionId, out _);
            }
        }

        if (state.UserId is not null &&
            _userToConnections.TryGetValue(UserKey(state.HubName, state.UserId), out var userConns))
        {
            userConns.TryRemove(connectionId, out _);
        }

        return Task.CompletedTask;
    }

    public Task<ClientConnectionState?> GetAsync(string connectionId, CancellationToken ct) =>
        Task.FromResult(_connections.GetValueOrDefault(connectionId));

    public async IAsyncEnumerable<ClientConnectionState> GetAllAsync(string hubName, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var state in _connections.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (state.HubName == hubName)
            {
                yield return state;
            }
        }

        await Task.CompletedTask;
    }

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct)
    {
        if (!_connections.TryGetValue(connectionId, out var state))
        {
            return Task.CompletedTask;
        }

        state.Groups.TryAdd(groupName, 0);
        _groupToConnections.GetOrAdd(GroupKey(state.HubName, groupName), _ => new ConcurrentDictionary<string, byte>())
            .TryAdd(connectionId, 0);

        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct)
    {
        if (!_connections.TryGetValue(connectionId, out var state))
        {
            return Task.CompletedTask;
        }

        state.Groups.TryRemove(groupName, out _);
        if (_groupToConnections.TryGetValue(GroupKey(state.HubName, groupName), out var members))
        {
            members.TryRemove(connectionId, out _);
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ClientConnectionState> GetGroupMembersAsync(string hubName, string groupName, [EnumeratorCancellation] CancellationToken ct)
    {
        if (_groupToConnections.TryGetValue(GroupKey(hubName, groupName), out var members))
        {
            foreach (var connectionId in members.Keys)
            {
                ct.ThrowIfCancellationRequested();
                if (_connections.TryGetValue(connectionId, out var state))
                {
                    yield return state;
                }
            }
        }

        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<ClientConnectionState> GetUserConnectionsAsync(string hubName, string userId, [EnumeratorCancellation] CancellationToken ct)
    {
        if (_userToConnections.TryGetValue(UserKey(hubName, userId), out var members))
        {
            foreach (var connectionId in members.Keys)
            {
                ct.ThrowIfCancellationRequested();
                if (_connections.TryGetValue(connectionId, out var state))
                {
                    yield return state;
                }
            }
        }

        await Task.CompletedTask;
    }

    private static string GroupKey(string hubName, string groupName) => $"{hubName}::{groupName}";
    private static string UserKey(string hubName, string userId) => $"{hubName}::{userId}";
}
