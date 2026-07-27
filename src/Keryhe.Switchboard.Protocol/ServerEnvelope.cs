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
}
