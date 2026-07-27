using System.Buffers;
using Microsoft.AspNetCore.Connections;

namespace Keryhe.Switchboard.Protocol.Framing;

/// <summary>
/// Finds and writes hub-protocol message frames for one client-facing wire format
/// (03-protocol.md §1.3). A frame returned by <see cref="TryReadFrame"/> always
/// <b>includes</b> its own framing (the JSON record separator, or the MessagePack length
/// prefix) — the server-facing <c>payload</c> contract requires raw hub-protocol bytes
/// including framing (04-design.md §11), and a reader that stripped it was the root cause
/// of the Phase 1 inbound-dispatch stall (00-review-findings.md, Phase 1 results).
/// </summary>
public interface IHubProtocolFraming
{
    /// <summary>The negotiated protocol name: "json" or "messagepack".</summary>
    string Name { get; }

    /// <summary>Drives the WebSocket message type used for frames of this protocol (Text for
    /// json, Binary for messagepack) — verified against a real client: a MessagePack
    /// <c>HubConnection</c> sends even its (always-JSON) handshake request inside a Binary
    /// WebSocket frame.</summary>
    TransferFormat TransferFormat { get; }

    /// <summary>
    /// Attempts to read one complete frame, <b>including its framing</b>, from the front of
    /// <paramref name="buffer"/>. Returns false if the buffer does not yet contain a complete
    /// frame, in which case the caller should read more data and retry with the same buffer
    /// plus the new input. On success, <paramref name="buffer"/> is advanced past the frame.
    /// </summary>
    bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame);

    /// <summary>Writes one already-serialized hub message plus its framing.</summary>
    void WriteFrame(IBufferWriter<byte> writer, ReadOnlySpan<byte> message);
}
