using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;

namespace Keryhe.Switchboard.Server.Routing;

/// <summary>
/// Central dispatch engine (04-design.md §5). Group and user fan-out
/// (<see cref="SendToGroupAsync"/>, <see cref="SendToUserAsync"/>) were a deliberate
/// log-and-no-op in Phase 1 (plan decision D3); Phase 2 implements real routing using the
/// membership/user indexes <see cref="IConnectionRegistry"/> already tracked, plus mixed-protocol
/// payload selection per connection (plan decision D7).
/// </summary>
public sealed class DefaultMessageRouter(
    IConnectionRegistry connectionRegistry,
    IHubRegistry hubRegistry,
    ILocalTransportRegistry localTransportRegistry,
    ILogger<DefaultMessageRouter> logger) : IMessageRouter
{
    /// <summary>Connections per fan-out batch (04-design.md §5).</summary>
    private const int BatchSize = 256;

    /// <summary>Bounded parallelism within a batch — writes are non-blocking
    /// (<c>DropWrite</c>), so this bounds concurrent registry/channel lookups, not I/O wait.</summary>
    private static readonly int MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount);

    public async ValueTask RouteClientMessageAsync(string connectionId, ReadOnlyMemory<byte> payload, string hubProtocol, CancellationToken ct)
    {
        var state = await connectionRegistry.GetAsync(connectionId, ct);
        if (state is null)
        {
            logger.LogWarning("RouteClientMessageAsync: unknown connection {ConnectionId}.", connectionId);
            return;
        }

        var serverConnection = hubRegistry.GetHub(state.HubName)?.ServerConnections.GetValueOrDefault(state.ServerConnectionId)?.Connection;
        if (serverConnection is null)
        {
            logger.LogWarning("RouteClientMessageAsync: server connection {ServerConnectionId} for {ConnectionId} is gone.", state.ServerConnectionId, connectionId);
            return;
        }

        await serverConnection.SendAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.ClientMessage,
            ConnectionId = connectionId,
            HubProtocol = hubProtocol,
            Payload = payload.ToArray(),
        }, ct);
    }

    public async ValueTask RouteToConnectionAsync(string connectionId, ReadOnlyMemory<byte> payload, string hubProtocol, CancellationToken ct)
    {
        var transport = localTransportRegistry.Get(connectionId);
        if (transport is null)
        {
            logger.LogWarning("RouteToConnectionAsync: no local transport for {ConnectionId} (may have disconnected or live on another node).", connectionId);
            return;
        }

        await transport.Output.Writer.WriteAsync(payload, ct);
    }

    public ValueTask BroadcastAsync(string hubName, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, IReadOnlySet<string>? excludedConnectionIds, CancellationToken ct) =>
        FanOutAsync(connectionRegistry.GetAllAsync(hubName, ct), payload, hubProtocol, payloadsByProtocol, excludedConnectionIds, ct);

    public ValueTask SendToGroupAsync(string hubName, string groupName, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, IReadOnlySet<string>? excludedConnectionIds, CancellationToken ct) =>
        FanOutAsync(connectionRegistry.GetGroupMembersAsync(hubName, groupName, ct), payload, hubProtocol, payloadsByProtocol, excludedConnectionIds, ct);

    public ValueTask SendToUserAsync(string hubName, string userId, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, CancellationToken ct) =>
        FanOutAsync(connectionRegistry.GetUserConnectionsAsync(hubName, userId, ct), payload, hubProtocol, payloadsByProtocol, excludedConnectionIds: null, ct);

    /// <summary>
    /// Partitioned parallel fan-out (04-design.md §5): batch the target set, then write each
    /// batch with bounded parallelism. Every write lands on a per-connection channel configured
    /// with <c>DropWrite</c> (00-review-findings.md), so a slow client sheds messages instead of
    /// stalling fan-out for everyone else — this method never awaits a slow client.
    /// </summary>
    private async ValueTask FanOutAsync(
        IAsyncEnumerable<Core.Models.ClientConnectionState> targets,
        ReadOnlyMemory<byte> payload,
        string hubProtocol,
        IReadOnlyDictionary<string, byte[]>? payloadsByProtocol,
        IReadOnlySet<string>? excludedConnectionIds,
        CancellationToken ct)
    {
        var batch = new List<(string ConnectionId, byte[] Payload)>(BatchSize);

        await foreach (var state in targets.WithCancellation(ct))
        {
            if (excludedConnectionIds?.Contains(state.ConnectionId) == true)
            {
                continue;
            }

            var targetProtocol = state.HubProtocol ?? "json";
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
                    state.ConnectionId, targetProtocol);
                continue;
            }

            batch.Add((state.ConnectionId, bytes));
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
}
