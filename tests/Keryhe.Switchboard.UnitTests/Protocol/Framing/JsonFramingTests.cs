using System.Buffers;
using System.Text;
using Keryhe.Switchboard.Protocol.Framing;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Connections;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Protocol.Framing;

public class JsonFramingTests
{
    private const byte RecordSeparator = Keryhe.Switchboard.Protocol.JsonFrameProtocol.RecordSeparator;

    [Fact]
    public void Name_And_TransferFormat_AreJson()
    {
        Assert.Equal("json", JsonFraming.Instance.Name);
        Assert.Equal(TransferFormat.Text, JsonFraming.Instance.TransferFormat);
    }

    [Fact]
    public void TryReadFrame_ReturnsFalse_WhenNoDelimiterYet()
    {
        var buffer = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("{\"a\":1}"));
        var result = JsonFraming.Instance.TryReadFrame(ref buffer, out var frame);

        Assert.False(result);
        Assert.True(frame.IsEmpty);
    }

    [Fact]
    public void TryReadFrame_ReturnsFrame_IncludingTrailingDelimiter()
    {
        var message = Encoding.UTF8.GetBytes("{\"a\":1}");
        var withDelimiter = message.Concat(new[] { RecordSeparator }).ToArray();
        var buffer = new ReadOnlySequence<byte>(withDelimiter);

        var result = JsonFraming.Instance.TryReadFrame(ref buffer, out var frame);

        Assert.True(result);
        Assert.Equal(message.Length + 1, frame.Length);
        Assert.Equal(RecordSeparator, frame.ToArray()[^1]);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TryReadFrame_HandlesMultipleFramesInOneBuffer()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"a\":1}").Concat(new[] { RecordSeparator })
            .Concat(Encoding.UTF8.GetBytes("{\"b\":2}")).Concat(new[] { RecordSeparator })
            .ToArray();
        var buffer = new ReadOnlySequence<byte>(bytes);

        var frames = new List<string>();
        while (JsonFraming.Instance.TryReadFrame(ref buffer, out var frame))
        {
            frames.Add(Encoding.UTF8.GetString(frame.ToArray()[..^1]));
        }

        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}" }, frames);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TryReadFrame_HandlesFrameSplitAcrossSegments()
    {
        var message = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
        var buffer = MultiSegmentSequence.Of(
            message[..5],
            message[5..12],
            message[12..],
            new[] { RecordSeparator });

        var result = JsonFraming.Instance.TryReadFrame(ref buffer, out var frame);

        Assert.True(result);
        Assert.Equal(message.Length + 1, frame.Length);
        Assert.Equal(Encoding.UTF8.GetString(message), Encoding.UTF8.GetString(frame.ToArray()[..^1]));
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void WriteFrame_AppendsRecordSeparator()
    {
        var writer = new ArrayBufferWriter<byte>();
        var message = Encoding.UTF8.GetBytes("{\"x\":1}");

        JsonFraming.Instance.WriteFrame(writer, message);

        var written = writer.WrittenSpan.ToArray();
        Assert.Equal(message.Length + 1, written.Length);
        Assert.Equal(RecordSeparator, written[^1]);
        Assert.Equal(message, written[..^1]);
    }
}
