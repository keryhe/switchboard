using Orleans;

namespace Keryhe.Switchboard.Orleans.Observers;

/// <summary>
/// Per-node observer interface for cross-silo delivery (ADR-003, plan decision D16). Every method
/// returns <c>Task</c>, not <c>void</c> — verified necessary, not stylistic: a <c>void</c> observer
/// method gives the calling grain no exception to catch, so a dead node's reference can never be
/// detected or evicted (finding 4). The implementation itself (<see cref="HubObserverImpl"/>) must
/// never await client I/O inside these methods — it writes to bounded per-connection channels
/// (<c>DropWrite</c>) exactly as local fan-out does and returns; the <c>Task</c> exists for failure
/// signalling, not delivery confirmation, so ADR-003's "fire-and-forget, may be lost under
/// partition" semantics are unchanged.
/// </summary>
/// <remarks>
/// <see cref="OnBroadcast"/> is the only method with real behavior as of Phase 3 Slice 2 — group,
/// user, and targeted cross-node delivery (plan decision D17) land in Slice 3, and the app-server
/// synthetic-connection paths (plan decision D18) in Slice 4. All six are declared together because
/// they are one interface decision (D16); which ones anything actually calls yet is a slice
/// boundary, not an interface one — see <see cref="HubObserverImpl"/>'s per-method doc comments.
/// </remarks>
[Alias("Keryhe.Switchboard.Orleans.Observers.IHubObserver")]
public interface IHubObserver : IGrainObserver
{
    [Alias("OnBroadcast")]
    Task OnBroadcast(byte[] payload, Dictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds);

    [Alias("OnGroupMessage")]
    Task OnGroupMessage(string groupName, byte[] payload, Dictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds);

    [Alias("OnUserMessage")]
    Task OnUserMessage(string userId, byte[] payload, Dictionary<string, byte[]>? payloadsByProtocol);

    [Alias("OnConnectionMessage")]
    Task OnConnectionMessage(string connectionId, byte[] payload, string hubProtocol);

    /// <summary>
    /// Phase 3 Slice 4 (plan decision D18) — delivers an arbitrary, already-serialized
    /// <c>ServerEnvelope</c> (open/client-message/close — anything service→app-server) to
    /// <paramref name="serverConnectionId"/>, one of this node's own local server connections
    /// (bare id, not node-qualified — the caller already resolved the node via
    /// <see cref="Grains.IHubGrain.SendServerEnvelopeAsync"/>'s <c>targetNodeId</c>). One general
    /// method rather than a separate one per envelope type, reusing the exact wire format already
    /// used between app servers and the service (<c>ServerEnvelopeSerializer</c>) instead of adding
    /// a parallel encoding just for the cross-node hop.
    /// </summary>
    [Alias("OnServerEnvelope")]
    Task OnServerEnvelope(string serverConnectionId, byte[] serializedEnvelope);

    /// <summary>Phase 3 Slice 4 (plan decision D18) — closes one client connection local to this
    /// node: written to a client that turns out to be assigned a server connection that dropped
    /// (<c>allowReconnect: true</c>) or explicitly closed by its app server
    /// (<c>allowReconnect: false</c>), wherever in the cluster the request to close it originated.</summary>
    [Alias("OnCloseConnection")]
    Task OnCloseConnection(string connectionId, string? error, bool allowReconnect);

    /// <summary>Phase 3 Slice 7 fix — mutates this node's own <c>ILocalTransportRegistry</c> group
    /// index for a connection local to it, on behalf of a cross-node <c>AddToGroup</c> envelope
    /// (plan decision D18 made this reachable: the app server that sent it may not share a node with
    /// the connection).</summary>
    [Alias("OnAddToGroup")]
    Task OnAddToGroup(string connectionId, string groupName);

    /// <summary>Same cross-node correction as <see cref="OnAddToGroup"/>, for group removal.</summary>
    [Alias("OnRemoveFromGroup")]
    Task OnRemoveFromGroup(string connectionId, string groupName);
}
