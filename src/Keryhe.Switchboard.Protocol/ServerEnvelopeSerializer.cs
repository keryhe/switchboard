using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using MessagePack;

namespace Keryhe.Switchboard.Protocol;

/// <summary>
/// Length-prefixed MessagePack framing for <see cref="ServerEnvelope"/>. The prefix is a 4-byte
/// big-endian byte length of the following MessagePack payload (see 03-protocol.md Part 2).
/// </summary>
public static class ServerEnvelopeSerializer
{
    private const int LengthPrefixBytes = 4;

    public static void Write(IBufferWriter<byte> writer, ServerEnvelope envelope)
    {
        var body = MessagePackSerializer.Serialize(envelope);

        var prefixSpan = writer.GetSpan(LengthPrefixBytes);
        BinaryPrimitives.WriteInt32BigEndian(prefixSpan, body.Length);
        writer.Advance(LengthPrefixBytes);

        writer.Write(body);
    }

    public static async ValueTask WriteAsync(PipeWriter writer, ServerEnvelope envelope, CancellationToken ct)
    {
        Write(writer, envelope);
        await writer.FlushAsync(ct);
    }

    /// <summary>
    /// Attempts to read one length-prefixed envelope from <paramref name="buffer"/>. Returns false
    /// if the buffer does not yet contain a complete frame; <paramref name="consumed"/> and
    /// <paramref name="examined"/> follow standard PipeReader partial-frame conventions.
    /// </summary>
    public static bool TryParseEnvelope(
        ReadOnlySequence<byte> buffer,
        out ServerEnvelope? envelope,
        out SequencePosition consumed,
        out SequencePosition examined)
    {
        envelope = null;
        consumed = buffer.Start;
        examined = buffer.End;

        if (buffer.Length < LengthPrefixBytes)
        {
            return false;
        }

        Span<byte> prefixBytes = stackalloc byte[LengthPrefixBytes];
        buffer.Slice(0, LengthPrefixBytes).CopyTo(prefixBytes);
        var bodyLength = BinaryPrimitives.ReadInt32BigEndian(prefixBytes);

        var totalLength = LengthPrefixBytes + bodyLength;
        if (buffer.Length < totalLength)
        {
            return false;
        }

        var bodySequence = buffer.Slice(LengthPrefixBytes, bodyLength);
        envelope = MessagePackSerializer.Deserialize<ServerEnvelope>(bodySequence);

        consumed = buffer.GetPosition(totalLength);
        examined = consumed;
        return true;
    }
}
