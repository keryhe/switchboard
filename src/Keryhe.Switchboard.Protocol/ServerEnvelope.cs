using MessagePack;

namespace Keryhe.Switchboard.Protocol;

public enum ServerEnvelopeType
{
    Handshake,
    HandshakeAck,
    HandshakeError,
    OpenConnection,
    CloseConnection,
    ClientMessage,
    SendToConnection,
    Broadcast,
    SendToGroup,
    SendToUser,
    AddToGroup,
    RemoveFromGroup,
    Ping,
    Pong
}

/// <summary>
/// The wire envelope wrapping a SignalR payload between the service and an app server.
/// [Key(n)] order is a wire contract — append new fields with new keys, never reuse or reorder.
/// </summary>
[MessagePackObject]
public sealed class ServerEnvelope
{
    [Key(0)]
    public required ServerEnvelopeType Type { get; init; }

    [Key(1)]
    public string? ConnectionId { get; init; }

    [Key(2)]
    public string? HubName { get; init; }

    [Key(3)]
    public string? GroupName { get; init; }

    [Key(4)]
    public string? UserId { get; init; }

    [Key(5)]
    public string? HubProtocol { get; init; }

    [Key(6)]
    public byte[]? Payload { get; init; }

    [Key(7)]
    public IReadOnlyList<string>? ExcludedConnectionIds { get; init; }

    [Key(8)]
    public IReadOnlyDictionary<string, string>? Claims { get; init; }

    [Key(9)]
    public string? Error { get; init; }

    /// <summary>
    /// Handshake protocol version (§2.2). Added after the initial [Key(0..9)] layout was pinned —
    /// an additive Key(10), never reusing or reordering existing keys.
    /// </summary>
    [Key(10)]
    public int? Version { get; init; }

    /// <summary>
    /// Per-protocol payload set for fan-out envelopes (<see cref="ServerEnvelopeType.Broadcast"/>,
    /// <see cref="ServerEnvelopeType.SendToGroup"/>, <see cref="ServerEnvelopeType.SendToUser"/>),
    /// keyed by hub protocol name ("json" / "messagepack") — plan decision D7. The Connector
    /// cannot know a fan-out target set's protocol mix in advance, so it populates an entry for
    /// every protocol the service supports; the service selects the entry matching each target
    /// connection's own negotiated protocol. Targeted sends
    /// (<see cref="ServerEnvelopeType.SendToConnection"/>, <see cref="ServerEnvelopeType.ClientMessage"/>)
    /// have exactly one recipient whose protocol is already known, so they keep using the single
    /// <see cref="Payload"/>/<see cref="HubProtocol"/> pair instead. Additive Key(11) — never
    /// reuse or reorder Key(0..10).
    /// </summary>
    [Key(11)]
    public IReadOnlyDictionary<string, byte[]>? Payloads { get; init; }
}
