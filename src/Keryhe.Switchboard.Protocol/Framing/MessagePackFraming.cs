using System.Buffers;
using Microsoft.AspNetCore.Connections;

namespace Keryhe.Switchboard.Protocol.Framing;

/// <summary>
/// MessagePack hub-protocol framing: each message is prefixed by its own byte length, encoded
/// as a 7-bit variable-length integer — LSB-first groups, high bit set on every byte but the
/// last (03-protocol.md §1.3). Verified empirically against
/// <c>Microsoft.AspNetCore.SignalR.Protocol.MessagePackHubProtocol.WriteMessage</c>: a 17-byte
/// message is prefixed by the single byte <c>0x11</c>; a 213-byte message by <c>0xD5 0x01</c>
/// (0x55 | continuation, then 0x01 — 0x55 | (1 &lt;&lt; 7) = 213).
/// </summary>
/// <remarks>
/// This finds frame <b>boundaries</b> only — it never deserializes the MessagePack body, so the
/// service never needs to understand a client's hub messages, matching the raw-bytes passthrough
/// contract in 04-design.md §11.
/// </remarks>
public sealed class MessagePackFraming : IHubProtocolFraming
{
    public static readonly MessagePackFraming Instance = new();

    /// <summary>ULEB128 caps at 5 bytes for a non-negative 32-bit length.</summary>
    private const int MaxPrefixBytes = 5;

    public string Name => "messagepack";

    public TransferFormat TransferFormat => TransferFormat.Binary;

    public bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        if (!TryReadVarIntLength(buffer, out var bodyLength, out var prefixLength))
        {
            frame = default;
            return false;
        }

        var frameLength = prefixLength + bodyLength;
        if (buffer.Length < frameLength)
        {
            frame = default;
            return false;
        }

        frame = buffer.Slice(0, frameLength);
        buffer = buffer.Slice(frameLength);
        return true;
    }

    public void WriteFrame(IBufferWriter<byte> writer, ReadOnlySpan<byte> message)
    {
        Span<byte> prefix = stackalloc byte[MaxPrefixBytes];
        var prefixLength = WriteVarIntLength((uint)message.Length, prefix);

        var span = writer.GetSpan(prefixLength + message.Length);
        prefix[..prefixLength].CopyTo(span);
        message.CopyTo(span[prefixLength..]);
        writer.Advance(prefixLength + message.Length);
    }

    /// <summary>
    /// Reads a 7-bit-group varint length prefix from the front of <paramref name="buffer"/>
    /// without consuming it. Returns false if the buffer ends mid-varint (need more data) or the
    /// prefix would exceed <see cref="MaxPrefixBytes"/> (malformed input).
    /// </summary>
    private static bool TryReadVarIntLength(ReadOnlySequence<byte> buffer, out int length, out int prefixLength)
    {
        length = 0;
        prefixLength = 0;
        var shift = 0;

        var reader = new SequenceReader<byte>(buffer);
        while (reader.TryRead(out var b))
        {
            length |= (b & 0x7F) << shift;
            prefixLength++;

            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
            if (prefixLength >= MaxPrefixBytes)
            {
                throw new InvalidDataException("MessagePack frame length prefix exceeds 5 bytes.");
            }
        }

        // Ran out of buffered bytes before the varint terminated — need more data.
        prefixLength = 0;
        length = 0;
        return false;
    }

    /// <summary>
    /// Splits a frame (as returned by <see cref="TryReadFrame"/>, so it includes its length
    /// prefix) into that prefix and the MessagePack-encoded body. Used by
    /// <see cref="HubMessageClassifier"/> so it does not need its own varint parser.
    /// </summary>
    public static bool TrySplitPrefixAndBody(ReadOnlySequence<byte> frame, out ReadOnlySequence<byte> body)
    {
        if (!TryReadVarIntLength(frame, out var bodyLength, out var prefixLength) || frame.Length < prefixLength + bodyLength)
        {
            body = default;
            return false;
        }

        body = frame.Slice(prefixLength, bodyLength);
        return true;
    }

    private static int WriteVarIntLength(uint value, Span<byte> destination)
    {
        var i = 0;
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }

            destination[i++] = b;
        } while (value != 0);

        return i;
    }
}
