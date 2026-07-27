using System.Buffers;
using Keryhe.Switchboard.Protocol.Framing;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR.Protocol;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Protocol.Framing;

public class MessagePackFramingTests
{
    private static readonly IHubProtocol MessagePack = new MessagePackHubProtocol();

    [Fact]
    public void Name_And_TransferFormat_AreMessagePack()
    {
        Assert.Equal("messagepack", MessagePackFraming.Instance.Name);
        Assert.Equal(TransferFormat.Binary, MessagePackFraming.Instance.TransferFormat);
    }

    [Fact]
    public void TryReadFrame_ReadsCanonicalMessage_WithSingleBytePrefix()
    {
        // A short Invocation message — the framework's own WriteMessage already embeds the
        // varint length prefix (verified: 17-byte body -> single prefix byte 0x11).
        var canonical = Write(new InvocationMessage("Echo", new object?[] { "hello" }));
        Assert.Equal(0x11, canonical[0]);

        var buffer = new ReadOnlySequence<byte>(canonical);
        var result = MessagePackFraming.Instance.TryReadFrame(ref buffer, out var frame);

        Assert.True(result);
        Assert.Equal(canonical, frame.ToArray());
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TryReadFrame_ReadsCanonicalMessage_WithTwoBytePrefix()
    {
        // A 200-char argument forces a body over 127 bytes, so the length prefix needs two
        // varint bytes (continuation bit set on the first).
        var canonical = Write(new InvocationMessage("Echo", new object?[] { new string('x', 200) }));
        Assert.True(canonical[0] >= 0x80, "expected the continuation bit set on the first prefix byte");

        var buffer = new ReadOnlySequence<byte>(canonical);
        var result = MessagePackFraming.Instance.TryReadFrame(ref buffer, out var frame);

        Assert.True(result);
        Assert.Equal(canonical, frame.ToArray());
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TryReadFrame_ReturnsFalse_WhenPrefixIncomplete()
    {
        var canonical = Write(new InvocationMessage("Echo", new object?[] { new string('x', 200) }));
        var buffer = new ReadOnlySequence<byte>(canonical[..1]); // only the first (continuation) prefix byte

        var result = MessagePackFraming.Instance.TryReadFrame(ref buffer, out var frame);

        Assert.False(result);
        Assert.True(frame.IsEmpty);
    }

    [Fact]
    public void TryReadFrame_ReturnsFalse_WhenBodyIncomplete()
    {
        var canonical = Write(new InvocationMessage("Echo", new object?[] { "hello" }));
        var buffer = new ReadOnlySequence<byte>(canonical[..^1]); // prefix present, one body byte short

        var result = MessagePackFraming.Instance.TryReadFrame(ref buffer, out var frame);

        Assert.False(result);
        Assert.True(frame.IsEmpty);
    }

    [Fact]
    public void TryReadFrame_HandlesMultipleFramesInOneBuffer()
    {
        var first = Write(new InvocationMessage("A", Array.Empty<object?>()));
        var second = Write(new InvocationMessage("B", Array.Empty<object?>()));
        var buffer = new ReadOnlySequence<byte>(first.Concat(second).ToArray());

        var frames = new List<byte[]>();
        while (MessagePackFraming.Instance.TryReadFrame(ref buffer, out var frame))
        {
            frames.Add(frame.ToArray());
        }

        Assert.Equal(new[] { first, second }, frames);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TryReadFrame_HandlesFrameSplitAcrossSegments_AtVarIntBoundary()
    {
        var canonical = Write(new InvocationMessage("Echo", new object?[] { new string('x', 200) }));
        // Split right between the two prefix bytes.
        var buffer = MultiSegmentSequence.Of(canonical[..1], canonical[1..]);

        var result = MessagePackFraming.Instance.TryReadFrame(ref buffer, out var frame);

        Assert.True(result);
        Assert.Equal(canonical, frame.ToArray());
    }

    [Fact]
    public void TryReadFrame_HandlesFrameSplitAcrossSegments_MidBody()
    {
        var canonical = Write(new InvocationMessage("Echo", new object?[] { "hello" }));
        var mid = canonical.Length / 2;
        var buffer = MultiSegmentSequence.Of(canonical[..mid], canonical[mid..]);

        var result = MessagePackFraming.Instance.TryReadFrame(ref buffer, out var frame);

        Assert.True(result);
        Assert.Equal(canonical, frame.ToArray());
    }

    [Fact]
    public void WriteFrame_ThenTryReadFrame_RoundTrips()
    {
        var body = new byte[] { 1, 2, 3, 4, 5 };
        var writer = new ArrayBufferWriter<byte>();

        MessagePackFraming.Instance.WriteFrame(writer, body);

        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        var result = MessagePackFraming.Instance.TryReadFrame(ref buffer, out var frame);

        Assert.True(result);
        Assert.True(MessagePackFraming.TrySplitPrefixAndBody(frame, out var readBack));
        Assert.Equal(body, readBack.ToArray());
    }

    [Fact]
    public void WriteFrame_UsesTwoBytePrefix_ForA200ByteBody()
    {
        var body = new byte[200];
        var writer = new ArrayBufferWriter<byte>();

        MessagePackFraming.Instance.WriteFrame(writer, body);

        var written = writer.WrittenSpan.ToArray();
        // 200 = 0b1100_1000 -> low7=0x48 with continuation set (0xC8), high bit=1 -> second byte 0x01.
        Assert.Equal(0xC8, written[0]);
        Assert.Equal(0x01, written[1]);
        Assert.Equal(202, written.Length);
    }

    private static byte[] Write(HubMessage message)
    {
        var writer = new ArrayBufferWriter<byte>();
        MessagePack.WriteMessage(message, writer);
        return writer.WrittenSpan.ToArray();
    }
}
