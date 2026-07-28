namespace Keryhe.Switchboard.Orleans.Grains;

/// <summary>Result of <see cref="IHubGrain.AssignServerConnectionAsync"/> — the cluster-wide
/// least-loaded pick (plan decision D18, Phase 3 Slice 4). Formatted into a
/// <see cref="Core.ServerConnectionRef"/> string by the caller (<c>OrleansServerConnectionSelector</c>)
/// rather than here, so this stays a plain data carrier with no dependency on the format helper.</summary>
[GenerateSerializer]
public sealed class ServerConnectionAssignment
{
    [Id(0)]
    public required string NodeId { get; init; }

    [Id(1)]
    public required string ServerConnectionId { get; init; }
}
