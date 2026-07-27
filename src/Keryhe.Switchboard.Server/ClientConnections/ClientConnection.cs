namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// Transport-agnostic identity for one live client connection (plan decision D10) — the thing
/// SSE and Long Polling's separate POST/GET requests will need to look up by
/// <see cref="ConnectionToken"/> once those transports land (Slices 5/6), since only the very
/// first request consumes the one-shot pending-connection entry (D4); every request after that
/// must resolve an already-established connection instead.
/// </summary>
public sealed class ClientConnection(
    string connectionId,
    string connectionToken,
    string hubName,
    string? userId,
    IFramedClientTransport transport,
    string hubProtocol)
{
    public string ConnectionId { get; } = connectionId;
    public string ConnectionToken { get; } = connectionToken;
    public string HubName { get; } = hubName;
    public string? UserId { get; } = userId;
    public IFramedClientTransport Transport { get; } = transport;
    public string HubProtocol { get; } = hubProtocol;

    /// <summary>Updated on every inbound frame (including absorbed pings). Read by the future
    /// SSE/Long Polling idle reaper against <c>SwitchboardOptions.DisconnectTimeout</c> — a
    /// WebSocket connection doesn't need this today since its own socket close already ends the
    /// connection, but the field lives here now so Slices 5/6 have it from day one.</summary>
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
}
