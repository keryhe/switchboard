namespace Keryhe.Switchboard.Core;

/// <summary>
/// Resolves a node id (<c>SwitchboardOptions.NodeId</c>) to that node's address on the internal
/// cluster network (<c>SwitchboardOptions.InternalUrl</c>), published at startup and removed at
/// shutdown (plan decision D19, Phase 3 Slice 5). Consulted only after
/// <see cref="IConnectionOwnershipResolver"/> has named a remote owner — single-node deployments
/// never reach this, since their <see cref="IConnectionOwnershipResolver"/> never returns a remote
/// owner in the first place.
/// </summary>
public interface INodeAddressResolver
{
    /// <summary>Null means this node's internal address is not currently published — either it
    /// never configured <c>InternalUrl</c>, or it has since left the cluster.</summary>
    Task<string?> GetInternalUrlAsync(string nodeId, CancellationToken ct);
}
