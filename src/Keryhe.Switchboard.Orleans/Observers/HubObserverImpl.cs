using System.Buffers;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.Protocol.Framing;
using Microsoft.Extensions.Logging;

namespace Keryhe.Switchboard.Orleans.Observers;

/// <summary>
/// One instance per hub this node knows about (ADR-003: "each node registers a local
/// HubObserverImpl... with the relevant hub grain") — a plain class, not a grain, holding
/// references to the node-local <see cref="ILocalTransportRegistry"/> (client-facing delivery) and
/// (Phase 3 Slice 4, plan decision D18) <see cref="IHubRegistry"/>/<see cref="IConnectionRegistry"/>
/// (app-server-facing delivery and full connection teardown) so delivery never leaves the process.
/// Registered by <see cref="ObserverHeartbeatService"/>.
/// </summary>
public sealed class HubObserverImpl(
    string hubName,
    ILocalTransportRegistry localTransportRegistry,
    IHubRegistry hubRegistry,
    IConnectionRegistry connectionRegistry,
    ILogger logger) : IHubObserver
{
    /// <summary>
    /// Cross-node broadcast delivery — the local half of <c>DefaultMessageRouter.FanOutAsync</c>'s
    /// protocol-selection logic (plan decision D7), reused here because the shape is identical:
    /// this node's own targets, resolved from the local index, each picking its own payload by
    /// negotiated protocol. Never awaits client I/O beyond the bounded per-connection channel write
    /// (<c>DropWrite</c>) — matches ADR-003's fire-and-forget semantics.
    /// </summary>
    public Task OnBroadcast(byte[] payload, Dictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds) =>
        DeliverAsync(localTransportRegistry.GetConnectionsForHub(hubName), payloadsByProtocol, ToExcludedSet(excludedConnectionIds));

    /// <summary>
    /// Cross-node group delivery (plan decision D17) — published by group name (<see cref="Grains.IHubGrain.GroupMessageAsync"/>),
    /// resolved here against this node's own local index, same protocol-selection/exclusion shape
    /// as <see cref="OnBroadcast"/>.
    /// </summary>
    public Task OnGroupMessage(string groupName, byte[] payload, Dictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds) =>
        DeliverAsync(localTransportRegistry.GetGroupMembers(hubName, groupName), payloadsByProtocol, ToExcludedSet(excludedConnectionIds));

    /// <summary>Cross-node user delivery (plan decision D17) — same shape as <see cref="OnGroupMessage"/>,
    /// no exclusion set (a user send never excludes a connection).</summary>
    public Task OnUserMessage(string userId, byte[] payload, Dictionary<string, byte[]>? payloadsByProtocol) =>
        DeliverAsync(localTransportRegistry.GetUserConnections(hubName, userId), payloadsByProtocol, excluded: null);

    /// <summary>
    /// Cross-node targeted delivery (plan decision D17) — unlike the fan-out methods above, the
    /// caller (<see cref="Grains.IHubGrain.SendToConnectionAsync"/>) already resolved this exact
    /// node as the connection's owner, so there is exactly one payload/protocol (mirrors
    /// <see cref="Core.IBackplane.PublishToConnectionAsync"/>) and no protocol lookup needed — the
    /// caller already encoded in the target's negotiated protocol. If the connection is gone from
    /// this node's local index by the time this arrives (a disconnect racing the cross-node call),
    /// that is logged and dropped rather than treated as an error.
    /// </summary>
    public async Task OnConnectionMessage(string connectionId, byte[] payload, string hubProtocol)
    {
        var transport = localTransportRegistry.Get(connectionId);
        if (transport is null)
        {
            logger.LogWarning("Cross-node targeted send skipped: connection {ConnectionId} not found locally on this node.", connectionId);
            return;
        }

        await transport.Output.Writer.WriteAsync(payload);
    }

    /// <summary>
    /// Delivers a service→app-server envelope (open/client-message/close, plan decision D18) to one
    /// of this node's own local server connections — <paramref name="serverConnectionId"/> is a bare
    /// id, since the caller (<see cref="Grains.IHubGrain.SendServerEnvelopeAsync"/>) already resolved
    /// this exact node as the connection's owner. Parses via the same
    /// <c>ServerEnvelopeSerializer</c> used for the real app-server wire format, so the cross-node
    /// hop carries exactly the bytes that would have been sent locally.
    /// </summary>
    public async Task OnServerEnvelope(string serverConnectionId, byte[] serializedEnvelope)
    {
        var buffer = new ReadOnlySequence<byte>(serializedEnvelope);
        if (!ServerEnvelopeSerializer.TryParseEnvelope(buffer, out var envelope, out _, out _) || envelope is null)
        {
            logger.LogWarning("OnServerEnvelope: failed to parse envelope for server connection {ServerConnectionId} on hub {HubName}.", serverConnectionId, hubName);
            return;
        }

        var serverConnection = hubRegistry.GetHub(hubName)?.ServerConnections.GetValueOrDefault(serverConnectionId)?.Connection;
        if (serverConnection is null)
        {
            logger.LogWarning("OnServerEnvelope: server connection {ServerConnectionId} not found locally on hub {HubName}.", serverConnectionId, hubName);
            return;
        }

        await serverConnection.SendAsync(envelope, CancellationToken.None);
    }

    /// <summary>
    /// Closes one connection local to this node — used both when its assigned server connection was
    /// lost cluster-wide (<paramref name="allowReconnect"/> true, plan decision D18) and when an app
    /// server explicitly closed it (false). Does the full teardown
    /// <c>RoutingServerEnvelopeDispatcher.CloseClientConnectionAsync</c> does for a local close:
    /// write the close frame, tear down the transport, and unregister from both the distributed and
    /// local registries — this node owns that connection's only physical presence, so nothing else
    /// will clean it up.
    /// </summary>
    public async Task OnCloseConnection(string connectionId, string? error, bool allowReconnect)
    {
        var transport = localTransportRegistry.Get(connectionId);
        if (transport is null)
        {
            // Already gone (raced a client-initiated disconnect) — nothing to do.
            return;
        }

        var hubProtocol = localTransportRegistry.GetHubProtocol(connectionId) ?? "json";
        var closeFrame = ClientFrameWriter.Close(hubProtocol, error, allowReconnect);
        await transport.Output.Writer.WriteAsync(closeFrame);
        await transport.CloseAsync(error);

        await connectionRegistry.UnregisterAsync(connectionId, CancellationToken.None);
        localTransportRegistry.Unregister(connectionId);
    }

    /// <summary>Phase 3 Slice 7 fix — the connection this names is local to this node (the caller
    /// already resolved that via <see cref="Grains.IConnectionGrain.GetOwnerNodeAsync"/>), so this
    /// is the same local mutation <c>RoutingServerEnvelopeDispatcher</c> makes directly when the
    /// sending app server does share a node with the connection.</summary>
    public Task OnAddToGroup(string connectionId, string groupName)
    {
        localTransportRegistry.AddToGroup(connectionId, groupName);
        return Task.CompletedTask;
    }

    /// <summary>Same cross-node correction as <see cref="OnAddToGroup"/>, for group removal.</summary>
    public Task OnRemoveFromGroup(string connectionId, string groupName)
    {
        localTransportRegistry.RemoveFromGroup(connectionId, groupName);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Shared per-target delivery loop for <see cref="OnBroadcast"/>, <see cref="OnGroupMessage"/>,
    /// and <see cref="OnUserMessage"/> — resolves each local target's own negotiated protocol
    /// (plan decision D7) and never awaits client I/O beyond the bounded per-connection channel
    /// write (<c>DropWrite</c>), matching ADR-003's fire-and-forget semantics.
    /// </summary>
    private async Task DeliverAsync(IEnumerable<LocalConnection> targets, Dictionary<string, byte[]>? payloadsByProtocol, HashSet<string>? excluded)
    {
        foreach (var target in targets)
        {
            if (excluded?.Contains(target.ConnectionId) == true)
            {
                continue;
            }

            var protocol = target.HubProtocol ?? "json";
            if (payloadsByProtocol is null || !payloadsByProtocol.TryGetValue(protocol, out var bytes))
            {
                logger.LogWarning(
                    "Cross-node fan-out skipped connection {ConnectionId}: no payload for its negotiated protocol {Protocol} (plan decision D7).",
                    target.ConnectionId, protocol);
                continue;
            }

            var transport = localTransportRegistry.Get(target.ConnectionId);
            if (transport is not null)
            {
                await transport.Output.Writer.WriteAsync(bytes);
            }
        }
    }

    private static HashSet<string>? ToExcludedSet(string[] excludedConnectionIds) =>
        excludedConnectionIds.Length == 0 ? null : new HashSet<string>(excludedConnectionIds);
}
