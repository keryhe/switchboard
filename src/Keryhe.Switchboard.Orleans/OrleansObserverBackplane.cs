using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Orleans.Grains;
using Microsoft.Extensions.Logging;
using Orleans;

namespace Keryhe.Switchboard.Orleans;

/// <summary>
/// Orleans grain-observer backplane (ADR-003, plan decision D16) — the Phase 3 replacement for
/// <c>NoOpBackplane</c> under <c>UseOrleansCluster</c>. All four methods have real cross-node
/// behavior as of Phase 3 Slice 3: broadcast/group/user publish by name (plan decision D17) via
/// <see cref="IHubGrain"/>'s fan-out, letting every other subscribed node resolve membership
/// against its own local index; <see cref="PublishToConnectionAsync"/> is the one exception to
/// "fan out to everyone" — it resolves the connection's owning node first
/// (<see cref="IConnectionGrain.GetOwnerNodeAsync"/>) and calls only that node's observer, since a
/// targeted send has exactly one recipient.
/// </summary>
public sealed class OrleansObserverBackplane(IGrainFactory grainFactory, ILogger<OrleansObserverBackplane> logger) : IBackplane
{
    public Task PublishBroadcastAsync(string hubName, byte[] payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds, string originNodeId, CancellationToken ct) =>
        grainFactory.GetGrain<IHubGrain>(hubName).BroadcastAsync(payload, ToDictionary(payloadsByProtocol), excludedConnectionIds, originNodeId);

    public Task PublishGroupMessageAsync(string hubName, string groupName, byte[] payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds, string originNodeId, CancellationToken ct) =>
        grainFactory.GetGrain<IHubGrain>(hubName).GroupMessageAsync(groupName, payload, ToDictionary(payloadsByProtocol), excludedConnectionIds, originNodeId);

    public Task PublishUserMessageAsync(string hubName, string userId, byte[] payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, string originNodeId, CancellationToken ct) =>
        grainFactory.GetGrain<IHubGrain>(hubName).UserMessageAsync(userId, payload, ToDictionary(payloadsByProtocol), originNodeId);

    /// <summary>
    /// Unlike the three fan-out methods above, a targeted send does not publish to every
    /// subscriber — it looks up exactly which node owns <paramref name="connectionId"/> and calls
    /// only that node's observer, mirroring <c>DefaultMessageRouter.RouteToConnectionAsync</c>'s
    /// "local hit writes directly, local miss asks the backplane" shape one level up: the
    /// connection isn't local to this node (that's why this was called at all), so the owner
    /// lookup finds whichever *other* node it is local to. A null lookup means the connection has
    /// since disconnected (or never existed) — logged once and dropped, not retried.
    /// </summary>
    public async Task PublishToConnectionAsync(string connectionId, byte[] payload, string hubProtocol, string originNodeId, CancellationToken ct)
    {
        var location = await grainFactory.GetGrain<IConnectionGrain>(connectionId).GetOwnerNodeAsync();
        if (location is null)
        {
            logger.LogWarning("PublishToConnectionAsync: connection {ConnectionId} is not registered (may have disconnected); dropping message.", connectionId);
            return;
        }

        if (location.OwnerNodeId == originNodeId)
        {
            // The sender's own local delivery already covers this — reaching here would mean the
            // registry says this node owns it but the local transport lookup just missed, which
            // only a disconnect racing this exact call could cause. Nothing to retry against.
            return;
        }

        await grainFactory.GetGrain<IHubGrain>(location.HubName).SendToConnectionAsync(location.OwnerNodeId, connectionId, payload, hubProtocol);
    }

    /// <summary>
    /// Plan decision D18 (Phase 3 Slice 4) — <paramref name="serverConnectionRef"/> already names
    /// the exact target node (it was minted by <c>OrleansServerConnectionSelector.AssignConnectionAsync</c>),
    /// so this is a direct targeted call, no owner lookup needed first (unlike
    /// <see cref="PublishToConnectionAsync"/>, which starts from a bare connectionId).
    /// </summary>
    public Task PublishServerEnvelopeAsync(string hubName, string serverConnectionRef, byte[] serializedEnvelope, CancellationToken ct)
    {
        if (!Core.ServerConnectionRef.TryParse(serverConnectionRef, out var nodeId, out var serverConnectionId))
        {
            logger.LogWarning("PublishServerEnvelopeAsync: malformed server connection reference {ServerConnectionRef}.", serverConnectionRef);
            return Task.CompletedTask;
        }

        return grainFactory.GetGrain<IHubGrain>(hubName).SendServerEnvelopeAsync(nodeId, serverConnectionId, serializedEnvelope);
    }

    /// <summary>
    /// Plan decision D18 (Phase 3 Slice 4) — same owner-lookup-then-targeted-call shape as
    /// <see cref="PublishToConnectionAsync"/>: an app server explicitly closing a client it doesn't
    /// share a node with.
    /// </summary>
    public async Task PublishCloseConnectionAsync(string connectionId, string? error, bool allowReconnect, string originNodeId, CancellationToken ct)
    {
        var location = await grainFactory.GetGrain<IConnectionGrain>(connectionId).GetOwnerNodeAsync();
        if (location is null)
        {
            logger.LogWarning("PublishCloseConnectionAsync: connection {ConnectionId} is not registered (may have disconnected); dropping close.", connectionId);
            return;
        }

        if (location.OwnerNodeId == originNodeId)
        {
            return;
        }

        await grainFactory.GetGrain<IHubGrain>(location.HubName).CloseConnectionAsync(location.OwnerNodeId, connectionId, error, allowReconnect);
    }

    /// <summary>
    /// Phase 3 Slice 7 fix — same owner-lookup-then-targeted-call shape as
    /// <see cref="PublishCloseConnectionAsync"/>: group membership is node-local state, so an
    /// <c>AddToGroup</c> envelope that arrived on a node other than the connection's own must be
    /// forwarded there rather than mutating this node's index for a connection it doesn't have.
    /// </summary>
    public async Task PublishAddToGroupAsync(string connectionId, string groupName, string originNodeId, CancellationToken ct)
    {
        var location = await grainFactory.GetGrain<IConnectionGrain>(connectionId).GetOwnerNodeAsync();
        if (location is null)
        {
            logger.LogWarning("PublishAddToGroupAsync: connection {ConnectionId} is not registered (may have disconnected); dropping.", connectionId);
            return;
        }

        if (location.OwnerNodeId == originNodeId)
        {
            return;
        }

        await grainFactory.GetGrain<IHubGrain>(location.HubName).AddToGroupCrossNodeAsync(location.OwnerNodeId, connectionId, groupName);
    }

    /// <summary>Same cross-node correction as <see cref="PublishAddToGroupAsync"/>, for group removal.</summary>
    public async Task PublishRemoveFromGroupAsync(string connectionId, string groupName, string originNodeId, CancellationToken ct)
    {
        var location = await grainFactory.GetGrain<IConnectionGrain>(connectionId).GetOwnerNodeAsync();
        if (location is null)
        {
            logger.LogWarning("PublishRemoveFromGroupAsync: connection {ConnectionId} is not registered (may have disconnected); dropping.", connectionId);
            return;
        }

        if (location.OwnerNodeId == originNodeId)
        {
            return;
        }

        await grainFactory.GetGrain<IHubGrain>(location.HubName).RemoveFromGroupCrossNodeAsync(location.OwnerNodeId, connectionId, groupName);
    }

    private static Dictionary<string, byte[]>? ToDictionary(IReadOnlyDictionary<string, byte[]>? source) =>
        source is null ? null : new Dictionary<string, byte[]>(source);
}
