namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// A transport whose inbound frames arrive over a separate request from the one that carries
/// outbound frames — SSE and Long Polling's POST "send" side (03-protocol.md §1.5/§1.6).
/// WebSocket doesn't implement this: its single socket carries both directions itself.
/// </summary>
public interface IPostableClientTransport : IFramedClientTransport
{
    /// <summary>Feeds raw bytes (a POST request body) into the transport's inbound frame reader.
    /// Bytes are appended verbatim — the caller must include whatever framing the negotiated
    /// protocol expects, exactly as the client sent it.</summary>
    Task FeedAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
}
