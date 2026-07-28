namespace Keryhe.Switchboard.Core;

/// <summary>
/// Cross-node fan-out. Phase 1/2: <c>NoOpBackplane</c> (single node — every call site already
/// delivers to every local target before calling this, per plan decision D14, so a no-op here is
/// behavior-preserving). Phase 3: Orleans grain observers (ADR-003).
/// <paramref name="originNodeId"/> lets the receiving side skip the node that already handled its
/// own local delivery; <paramref name="payloadsByProtocol"/> carries one encoding per hub protocol
/// (plan decision D7) since the receiving node's local targets may be mixed-protocol and unknown to
/// the sender in advance — mirrors <see cref="IMessageRouter"/>'s own fan-out signatures.
/// </summary>
public interface IBackplane
{
    Task PublishBroadcastAsync(string hubName, byte[] payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds, string originNodeId, CancellationToken ct);
    Task PublishGroupMessageAsync(string hubName, string groupName, byte[] payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds, string originNodeId, CancellationToken ct);
    Task PublishUserMessageAsync(string hubName, string userId, byte[] payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, string originNodeId, CancellationToken ct);

    /// <summary>Single payload/protocol only — a targeted send has exactly one recipient, whose
    /// protocol the sender already knows (mirrors <see cref="IMessageRouter.RouteToConnectionAsync"/>).</summary>
    Task PublishToConnectionAsync(string connectionId, byte[] payload, string hubProtocol, string originNodeId, CancellationToken ct);

    /// <summary>
    /// Phase 3 Slice 4 (plan decision D18) — delivers an arbitrary, already-serialized
    /// <c>ServerEnvelope</c> (open/client-message/close — anything service-to-app-server) to the
    /// specific server connection <paramref name="serverConnectionRef"/> names, wherever in the
    /// cluster it lives. <paramref name="serverConnectionRef"/> is the
    /// <see cref="ServerConnectionRef"/>-formatted node-qualified id — cluster-wide server-connection
    /// assignment means the connection a client's messages are routed to is no longer guaranteed
    /// local to the node the client itself is connected to. Unlike the fan-out methods above, this
    /// targets exactly one node, mirroring <see cref="PublishToConnectionAsync"/>'s own shape one
    /// level up (service→app-server instead of service→client).
    /// </summary>
    Task PublishServerEnvelopeAsync(string hubName, string serverConnectionRef, byte[] serializedEnvelope, CancellationToken ct);

    /// <summary>
    /// Phase 3 Slice 4 (plan decision D18) — an app server explicitly closing one of its clients
    /// (<c>ServerEnvelopeType.CloseConnection</c>) may no longer share a node with that client, since
    /// server-connection assignment is cluster-wide. Resolves <paramref name="connectionId"/>'s
    /// owning node and delivers the close there; a null lookup (already disconnected) is a no-op.
    /// <paramref name="allowReconnect"/> distinguishes this (an explicit close, normally false) from
    /// the server-connection-loss case that also flows through <c>IHubObserver.OnCloseConnection</c>
    /// (normally true).
    /// </summary>
    Task PublishCloseConnectionAsync(string connectionId, string? error, bool allowReconnect, string originNodeId, CancellationToken ct);

    /// <summary>
    /// Phase 3 Slice 7 fix — group membership is node-local state (<c>ILocalTransportRegistry</c>,
    /// plan decision D14/D0), but the app server that sends <c>AddToGroup</c> is only guaranteed to
    /// share a node with the connection it names in single-node mode. Once server-connection
    /// assignment is cluster-wide (plan decision D18, Phase 3 Slice 4), a client's assigned app
    /// server — and therefore which node's <c>RoutingServerEnvelopeDispatcher</c> receives this
    /// envelope — is no longer guaranteed local to the node the client itself is connected to.
    /// Resolves <paramref name="connectionId"/>'s owning node and mutates <em>that</em> node's local
    /// group index; a null lookup (already disconnected) is a no-op, mirroring
    /// <see cref="PublishCloseConnectionAsync"/>'s own shape.
    /// </summary>
    Task PublishAddToGroupAsync(string connectionId, string groupName, string originNodeId, CancellationToken ct);

    /// <summary>Same cross-node correction as <see cref="PublishAddToGroupAsync"/>, for group removal.</summary>
    Task PublishRemoveFromGroupAsync(string connectionId, string groupName, string originNodeId, CancellationToken ct);
}
