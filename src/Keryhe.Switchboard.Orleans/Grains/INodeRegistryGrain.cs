using Orleans;

namespace Keryhe.Switchboard.Orleans.Grains;

/// <summary>Cluster-wide singleton grain mapping <c>nodeId</c> → internal cluster URL (plan
/// decision D19, Phase 3 Slice 5) and, since Phase 4 (plan decision D27), each node's own known hub
/// names — the cluster-wide hub directory neither <c>IHubRegistry.GetAllHubs()</c> nor
/// <c>ILocalTransportRegistry.GetKnownHubNames()</c> can answer alone, since both are node-local
/// (finding 5). Each node registers here on a cadence (<see cref="Observers.ObserverHeartbeatService"/>'s
/// own re-subscribe cadence covers a comparable staleness concern; this grain is republished by
/// <see cref="NodeRegistryPublisherService"/> on the same <c>ObserverHeartbeatInterval</c>) and
/// removes itself at shutdown; <c>ClientConnectionForwarder</c> resolves a remote connection
/// owner's address through <see cref="GetInternalUrlAsync"/> before forwarding an SSE/Long Polling
/// request, and the management API's detailed health endpoint resolves the hub directory through
/// <see cref="GetAllHubNamesAsync"/>. Keyed by a single fixed string
/// (<see cref="NodeRegistryGrainKey.Value"/>) — there is exactly one activation for the whole
/// cluster, holding a plain map rather than one grain per node, since the map itself is small and
/// every node needs to read all of it. Interface and every method carry <see cref="AliasAttribute"/>
/// so a rename is not a wire-breaking change (plan decision D20).</summary>
[Alias("Keryhe.Switchboard.Orleans.Grains.INodeRegistryGrain")]
public interface INodeRegistryGrain : IGrainWithStringKey
{
    /// <summary><paramref name="internalUrl"/> is null for a node that hasn't configured one (a
    /// WebSocket-only or single-node-style deployment) — it still registers to publish
    /// <paramref name="hubNames"/>, since the cluster-wide hub directory must not depend on a
    /// feature (D19's forward hop) the node may not even be using.</summary>
    [Alias("Register")]
    Task RegisterAsync(string nodeId, string? internalUrl, IReadOnlyList<string> hubNames);

    [Alias("Unregister")]
    Task UnregisterAsync(string nodeId);

    [Alias("GetInternalUrl")]
    Task<string?> GetInternalUrlAsync(string nodeId);

    /// <summary>Every nodeId currently registered (via <see cref="RegisterAsync"/>, whether or not
    /// it has an <c>InternalUrl</c>) and not since <see cref="UnregisterAsync"/>'d.</summary>
    [Alias("GetAllNodes")]
    Task<IReadOnlyList<string>> GetAllNodesAsync();

    /// <summary>Union of every registered node's known hub names (Phase 4 plan decision D27,
    /// finding 5) — the cluster-wide hub directory.</summary>
    [Alias("GetAllHubNames")]
    Task<IReadOnlyList<string>> GetAllHubNamesAsync();
}

/// <summary>The single fixed grain key <see cref="INodeRegistryGrain"/> is activated under.</summary>
public static class NodeRegistryGrainKey
{
    public const string Value = "cluster";
}
