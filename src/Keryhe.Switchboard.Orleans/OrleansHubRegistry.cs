using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Orleans.Grains;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;

namespace Keryhe.Switchboard.Orleans;

/// <summary>
/// Orleans-backed <see cref="IHubRegistry"/> (plan decision D18/D20, Phase 3 Slice 4). Node-local
/// live-connection bookkeeping (<see cref="GetHub"/>, <see cref="GetAllHubs"/>) is identical to
/// <see cref="InMemoryHubRegistry"/> — a live <see cref="IServerConnection"/> is only ever
/// meaningful on the node that physically owns the socket, so it is delegated to an internal
/// instance rather than duplicated — while <see cref="RegisterServerConnectionAsync"/>/
/// <see cref="UnregisterServerConnectionAsync"/>/<see cref="HasActiveServerConnectionAsync"/>
/// additionally inform <see cref="IHubGrain"/>'s cluster-wide server-connection inventory, which is
/// what makes negotiate's 503 check and server-connection assignment cluster-wide instead of
/// node-local.
/// </summary>
public sealed class OrleansHubRegistry(IGrainFactory grainFactory, IOptions<SwitchboardOptions> options, ILogger<OrleansHubRegistry> logger) : IHubRegistry
{
    private readonly InMemoryHubRegistry _local = new();
    private readonly string _nodeId = options.Value.NodeId;

    public async Task RegisterServerConnectionAsync(ServerConnectionState state, CancellationToken ct)
    {
        await _local.RegisterServerConnectionAsync(state, ct);

        // The grain's inventory has no concept of ServerConnectionStatus (Degraded/Reconnecting
        // transitions happen purely in-place on the local ServerConnectionState object, never
        // reported anywhere) — only registering a connection that starts out Connected keeps
        // HasActiveServerConnectionAsync's cluster-wide answer consistent with the node-local one
        // for the case that actually varies at registration time. In practice every real connection
        // is Connected here (ServerConnectionEndpoint accepts and hands off in that state); a
        // connection degrading *after* registration is a known, accepted gap — the cluster-wide
        // inventory doesn't yet track post-registration status changes.
        if (state.Status == ServerConnectionStatus.Connected)
        {
            await grainFactory.GetGrain<IHubGrain>(state.HubName).RegisterServerConnectionAsync(_nodeId, state.ConnectionId);
        }
    }

    public async Task UnregisterServerConnectionAsync(string hubName, string serverConnectionId, CancellationToken ct)
    {
        await _local.UnregisterServerConnectionAsync(hubName, serverConnectionId, ct);

        // The grain call does the cluster-wide inventory removal *and* the OnCloseConnection
        // fan-out to every client that was assigned to this connection, wherever it lives (plan
        // decision D18) — see HubGrain.UnregisterServerConnectionAsync. Best-effort: this runs from
        // ServerConnectionEndpoint's own teardown, which can itself be racing this exact process's
        // graceful shutdown (an app server connection dropping *because* the node is going down) —
        // an outbound grain call losing its silo mid-flight must never crash the host on its way
        // out. The node-local unregister above has already happened regardless.
        try
        {
            await grainFactory.GetGrain<IHubGrain>(hubName).UnregisterServerConnectionAsync(_nodeId, serverConnectionId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UnregisterServerConnectionAsync: cluster-wide cleanup for {HubName}/{ServerConnectionId} failed; node-local state was already removed.", hubName, serverConnectionId);
        }
    }

    public HubDescriptor? GetHub(string hubName) => _local.GetHub(hubName);

    public Task<bool> HasActiveServerConnectionAsync(string hubName, CancellationToken ct) =>
        grainFactory.GetGrain<IHubGrain>(hubName).HasActiveServerConnectionAsync();

    public IEnumerable<HubDescriptor> GetAllHubs() => _local.GetAllHubs();
}
