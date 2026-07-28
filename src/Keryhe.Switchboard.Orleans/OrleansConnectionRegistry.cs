using System.Runtime.CompilerServices;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Orleans.Grains;
using Microsoft.Extensions.Options;
using Orleans;

namespace Keryhe.Switchboard.Orleans;

/// <summary>
/// Orleans-backed <see cref="IConnectionRegistry"/> (plan decision D20 — a substitution, per
/// ADR-002, not an interface change). Delegates to <see cref="IConnectionGrain"/>/<see cref="IHubGrain"/>
/// /<see cref="IGroupGrain"/>/<see cref="IUserGrain"/> via <see cref="IGrainFactory"/>. Group
/// membership on the returned <see cref="ClientConnectionState"/> is populated from the connection
/// grain's own mirrored record and is never treated as authoritative (plan decision D15) —
/// <see cref="IGroupGrain"/> is the source of truth.
/// </summary>
public sealed class OrleansConnectionRegistry(IGrainFactory grainFactory, IOptions<SwitchboardOptions> options) : IConnectionRegistry
{
    private readonly string _nodeId = options.Value.NodeId;

    public Task RegisterAsync(ClientConnectionState state, CancellationToken ct)
    {
        var record = new ConnectionRecord
        {
            OwnerNodeId = _nodeId,
            HubName = state.HubName,
            UserId = state.UserId,
            ConnectionToken = state.ConnectionToken,
            Transport = state.Transport,
            HubProtocol = state.HubProtocol,
            ServerConnectionId = state.ServerConnectionId,
            ConnectedAt = state.ConnectedAt,
            LastSeen = state.LastSeen,
        };

        return grainFactory.GetGrain<IConnectionGrain>(state.ConnectionId).RegisterAsync(record);
    }

    public Task SetProtocolAsync(string connectionId, string hubProtocol, CancellationToken ct) =>
        grainFactory.GetGrain<IConnectionGrain>(connectionId).SetHubProtocolAsync(hubProtocol);

    public Task UnregisterAsync(string connectionId, CancellationToken ct) =>
        grainFactory.GetGrain<IConnectionGrain>(connectionId).UnregisterAsync();

    public async Task<ClientConnectionState?> GetAsync(string connectionId, CancellationToken ct)
    {
        var record = await grainFactory.GetGrain<IConnectionGrain>(connectionId).GetAsync();
        return record is null ? null : ToState(connectionId, record);
    }

    /// <summary>Not on the fan-out hot path (plan decision D14) — diagnostics/tests only. One
    /// grain call per connection in the hub, since <see cref="IHubGrain"/> only tracks membership,
    /// not full connection state.</summary>
    public async IAsyncEnumerable<ClientConnectionState> GetAllAsync(string hubName, [EnumeratorCancellation] CancellationToken ct)
    {
        var connectionIds = await grainFactory.GetGrain<IHubGrain>(hubName).GetConnectionIdsAsync();
        foreach (var connectionId in connectionIds)
        {
            ct.ThrowIfCancellationRequested();
            var state = await GetAsync(connectionId, ct);
            if (state is not null)
            {
                yield return state;
            }
        }
    }

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct) =>
        grainFactory.GetGrain<IConnectionGrain>(connectionId).AddToGroupAsync(groupName);

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct) =>
        grainFactory.GetGrain<IConnectionGrain>(connectionId).RemoveFromGroupAsync(groupName);

    public async IAsyncEnumerable<ClientConnectionState> GetGroupMembersAsync(string hubName, string groupName, [EnumeratorCancellation] CancellationToken ct)
    {
        var connectionIds = await grainFactory.GetGrain<IGroupGrain>(GroupKey(hubName, groupName)).GetMembersAsync();
        foreach (var connectionId in connectionIds)
        {
            ct.ThrowIfCancellationRequested();
            var state = await GetAsync(connectionId, ct);
            if (state is not null)
            {
                yield return state;
            }
        }
    }

    public async IAsyncEnumerable<ClientConnectionState> GetUserConnectionsAsync(string hubName, string userId, [EnumeratorCancellation] CancellationToken ct)
    {
        var connectionIds = await grainFactory.GetGrain<IUserGrain>(UserKey(hubName, userId)).GetConnectionIdsAsync();
        foreach (var connectionId in connectionIds)
        {
            ct.ThrowIfCancellationRequested();
            var state = await GetAsync(connectionId, ct);
            if (state is not null)
            {
                yield return state;
            }
        }
    }

    private static ClientConnectionState ToState(string connectionId, ConnectionRecord record)
    {
        var state = new ClientConnectionState
        {
            ConnectionId = connectionId,
            ConnectionToken = record.ConnectionToken,
            HubName = record.HubName,
            UserId = record.UserId,
            Transport = record.Transport,
            HubProtocol = record.HubProtocol,
            ServerConnectionId = record.ServerConnectionId,
            ConnectedAt = record.ConnectedAt,
            LastSeen = record.LastSeen,
        };

        foreach (var groupName in record.Groups)
        {
            state.Groups.TryAdd(groupName, 0);
        }

        return state;
    }

    private static string GroupKey(string hubName, string groupName) => $"{hubName}::{groupName}";
    private static string UserKey(string hubName, string userId) => $"{hubName}::{userId}";
}
