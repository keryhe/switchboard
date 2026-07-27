using System.Buffers;

namespace Keryhe.Switchboard.UnitTests.TestSupport;

/// <summary>Builds a <see cref="ReadOnlySequence{T}"/> spanning multiple non-contiguous memory
/// segments, for tests that need to prove a parser handles data split across pipe segments
/// rather than one contiguous array.</summary>
public static class MultiSegmentSequence
{
    public static ReadOnlySequence<byte> Of(params byte[][] chunks)
    {
        if (chunks.Length == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        if (chunks.Length == 1)
        {
            return new ReadOnlySequence<byte>(chunks[0]);
        }

        var first = new Segment(chunks[0]);
        var last = first;
        for (var i = 1; i < chunks.Length; i++)
        {
            last = last.Append(chunks[i]);
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(byte[] chunk)
        {
            Memory = chunk;
        }

        public Segment Append(byte[] chunk)
        {
            var segment = new Segment(chunk) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }
}
