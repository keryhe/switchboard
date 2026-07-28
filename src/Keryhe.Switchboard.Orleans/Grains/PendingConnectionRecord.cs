namespace Keryhe.Switchboard.Orleans.Grains;

/// <summary>Grain-serializable twin of <c>Keryhe.Switchboard.Registry.PendingConnection</c>, minus
/// the token itself (that's the grain's key). Plan decision D19 — this is the state substitution
/// that makes negotiate and the transport upgrade legal to land on different nodes.</summary>
[GenerateSerializer]
public sealed class PendingConnectionRecord
{
    [Id(0)]
    public required string ConnectionId { get; init; }

    [Id(1)]
    public required string HubName { get; init; }

    [Id(2)]
    public string? UserId { get; init; }

    [Id(3)]
    public Dictionary<string, string>? Claims { get; init; }

    [Id(4)]
    public required DateTimeOffset ExpiresAt { get; init; }
}
