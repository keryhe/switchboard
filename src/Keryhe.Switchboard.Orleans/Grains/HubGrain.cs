using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Orleans.Observers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;

namespace Keryhe.Switchboard.Orleans.Grains;

[GenerateSerializer]
public sealed class HubGrainState
{
    [Id(0)]
    public HashSet<string> ConnectionIds { get; set; } = [];
}

/// <summary>
/// Not marked <c>[Reentrant]</c>: a prior version of <see cref="UnregisterServerConnectionAsync"/>
/// awaited an <see cref="IHubObserver.OnCloseConnection"/> call chain that could call back into this
/// exact grain's <see cref="UnregisterConnectionAsync"/> — a real call cycle back to the same
/// activation, verified to deadlock outright (Orleans grains process one call at a time by default,
/// so the grain can never reach the incoming call while still blocked awaiting the chain that
/// produces it). Fixed structurally instead of via reentrancy — <see cref="UnregisterServerConnectionAsync"/>
/// now removes the connectionId from its own state and calls
/// <c>IConnectionGrain.UnregisterWithoutHubCallbackAsync</c> directly, which never calls back here —
/// so no cycle remains and this grain stays single-turn, the simpler default to reason about.
/// </summary>
public sealed class HubGrain(
    [PersistentState("hub", SwitchboardOrleansExtensions.StorageProviderName)] IPersistentState<HubGrainState> state,
    IOptions<SwitchboardOptions> options,
    SwitchboardMetrics metrics,
    ILogger<HubGrain> logger)
    : Grain, IHubGrain
{
    /// <summary>
    /// How many heartbeats an observer may miss before it is presumed gone. Three is the usual
    /// missed-heartbeat convention — long enough that a GC pause or a momentarily busy node is never
    /// mistaken for a dead one, short enough that a genuinely dead node stops being retried promptly.
    /// </summary>
    private const int MissedHeartbeatsBeforeEviction = 3;

    /// <remarks>
    /// Measured against the same one-second-floored interval <c>ObserverHeartbeatService</c> uses to
    /// bound a single attempt, not the raw configured interval. They have to agree: a node that
    /// abandons one slow attempt does not re-subscribe again until the following tick, so with a
    /// fast cadence the real worst-case gap between successful re-subscribes is roughly
    /// <c>attempt timeout + interval</c>. Deriving this threshold from the raw interval instead would
    /// put it *below* that gap and evict healthy nodes for a single slow call.
    /// </remarks>
    private readonly TimeSpan _observerStaleAfter =
        TimeSpan.FromSeconds(Math.Max(1, options.Value.ObserverHeartbeatInterval.TotalSeconds)) * MissedHeartbeatsBeforeEviction;

    /// <summary>
    /// Deliberately NOT part of <see cref="HubGrainState"/> — an observer reference is a live
    /// pointer to an object on whichever silo registered it, meaningless (and unsafe to treat as
    /// meaningful) once persisted and read back after a restart. "No subscription handle
    /// persistence required" (ADR-003); the re-subscribe heartbeat is what rebuilds this on both a
    /// grain reactivation and a silo restart alike.
    /// </summary>
    private readonly Dictionary<string, (IHubObserver Observer, DateTimeOffset LastSeen)> _observers = new();

    /// <summary>
    /// Cluster-wide server-connection inventory (plan decision D18, Phase 3 Slice 4) — keyed by
    /// <see cref="ServerConnectionRef"/>-formatted reference, value is the current assignment count.
    /// Transient for the same reason <see cref="_observers"/> is: a live app-server WebSocket is
    /// meaningless once persisted and read back after a restart; a reconnecting app server
    /// re-registers on its own.
    /// </summary>
    private readonly Dictionary<string, int> _serverConnectionCounts = new();

    public async Task RegisterConnectionAsync(string connectionId)
    {
        if (state.State.ConnectionIds.Add(connectionId))
        {
            await state.WriteStateAsync();
        }
    }

    public async Task UnregisterConnectionAsync(string connectionId)
    {
        if (state.State.ConnectionIds.Remove(connectionId))
        {
            await state.WriteStateAsync();
        }
    }

    public Task<List<string>> GetConnectionIdsAsync() => Task.FromResult(state.State.ConnectionIds.ToList());

    public Task SubscribeAsync(IHubObserver observer, string nodeId)
    {
        _observers[nodeId] = (observer, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(string nodeId)
    {
        _observers.Remove(nodeId);
        return Task.CompletedTask;
    }

    public Task BroadcastAsync(byte[] payload, Dictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds, string originNodeId) =>
        FanOutToObserversAsync(originNodeId, observer => observer.OnBroadcast(payload, payloadsByProtocol, excludedConnectionIds));

    public Task GroupMessageAsync(string groupName, byte[] payload, Dictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds, string originNodeId) =>
        FanOutToObserversAsync(originNodeId, observer => observer.OnGroupMessage(groupName, payload, payloadsByProtocol, excludedConnectionIds));

    public Task UserMessageAsync(string userId, byte[] payload, Dictionary<string, byte[]>? payloadsByProtocol, string originNodeId) =>
        FanOutToObserversAsync(originNodeId, observer => observer.OnUserMessage(userId, payload, payloadsByProtocol));

    public Task SendToConnectionAsync(string targetNodeId, string connectionId, byte[] payload, string hubProtocol) =>
        InvokeObserverAsync(targetNodeId, observer => observer.OnConnectionMessage(connectionId, payload, hubProtocol), connectionId);

    public Task SendServerEnvelopeAsync(string targetNodeId, string serverConnectionId, byte[] serializedEnvelope) =>
        InvokeObserverAsync(targetNodeId, observer => observer.OnServerEnvelope(serverConnectionId, serializedEnvelope), serverConnectionId);

    public Task CloseConnectionAsync(string targetNodeId, string connectionId, string? error, bool allowReconnect) =>
        InvokeObserverAsync(targetNodeId, observer => observer.OnCloseConnection(connectionId, error, allowReconnect), connectionId);

    public Task AddToGroupCrossNodeAsync(string targetNodeId, string connectionId, string groupName) =>
        InvokeObserverAsync(targetNodeId, observer => observer.OnAddToGroup(connectionId, groupName), connectionId);

    public Task RemoveFromGroupCrossNodeAsync(string targetNodeId, string connectionId, string groupName) =>
        InvokeObserverAsync(targetNodeId, observer => observer.OnRemoveFromGroup(connectionId, groupName), connectionId);

    /// <summary>
    /// Idempotent: <see cref="ObserverHeartbeatService"/> re-announces every live server connection
    /// on each tick (this inventory is transient, so a deactivation would otherwise drop it for
    /// good), and an already-known connection must keep its current assignment count — resetting it
    /// to zero every heartbeat would make <see cref="AssignServerConnectionAsync"/>'s least-loaded
    /// pick meaningless.
    /// </summary>
    public Task RegisterServerConnectionAsync(string nodeId, string serverConnectionId)
    {
        _serverConnectionCounts.TryAdd(ServerConnectionRef.Format(nodeId, serverConnectionId), 0);
        return Task.CompletedTask;
    }

    public async Task UnregisterServerConnectionAsync(string nodeId, string serverConnectionId)
    {
        var reference = ServerConnectionRef.Format(nodeId, serverConnectionId);
        _serverConnectionCounts.Remove(reference);

        // Notify every client currently assigned to the dropped connection, wherever it lives.
        // ConnectionIds is already tracked for this hub (client-connection membership) — reusing it
        // avoids a second reverse index just for this rare event.
        var stateChanged = false;
        foreach (var connectionId in state.State.ConnectionIds.ToList())
        {
            var record = await GrainFactory.GetGrain<IConnectionGrain>(connectionId).GetAsync();
            if (record is null || record.ServerConnectionId != reference)
            {
                continue;
            }

            // Removed from this grain's own state and the connection grain's distributed state
            // *before* notifying the observer, and via UnregisterWithoutHubCallbackAsync rather
            // than the ordinary UnregisterAsync — that method calls back into
            // IHubGrain.UnregisterConnectionAsync on this exact grain, which would still be
            // executing this call. A real cycle back to the same activation, verified to deadlock:
            // Orleans grains process one call at a time, so this grain can never reach that
            // incoming call while still blocked awaiting the chain that would produce it. Doing the
            // removal here directly avoids the cycle entirely rather than depending on [Reentrant]
            // alone.
            state.State.ConnectionIds.Remove(connectionId);
            stateChanged = true;
            await GrainFactory.GetGrain<IConnectionGrain>(connectionId).UnregisterWithoutHubCallbackAsync();

            await InvokeObserverAsync(record.OwnerNodeId, observer => observer.OnCloseConnection(connectionId, "Server connection lost.", allowReconnect: true), connectionId);
        }

        if (stateChanged)
        {
            await state.WriteStateAsync();
        }
    }

    /// <summary>
    /// Least-loaded pick restricted to connections this call can actually reach: a connection whose
    /// owner node is <paramref name="requestingNodeId"/> itself, or whose owner node currently has
    /// an active observer subscription. A candidate on some other node with no subscription would
    /// be assigned, then silently dropped on delivery — <c>HubGrain.InvokeObserverAsync</c> logs a
    /// warning and returns rather than throwing, since a missing subscription there also covers the
    /// ordinary "node briefly between heartbeats" case, which is not an error. And because
    /// assignment is sticky (the client keeps this connection for its whole life,
    /// <see cref="ClientConnectionValidation"/>), a client unlucky enough to land on that pick never
    /// recovers on a later heartbeat the way a fan-out send would — verified: it broke
    /// OpenConnection delivery outright when a remote node's subscription hadn't landed yet.
    /// Requiring nothing beyond "local" for the local node itself matters just as much: local
    /// delivery in <c>ClientConnectionLifecycle.SendToAssignedServerConnectionAsync</c> writes
    /// straight to the node's own <see cref="IServerConnection"/> and never goes through an
    /// observer at all, so gating a local pick on this node's own subscription would incorrectly
    /// refuse a connection that was always going to work.
    /// </summary>
    public Task<ServerConnectionAssignment?> AssignServerConnectionAsync(string requestingNodeId)
    {
        // Reachability below is judged from the observer set, so stale entries must go first —
        // otherwise a client could be assigned, stickily, to a connection on a node that stopped
        // heartbeating and can no longer be delivered to.
        PruneStaleObservers();

        var candidates = _serverConnectionCounts
            .Where(kv => ServerConnectionRef.TryParse(kv.Key, out var ownerNodeId, out _)
                && (ownerNodeId == requestingNodeId || _observers.ContainsKey(ownerNodeId)))
            .ToList();

        if (candidates.Count == 0)
        {
            return Task.FromResult<ServerConnectionAssignment?>(null);
        }

        var best = candidates.OrderBy(kv => kv.Value).First();
        _serverConnectionCounts[best.Key] = best.Value + 1;

        ServerConnectionRef.TryParse(best.Key, out var nodeId, out var serverConnectionId);
        return Task.FromResult<ServerConnectionAssignment?>(new ServerConnectionAssignment { NodeId = nodeId, ServerConnectionId = serverConnectionId });
    }

    public Task ReleaseServerConnectionAsync(string nodeId, string serverConnectionId)
    {
        var reference = ServerConnectionRef.Format(nodeId, serverConnectionId);
        if (_serverConnectionCounts.TryGetValue(reference, out var count))
        {
            _serverConnectionCounts[reference] = Math.Max(0, count - 1);
        }

        return Task.CompletedTask;
    }

    public Task<bool> HasActiveServerConnectionAsync() => Task.FromResult(_serverConnectionCounts.Count > 0);

    /// <summary>
    /// Shared iterate-every-subscriber-except-origin, evict-on-<see cref="ClientNotAvailableException"/>
    /// shape used by <see cref="BroadcastAsync"/>, <see cref="GroupMessageAsync"/>, and
    /// <see cref="UserMessageAsync"/> (plan decision D16/D17) — a dead node's observer reference
    /// throws on every call forever, so eviction is what keeps it from being retried on the next
    /// publish, while a transient failure (RPC timeout, momentary partition) is not proof the node
    /// is gone and the heartbeat re-subscribe recovers it on its own.
    /// </summary>
    private async Task FanOutToObserversAsync(string originNodeId, Func<IHubObserver, Task> invokeAsync)
    {
        PruneStaleObservers();

        List<string>? deadNodes = null;

        foreach (var (nodeId, entry) in _observers)
        {
            if (nodeId == originNodeId)
            {
                continue;
            }

            try
            {
                await invokeAsync(entry.Observer);
            }
            catch (ClientNotAvailableException)
            {
                (deadNodes ??= []).Add(nodeId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Observer call to node {NodeId} for hub {HubName} failed; not evicting.", nodeId, this.GetPrimaryKeyString());
            }
        }

        if (deadNodes is null)
        {
            return;
        }

        foreach (var nodeId in deadNodes)
        {
            _observers.Remove(nodeId);
            logger.LogInformation("Evicted stale observer for node {NodeId} on hub {HubName}.", nodeId, this.GetPrimaryKeyString());
        }
    }

    /// <summary>
    /// Shared targeted-call shape used by <see cref="SendToConnectionAsync"/>,
    /// <see cref="SendServerEnvelopeAsync"/>, and <see cref="CloseConnectionAsync"/> (plan decision
    /// D17/D18) — calls exactly one node's observer, never every subscriber. If that node has no
    /// active subscription (dead, or never subscribed), this logs one warning and drops the message
    /// rather than throwing; a live subscription that throws <see cref="ClientNotAvailableException"/>
    /// is evicted the same way <see cref="FanOutToObserversAsync"/> evicts one.
    /// </summary>
    private async Task InvokeObserverAsync(string targetNodeId, Func<IHubObserver, Task> invokeAsync, string subjectForLogging)
    {
        PruneStaleObservers();

        if (!_observers.TryGetValue(targetNodeId, out var entry))
        {
            logger.LogWarning("Targeted observer call: node {NodeId} for hub {HubName} has no active subscription; dropping message for {Subject}.", targetNodeId, this.GetPrimaryKeyString(), subjectForLogging);
            metrics.EnvelopesUnrouted.Add(1, new KeyValuePair<string, object?>("reason", "no_node_subscribed"));
            return;
        }

        try
        {
            await invokeAsync(entry.Observer);
        }
        catch (ClientNotAvailableException)
        {
            _observers.Remove(targetNodeId);
            logger.LogInformation("Evicted stale observer for node {NodeId} on hub {HubName}.", targetNodeId, this.GetPrimaryKeyString());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Observer call to node {NodeId} for hub {HubName} failed; not evicting.", targetNodeId, this.GetPrimaryKeyString());
        }
    }

    public Task<int> GetSubscriberCountAsync()
    {
        PruneStaleObservers();
        return Task.FromResult(_observers.Count);
    }

    /// <summary>
    /// Drops observers whose node has stopped re-subscribing, which is the only eviction signal
    /// that does not depend on guessing an exception type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FanOutToObserversAsync"/> evicts on <see cref="ClientNotAvailableException"/>,
    /// which Orleans raises for a dead observer target — but that is not the only way a dead node
    /// surfaces. Killing a silo outright can just as easily produce a
    /// <c>SiloUnavailableException</c>, a message-rejection, or a plain RPC timeout, and those are
    /// deliberately not evicted there because a transient failure is not proof a node is gone.
    /// The result was an observer for a node that no longer exists staying subscribed forever, retried
    /// on every single publish — verified: it made an end-to-end node-death test intermittently
    /// observe a subscriber count that never dropped, and the node it pointed at was never coming
    /// back.
    /// </para>
    /// <para>
    /// <see cref="SubscribeAsync"/> already stamps <c>LastSeen</c> on every heartbeat
    /// (plan decision D16), so freshness answers the question directly and without an exception
    /// taxonomy: a live node re-subscribes on its own cadence no matter how any individual publish
    /// went, and a node that has stopped is gone. Evicting is also cheap and self-correcting — a node
    /// wrongly evicted after a long stall re-subscribes on its very next tick, costing it at most one
    /// heartbeat of fan-out. Uses this node's own configured interval as the yardstick, which assumes
    /// every node runs the same heartbeat cadence — true for any normal deployment, where
    /// <see cref="SwitchboardOptions"/> is shared configuration.
    /// </para>
    /// </remarks>
    private void PruneStaleObservers()
    {
        if (_observers.Count == 0)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - _observerStaleAfter;
        List<string>? staleNodes = null;

        foreach (var (nodeId, entry) in _observers)
        {
            if (entry.LastSeen < cutoff)
            {
                (staleNodes ??= []).Add(nodeId);
            }
        }

        if (staleNodes is null)
        {
            return;
        }

        foreach (var nodeId in staleNodes)
        {
            _observers.Remove(nodeId);
            logger.LogInformation(
                "Evicted observer for node {NodeId} on hub {HubName}: no re-subscribe within {StaleAfter}.",
                nodeId, this.GetPrimaryKeyString(), _observerStaleAfter);
        }
    }

    public Task DeactivateForTestingAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public Task<HubGrainStats> GetStatsAsync() => Task.FromResult(new HubGrainStats
    {
        ClientConnectionCount = state.State.ConnectionIds.Count,
        ServerConnectionCount = _serverConnectionCounts.Count,
    });
}
