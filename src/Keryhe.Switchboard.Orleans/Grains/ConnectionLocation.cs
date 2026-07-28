namespace Keryhe.Switchboard.Orleans.Grains;

/// <summary>Lean projection of <see cref="ConnectionRecord"/> for cross-node targeted sends (plan
/// decision D17, Phase 3 Slice 3) — just enough for <c>OrleansObserverBackplane.PublishToConnectionAsync</c>
/// to find the owning node's <see cref="Observers.IHubObserver"/> without fetching the full record.</summary>
[GenerateSerializer]
public sealed class ConnectionLocation
{
    [Id(0)]
    public required string OwnerNodeId { get; init; }

    [Id(1)]
    public required string HubName { get; init; }
}
