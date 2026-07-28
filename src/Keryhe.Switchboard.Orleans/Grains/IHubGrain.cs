using Keryhe.Switchboard.Orleans.Observers;
using Orleans;

namespace Keryhe.Switchboard.Orleans.Grains;

/// <summary>Authoritative set of connectionIds for one hub, keyed by <c>hubName</c> (ADR-002 grain
/// topology), plus (Phase 3 Slice 2, plan decision D16) the per-node observer subscriptions that
/// make it the backplane's cross-silo fan-out coordinator for this hub. Client-connection
/// membership backs <c>OrleansConnectionRegistry.GetAllAsync</c> (diagnostics/tests — not the
/// fan-out hot path, plan decision D14). Server-connection tracking and cluster-wide assignment
/// (plan decision D18) are Phase 3 Slice 4 additions, layered on top via new
/// <c>[Id(n)]</c>-numbered state rather than changing what's here.</summary>
[Alias("Keryhe.Switchboard.Orleans.Grains.IHubGrain")]
public interface IHubGrain : IGrainWithStringKey
{
    [Alias("RegisterConnection")]
    Task RegisterConnectionAsync(string connectionId);

    [Alias("UnregisterConnection")]
    Task UnregisterConnectionAsync(string connectionId);

    [Alias("GetConnectionIds")]
    Task<List<string>> GetConnectionIdsAsync();

    /// <summary>Called by <see cref="Observers.ObserverHeartbeatService"/> on every heartbeat tick,
    /// not just once at startup — re-subscribing is what makes a hub-grain deactivation (finding 5:
    /// silently drops every subscription) and a transient observer-call failure both survivable
    /// without an operator restarting anything.</summary>
    [Alias("Subscribe")]
    Task SubscribeAsync(IHubObserver observer, string nodeId);

    [Alias("Unsubscribe")]
    Task UnsubscribeAsync(string nodeId);

    /// <summary><paramref name="originNodeId"/>'s own observer is skipped — it already delivered
    /// to its local targets before publishing here (plan decision D14). A dead node's observer call
    /// throws <c>Orleans.Runtime.ClientNotAvailableException</c> (verified, finding 3); that
    /// specific node is evicted so it is never retried, while a call to every other node still
    /// proceeds.</summary>
    [Alias("Broadcast")]
    Task BroadcastAsync(byte[] payload, Dictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds, string originNodeId);

    /// <summary>Cross-node group send (plan decision D17, Phase 3 Slice 3) — published by group
    /// name, not membership: every subscribed node's observer resolves the group against its own
    /// <c>ILocalTransportRegistry</c> and applies <paramref name="excludedConnectionIds"/> locally,
    /// so this grain never needs to know who the members are.</summary>
    [Alias("GroupMessage")]
    Task GroupMessageAsync(string groupName, byte[] payload, Dictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds, string originNodeId);

    /// <summary>Cross-node user send (plan decision D17, Phase 3 Slice 3) — same "publish by name,
    /// resolve locally" shape as <see cref="GroupMessageAsync"/>.</summary>
    [Alias("UserMessage")]
    Task UserMessageAsync(string userId, byte[] payload, Dictionary<string, byte[]>? payloadsByProtocol, string originNodeId);

    /// <summary>Cross-node targeted send (plan decision D17, Phase 3 Slice 3) — unlike
    /// <see cref="BroadcastAsync"/>/<see cref="GroupMessageAsync"/>/<see cref="UserMessageAsync"/>,
    /// this calls exactly one node's observer (<paramref name="targetNodeId"/>, resolved by the
    /// caller via <see cref="IConnectionGrain.GetOwnerNodeAsync"/>), never every subscriber. If that
    /// node has no active subscription (dead, or never subscribed), this logs one warning and drops
    /// the message rather than throwing.</summary>
    [Alias("SendToConnection")]
    Task SendToConnectionAsync(string targetNodeId, string connectionId, byte[] payload, string hubProtocol);

    /// <summary>
    /// Cluster-wide server-connection inventory (plan decision D18, Phase 3 Slice 4) — called by
    /// <c>OrleansHubRegistry.RegisterServerConnectionAsync</c> once an app server's WebSocket is
    /// accepted on <paramref name="nodeId"/>, in addition to that node's own local bookkeeping.
    /// Transient (never <c>[PersistentState]</c>), same rationale as the observer subscriptions:
    /// meaningless once persisted and read back after a restart — a reconnecting app server
    /// re-registers on its own.
    /// </summary>
    [Alias("RegisterServerConnection")]
    Task RegisterServerConnectionAsync(string nodeId, string serverConnectionId);

