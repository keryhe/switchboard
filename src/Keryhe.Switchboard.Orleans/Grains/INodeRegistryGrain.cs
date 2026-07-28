using Orleans;

namespace Keryhe.Switchboard.Orleans.Grains;

/// <summary>Cluster-wide singleton grain mapping <c>nodeId</c> → internal cluster URL (plan
/// decision D19, Phase 3 Slice 5). Each node registers its own <c>SwitchboardOptions.InternalUrl</c>
/// here at startup and removes it at shutdown; <c>ClientConnectionForwarder</c> resolves a remote
/// connection owner's address through it before forwarding an SSE/Long Polling request. Keyed by a
/// single fixed string (<see cref="NodeRegistryGrainKey.Value"/>) — there is exactly one activation
/// for the whole cluster, holding a plain map rather than one grain per node, since the map itself
/// is small and every node needs to read all of it. Interface and every method carry
/// <see cref="AliasAttribute"/> so a rename is not a wire-breaking change (plan decision D20).</summary>
[Alias("Keryhe.Switchboard.Orleans.Grains.INodeRegistryGrain")]
public interface INodeRegistryGrain : IGrainWithStringKey
{
    [Alias("Register")]
    Task RegisterAsync(string nodeId, string internalUrl);

    [Alias("Unregister")]
    Task UnregisterAsync(string nodeId);

    [Alias("GetInternalUrl")]
    Task<string?> GetInternalUrlAsync(string nodeId);
}

/// <summary>The single fixed grain key <see cref="INodeRegistryGrain"/> is activated under.</summary>
public static class NodeRegistryGrainKey
{
    public const string Value = "cluster";
}
