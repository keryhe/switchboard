namespace Keryhe.Switchboard.Core;

/// <summary>A connection as seen by a node-local index lookup — just enough for fan-out to pick the
/// right payload and find the transport (plan decision D14). <see cref="HubProtocol"/> is null until
/// that connection's handshake completes; callers fall back to the sender's own protocol.</summary>
public readonly record struct LocalConnection(string ConnectionId, string? HubProtocol);

/// <summary>
/// Node-local index of everything this node needs to fan out without a distributed lookup:
/// transport handles, hub/group/user membership, and each connection's negotiated hub protocol
/// (plan decision D14). Synchronous by design — Phase 3's fan-out shape is "local index, then one
/// backplane publish", so this sits on the hot path and must never involve a distributed lookup or
/// even a Task allocation. Never replicated to Orleans grain state (Phase 3) — this is only
/// meaningful on the node that physically owns the socket.
/// </summary>
public interface ILocalTransportRegistry
{
    void Register(IClientTransport transport, string hubName, string? userId);
    void Unregister(string connectionId);
    IClientTransport? Get(string connectionId);

    /// <summary>Called once the connection's handshake completes and its hub protocol is known.</summary>
    void SetHubProtocol(string connectionId, string hubProtocol);
    string? GetHubProtocol(string connectionId);

    void AddToGroup(string connectionId, string groupName);
    void RemoveFromGroup(string connectionId, string groupName);

    /// <summary>Every connection for a hub that is local to this node.</summary>
    IEnumerable<LocalConnection> GetConnectionsForHub(string hubName);

    /// <summary>
    /// Every hub name this node has ever locally registered a <em>client</em> connection for (Phase
    /// 3 Slice 7 fix) — like <c>IHubRegistry.GetAllHubs()</c>'s server-connection-driven set, an
    /// entry sticks once seen, even after every connection for it disconnects. Needed because a
    /// node's set of hubs it must subscribe an observer to (<c>ObserverHeartbeatService</c>) cannot
    /// be derived from server connections alone: a node can have live clients for a hub with zero
    /// local app-server connections (D18's own "zero server connections" gate, and the ordinary
    /// window right after a node restart before its own app-server pool reconnects) — without this,
    /// such a node never subscribes and can never receive a cross-node targeted/group/broadcast
    /// message for those clients at all.
    /// </summary>
    IEnumerable<string> GetKnownHubNames();

    /// <summary>Every member of a group that is local to this node — not necessarily every member
    /// of the group cluster-wide.</summary>
    IEnumerable<LocalConnection> GetGroupMembers(string hubName, string groupName);

    /// <summary>Every connection for a user that is local to this node — not necessarily every
    /// connection that user has cluster-wide.</summary>
    IEnumerable<LocalConnection> GetUserConnections(string hubName, string userId);
}
