using System.Buffers;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace Keryhe.Switchboard.Protocol.Framing;

/// <summary>
/// The service's only source of client-facing hub-protocol bytes it must construct itself: the
/// handshake response, hub-level <c>Close</c>, and hub-level <c>Ping</c>
/// (03-protocol.md §1.2/§1.4, 04-design.md §3/§6). The service is otherwise deliberately
/// payload-agnostic — widening this type into a general hub-protocol writer is a review
/// failure, not a build failure.
/// </summary>
/// <remarks>
/// Every method here returns bytes that already include their own framing (the JSON record
/// separator, or the MessagePack length prefix) — verified empirically:
/// <c>HandshakeProtocol.WriteResponseMessage</c> and <c>IHubProtocol.WriteMessage</c> both emit
/// their trailing/leading framing themselves, so nothing here calls
/// <see cref="IHubProtocolFraming.WriteFrame"/>.
/// </remarks>
public static class ClientFrameWriter
{
    private static readonly IHubProtocol Json = new JsonHubProtocol();
    private static readonly IHubProtocol MessagePack = new MessagePackHubProtocol();

    /// <summary>
    /// The handshake response is always JSON, regardless of the negotiated hub protocol —
    /// verified against a real <c>@microsoft/signalr</c>-equivalent MessagePack client, which
    /// sends its (JSON) handshake request inside a <em>Binary</em> WebSocket frame. Only the
    /// WebSocket message type the caller wraps this in should vary with
    /// <see cref="IHubProtocolFraming.TransferFormat"/> — the bytes here never do.
    /// </summary>
    public static byte[] HandshakeResponse()
    {
        var writer = new ArrayBufferWriter<byte>();
        HandshakeProtocol.WriteResponseMessage(HandshakeResponseMessage.Empty, writer);
        return writer.WrittenSpan.ToArray();
    }

    public static byte[] HandshakeError(string error)
    {
        var writer = new ArrayBufferWriter<byte>();
        HandshakeProtocol.WriteResponseMessage(new HandshakeResponseMessage(error), writer);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Hub-level Close (type 7) — distinct from a transport-level close
    /// (03-protocol.md §1.4). Encoded in the connection's negotiated hub protocol so a
    /// MessagePack client receives a MessagePack-framed Close, not JSON.</summary>
    public static byte[] Close(string hubProtocol, string? error, bool allowReconnect = false)
    {
        var writer = new ArrayBufferWriter<byte>();
        Resolve(hubProtocol).WriteMessage(new CloseMessage(error, allowReconnect), writer);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Hub-level Ping (type 6) keep-alive — distinct from WebSocket ping/pong frames
    /// (04-design.md §6). The service owns client keep-alive; the Connector drops any
    /// <c>PingMessage</c> it sees from the app-server-side pipeline rather than double-pinging
    /// (04-design.md §11).</summary>
    public static byte[] Ping(string hubProtocol)
    {
        var writer = new ArrayBufferWriter<byte>();
        Resolve(hubProtocol).WriteMessage(PingMessage.Instance, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static IHubProtocol Resolve(string hubProtocol) => hubProtocol switch
    {
        "json" => Json,
        "messagepack" => MessagePack,
        _ => throw new ArgumentOutOfRangeException(nameof(hubProtocol), hubProtocol, "Unknown hub protocol."),
    };
}
