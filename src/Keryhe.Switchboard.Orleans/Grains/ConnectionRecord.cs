using Keryhe.Switchboard.Core.Models;

namespace Keryhe.Switchboard.Orleans.Grains;

/// <summary>
/// Grain-serializable twin of <see cref="ClientConnectionState"/> (minus the node-local
/// <c>TransportHandle</c> field already removed in Phase 3 Slice 0 — plan decision D15). Held as
/// <see cref="IConnectionGrain"/> state; <c>Keryhe.Switchboard.Orleans.OrleansConnectionRegistry</c>
/// converts to/from <see cref="ClientConnectionState"/> at the registry boundary so nothing outside
/// this project needs to know the grain-state shape. <c>[Id(n)]</c> ordering is a wire contract on
/// par with <c>ServerEnvelope</c>'s <c>[Key(n)]</c> (plan decision D20) — append only, never reorder
/// or reuse a slot, since this is persisted and read back across a rolling upgrade.
/// </summary>
[GenerateSerializer]
public sealed class ConnectionRecord
{
    /// <summary>The node that accepted this connection — set once at register time, read by
    /// Phase 3 Slice 4+ cross-node routing. Not yet consulted by anything in Slice 1.</summary>
    [Id(0)]
    public required string OwnerNodeId { get; init; }

    [Id(1)]
    public required string HubName { get; init; }

    [Id(2)]
    public string? UserId { get; init; }

    [Id(3)]
    public required string ConnectionToken { get; init; }

    [Id(4)]
    public required TransportType Transport { get; init; }

    [Id(5)]
    public string? HubProtocol { get; set; }

    [Id(6)]
    public required string ServerConnectionId { get; set; }

    [Id(7)]
    public required DateTimeOffset ConnectedAt { get; init; }

    [Id(8)]
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>Mirrored group membership — <see cref="IGroupGrain"/> is the authoritative source
    /// (plan decision D15); this copy exists only so <see cref="IConnectionGrain.UnregisterAsync"/>
    /// can remove itself from every group it joined without a cross-grain scan.</summary>
    [Id(9)]
    public HashSet<string> Groups { get; init; } = [];
}