    /// <summary>
    /// Removes the connection from the cluster-wide inventory and, as part of the same call,
    /// notifies every client currently assigned to it — wherever in the cluster they live — via
    /// <see cref="Observers.IHubObserver.OnCloseConnection"/> with <c>allowReconnect: true</c>. Scans
    /// this hub's own client-connection membership (already tracked, no new reverse index needed)
    /// and checks each one's <c>ConnectionRecord.ServerConnectionId</c> against the dropped
    /// reference — O(clients on this hub) grain calls, acceptable since a server-connection drop is
    /// rare, not a hot path.
    /// </summary>
    [Alias("UnregisterServerConnection")]
    Task UnregisterServerConnectionAsync(string nodeId, string serverConnectionId);

    /// <summary>Least-loaded pick across every server connection registered for this hub cluster-wide,
    /// incrementing its assignment count atomically with the pick. Null if none are registered
    /// anywhere (negotiate/connect 503).</summary>
    [Alias("AssignServerConnection")]
    Task<ServerConnectionAssignment?> AssignServerConnectionAsync(string requestingNodeId);

    /// <summary>Decrements an assignment's count. A no-op if the connection is already gone.</summary>
    [Alias("ReleaseServerConnection")]
    Task ReleaseServerConnectionAsync(string nodeId, string serverConnectionId);

    /// <summary>Cluster-wide answer to "does this hub have a server connection anywhere" — negotiate's
    /// 503 check (plan decision D18) is no longer a node-local question.</summary>
    [Alias("HasActiveServerConnection")]
    Task<bool> HasActiveServerConnectionAsync();

    /// <summary>
    /// Delivers an arbitrary, already-serialized <c>ServerEnvelope</c> (open/client-message/close —
    /// anything service→app-server) to <paramref name="targetNodeId"/>'s specific server connection,
    /// via that node's <see cref="Observers.IHubObserver.OnServerEnvelope"/>. Same targeted,
    /// single-node-only shape as <see cref="SendToConnectionAsync"/> — never a fan-out.
    /// </summary>
    [Alias("SendServerEnvelope")]
    Task SendServerEnvelopeAsync(string targetNodeId, string serverConnectionId, byte[] serializedEnvelope);

    /// <summary>
    /// Targeted close for one connection (plan decision D18) — used when an app server explicitly
    /// closes a client that turns out not to be local to the node the app server's own WebSocket
    /// terminates on. <paramref name="allowReconnect"/> is normally <c>false</c> here (a deliberate
    /// close), unlike the server-connection-loss fan-out in <see cref="UnregisterServerConnectionAsync"/>
    /// (normally <c>true</c>) — both ultimately call the same
    /// <see cref="Observers.IHubObserver.OnCloseConnection"/>.
    /// </summary>
    [Alias("CloseConnection")]
    Task CloseConnectionAsync(string targetNodeId, string connectionId, string? error, bool allowReconnect);

    /// <summary>Phase 3 Slice 7 fix — same targeted, single-node shape as
    /// <see cref="CloseConnectionAsync"/>: <paramref name="targetNodeId"/> is the connection's actual
    /// owner (already resolved by the caller), which the app server that sent the originating
    /// <c>AddToGroup</c> envelope may not share a node with once server-connection assignment is
    /// cluster-wide (plan decision D18).</summary>
    [Alias("AddToGroupCrossNode")]
    Task AddToGroupCrossNodeAsync(string targetNodeId, string connectionId, string groupName);

    /// <summary>Same cross-node correction as <see cref="AddToGroupCrossNodeAsync"/>, for group removal.</summary>
    [Alias("RemoveFromGroupCrossNode")]
    Task RemoveFromGroupCrossNodeAsync(string targetNodeId, string connectionId, string groupName);

    /// <summary>Operational introspection, not just a test hook — useful for a future readiness
    /// check to answer "does this hub have any node subscribed at all". Exercised directly by the
    /// Slice 2 eviction gate, which has no other way to observe that a dead node's subscription was
    /// actually removed rather than merely skipped on this one call.</summary>
    [Alias("GetSubscriberCount")]
    Task<int> GetSubscriberCountAsync();

    /// <summary>Test-only diagnostic hook for Phase 3 Slice 2's grain-deactivation gate (finding
    /// 5): forces this grain out of memory so the test can observe that
    /// <see cref="Observers.ObserverHeartbeatService"/>'s next heartbeat re-subscribes every node
    /// within one interval. Never called by production code.</summary>
    [Alias("DeactivateForTesting")]
    Task DeactivateForTestingAsync();
}
