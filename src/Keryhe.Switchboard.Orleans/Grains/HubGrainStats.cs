using Orleans;

namespace Keryhe.Switchboard.Orleans.Grains;

/// <summary>Result of <see cref="IHubGrain.GetStatsAsync"/> (Phase 4 plan decision D27, finding 6)
/// — plain counts, so the management API's detailed health endpoint never has to transfer the full
/// connection-id list just to call <c>.Count</c> on it.</summary>
[GenerateSerializer]
public sealed class HubGrainStats
{
    [Id(0)]
    public required int ClientConnectionCount { get; init; }

    [Id(1)]
    public required int ServerConnectionCount { get; init; }
}
