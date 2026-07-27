using System.Buffers;
using System.IO.Pipelines;

namespace Keryhe.Switchboard.Protocol;

/// <summary>
/// Client-facing JSON hub-protocol framing: messages delimited by the ASCII record separator
/// 0x1E (see 03-protocol.md §1.3). A single read may contain zero, one, or many complete frames,
/// and a frame may span multiple pipe segments — both cases are handled explicitly below.
/// </summary>
public static class JsonFrameProtocol
{
    public const byte RecordSeparator = 0x1E;

    /// <summary>
    /// Attempts to read one complete frame (delimiter excluded) from the front of
    /// <paramref name="buffer"/>. Returns false if no delimiter has been seen yet, in which case
    /// the caller should keep reading more data and retry with the same buffer plus new input.
    /// </summary>
    public static bool TryParseFrame(
        ref ReadOnlySequence<byte> buffer,
        out ReadOnlySequence<byte> frame)
    {
        var reader = new SequenceReader<byte>(buffer);

        if (!reader.TryReadTo(out frame, RecordSeparator))
        {
            frame = default;
            return false;
        }

        buffer = buffer.Slice(reader.Position);
        return true;
    }

    /// <summary>Writes a single already-serialized JSON message plus its trailing delimiter.</summary>
    public static void WriteFrame(IBufferWriter<byte> writer, ReadOnlySpan<byte> jsonMessage)
    {
        var span = writer.GetSpan(jsonMessage.Length + 1);
        jsonMessage.CopyTo(span);
        span[jsonMessage.Length] = RecordSeparator;
        writer.Advance(jsonMessage.Length + 1);
    }

    public static async ValueTask WriteFrameAsync(PipeWriter writer, ReadOnlyMemory<byte> jsonMessage, CancellationToken ct)
    {
        WriteFrame(writer, jsonMessage.Span);
        await writer.FlushAsync(ct);
    }

    /// <summary>
    /// Reads every complete frame currently available from <paramref name="reader"/>, yielding
    /// each frame as a standalone byte array (copied out of the pipe before the buffer is
    /// advanced). Completes when the pipe reports completion with no further data.
    /// </summary>
    public static async IAsyncEnumerable<byte[]> ReadAllFramesAsync(
        PipeReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;

            while (TryParseFrame(ref buffer, out var frame))
            {
                yield return frame.ToArray();
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                if (buffer.Length > 0)
                {
                    // Trailing bytes with no delimiter: not a complete frame, and no more data is coming.
                    throw new InvalidOperationException("Pipe completed with an incomplete (undelimited) frame remaining.");
                }

                yield break;
            }
        }
    }
}
