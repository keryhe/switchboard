using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Protocol;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Server.Routing;

/// <summary>
/// Central dispatch engine (04-design.md §5). Fan-out (<see cref="BroadcastAsync"/>,
/// <see cref="SendToGroupAsync"/>, <see cref="SendToUserAsync"/>) delivers to this node's own
/// targets from the node-local index, then publishes once to <see cref="IBackplane"/> for every
/// other node to do the same (plan decision D14) — the distributed <see cref="IConnectionRegistry"/>
/// is never enumerated on this hot path; it remains the source of truth for ownership lookups,
/// disconnect cleanup, and the Phase 4 management API. <see cref="RouteToConnectionAsync"/> follows
/// the same shape: a local hit writes directly, a local miss publishes to the backplane instead of
/// just logging and dropping.
/// </summary>
public sealed class DefaultMessageRouter(
    IConnectionRegistry connectionRegistry,
    IHubRegistry hubRegistry,
    ILocalTransportRegistry localTransportRegistry,
    IBackplane backplane,
    IOptions<SwitchboardOptions> options,
    ILogger<DefaultMessageRouter> logger) : IMessageRouter
{
    private static readonly string[] NoExclusions = [];

    /// <summary>Connections per fan-out batch (04-design.md §5).</summary>
    private const int BatchSize = 256;

    /// <summary>Bounded parallelism within a batch — writes are non-blocking
    /// (<c>DropWrite</c>), so this bounds concurrent registry/channel lookups, not I/O wait.</summary>
    private static readonly int MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount);

    private readonly string _nodeId = options.Value.NodeId;

    public async ValueTask RouteClientMessageAsync(string connectionId, ReadOnlyMemory<byte> payload, string hubProtocol, CancellationToken ct)
    {
        var state = await connectionRegistry.GetAsync(connectionId, ct);
        if (state is null)
        {
            logger.LogWarning("RouteClientMessageAsync: unknown connection {ConnectionId}.", connectionId);
            return;
        }

        // ServerConnectionId is a ServerConnectionRef-formatted, node-qualified reference (plan
        // decision D18) — the assigned server connection is no longer guaranteed local to the node
        // this client physically connected to (finding 12).
        if (!ServerConnectionRef.TryParse(state.ServerConnectionId, out var ownerNodeId, out var serverConnectionId))
        {
            logger.LogWarning("RouteClientMessageAsync: malformed server connection reference {ServerConnectionId} for {ConnectionId}.", state.ServerConnectionId, connectionId);
            return;
        }

        var envelope = new ServerEnvelope
        {
            Type = ServerEnvelopeType.ClientMessage,
            ConnectionId = connectionId,
            HubProtocol = hubProtocol,
            Payload = payload.ToArray(),
        };

        if (ownerNodeId == _nodeId)
        {
            var serverConnection = hubRegistry.GetHub(state.HubName)?.ServerConnections.GetValueOrDefault(serverConnectionId)?.Connection;
            if (serverConnection is null)
            {
                logger.LogWarning("RouteClientMessageAsync: server connection {ServerConnectionId} for {ConnectionId} is gone.", state.ServerConnectionId, connectionId);
                return;
            }

            await serverConnection.SendAsync(envelope, ct);
            return;
        }

        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        ServerEnvelopeSerializer.Write(writer, envelope);
        await backplane.PublishServerEnvelopeAsync(state.HubName, state.ServerConnectionId, writer.WrittenMemory.ToArray(), ct);
    }

    public async ValueTask RouteToConnectionAsync(string connectionId, ReadOnlyMemory<byte> payload, string hubProtocol, CancellationToken ct)
    {
        var transport = localTransportRegistry.Get(connectionId);
        if (transport is not null)
        {
            await transport.Output.Writer.WriteAsync(payload, ct);
            return;
        }

        // Not local. Phase 1/2: NoOpBackplane, so this is still a drop in practice — logged at
        // debug rather than warning, since "may live on another node" is the expected case once
        // Phase 3's backplane makes it real, not an error condition.
        logger.LogDebug("RouteToConnectionAsync: no local transport for {ConnectionId}; publishing to backplane.", connectionId);
        await backplane.PublishToConnectionAsync(connectionId, payload.ToArray(), hubProtocol, _nodeId, ct);
    }

    public ValueTask BroadcastAsync(string hubName, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, IReadOnlySet<string>? excludedConnectionIds, CancellationToken ct) =>
        FanOutAsync(
            localTransportRegistry.GetConnectionsForHub(hubName),
            payload, hubProtocol, payloadsByProtocol, excludedConnectionIds,
            publishAsync: innerCt => backplane.PublishBroadcastAsync(
                hubName, payload.ToArray(), hubProtocol, payloadsByProtocol, ToArray(excludedConnectionIds), _nodeId, innerCt),
            ct);

    public ValueTask SendToGroupAsync(string hubName, string groupName, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, IReadOnlySet<string>? excludedConnectionIds, CancellationToken ct) =>
        FanOutAsync(
            localTransportRegistry.GetGroupMembers(hubName, groupName),
            payload, hubProtocol, payloadsByProtocol, excludedConnectionIds,
            publishAsync: innerCt => backplane.PublishGroupMessageAsync(
                hubName, groupName, payload.ToArray(), hubProtocol, payloadsByProtocol, ToArray(excludedConnectionIds), _nodeId, innerCt),
            ct);

    public ValueTask SendToUserAsync(string hubName, string userId, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, CancellationToken ct) =>
        FanOutAsync(
            localTransportRegistry.GetUserConnections(hubName, userId),
            payload, hubProtocol, payloadsByProtocol, excludedConnectionIds: null,
            publishAsync: innerCt => backplane.PublishUserMessageAsync(
                hubName, userId, payload.ToArray(), hubProtocol, payloadsByProtocol, _nodeId, innerCt),
            ct);

    /// <summary>
    /// Local index, then one backplane publish (plan decision D14): batch this node's own targets
    /// from <paramref name="targets"/> and write with bounded parallelism — every write lands on a
    /// per-connection channel configured with <c>DropWrite</c> (00-review-findings.md), so a slow
    /// client sheds messages instead of stalling fan-out for everyone else — then, once local
    /// delivery is done, publish exactly once so every other node can do the same for its own
    /// targets.
    /// </summary>
    private async ValueTask FanOutAsync(
        IEnumerable<LocalConnection> targets,
        ReadOnlyMemory<byte> payload,
        string hubProtocol,
        IReadOnlyDictionary<string, byte[]>? payloadsByProtocol,
        IReadOnlySet<string>? excludedConnectionIds,
        Func<CancellationToken, Task> publishAsync,
        CancellationToken ct)
    {
        var batch = new List<(string ConnectionId, byte[] Payload)>(BatchSize);

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            if (excludedConnectionIds?.Contains(target.ConnectionId) == true)
            {
                continue;
            }

            var targetProtocol = target.HubProtocol ?? "json";
            byte[]? bytes = null;
            if (payloadsByProtocol is not null && payloadsByProtocol.TryGetValue(targetProtocol, out var protocolBytes))
            {
                bytes = protocolBytes;
            }
            else if (targetProtocol == hubProtocol)
            {
                bytes = payload.ToArray();
            }

            if (bytes is null)
            {
                logger.LogWarning(
                    "Fan-out skipped connection {ConnectionId}: no payload for its negotiated protocol {Protocol} (plan decision D7).",
                    target.ConnectionId, targetProtocol);
                continue;
            }

            batch.Add((target.ConnectionId, bytes));
            if (batch.Count == BatchSize)
            {
                await WriteBatchAsync(batch, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await WriteBatchAsync(batch, ct);
        }

        await publishAsync(ct);
    }

    private async Task WriteBatchAsync(List<(string ConnectionId, byte[] Payload)> batch, CancellationToken ct)
    {
        await Parallel.ForEachAsync(
            batch,
            new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = ct },
            async (item, token) =>
            {
                var transport = localTransportRegistry.Get(item.ConnectionId);
                if (transport is not null)
                {
                    await transport.Output.Writer.WriteAsync(item.Payload, token);
                }
            });
    }

    private static string[] ToArray(IReadOnlySet<string>? excludedConnectionIds) =>
        excludedConnectionIds is null or { Count: 0 } ? NoExclusions : excludedConnectionIds.ToArray();
}
