using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Keryhe.Switchboard.Protocol;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Protocol;

public class JsonFrameProtocolTests
{
    [Fact]
    public async Task ReadAllFramesAsync_YieldsNothing_ForEmptyInput()
    {
        var pipe = new Pipe();
        await pipe.Writer.CompleteAsync();

        var frames = new List<byte[]>();
        await foreach (var frame in JsonFrameProtocol.ReadAllFramesAsync(pipe.Reader))
        {
            frames.Add(frame);
        }

        Assert.Empty(frames);
    }

    [Fact]
    public async Task ReadAllFramesAsync_SplitsMultipleFramesInOneRead()
    {
        var pipe = new Pipe();
        var payload = Encoding.UTF8.GetBytes("{\"a\":1}") .Concat([JsonFrameProtocol.RecordSeparator])
            .Concat(Encoding.UTF8.GetBytes("{\"b\":2}")).Concat([JsonFrameProtocol.RecordSeparator])
            .ToArray();
        await pipe.Writer.WriteAsync(payload);
        await pipe.Writer.CompleteAsync();

        var frames = new List<string>();
        await foreach (var frame in JsonFrameProtocol.ReadAllFramesAsync(pipe.Reader))
        {
            frames.Add(Encoding.UTF8.GetString(frame));
        }

        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], frames);
    }

    [Fact]
    public async Task ReadAllFramesAsync_HandlesFrameSplitAcrossSegments()
    {
        var pipe = new Pipe();
        var full = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");

        var writeTask = Task.Run(async () =>
        {
            // Write byte-by-byte to force the reader to see partial frames across multiple reads.
            for (var i = 0; i < full.Length; i++)
            {
                await pipe.Writer.WriteAsync(new ReadOnlyMemory<byte>(new[] { full[i] }));
            }
            await pipe.Writer.WriteAsync(new ReadOnlyMemory<byte>(new[] { JsonFrameProtocol.RecordSeparator }));
            await pipe.Writer.CompleteAsync();
        });

        var frames = new List<string>();
        await foreach (var frame in JsonFrameProtocol.ReadAllFramesAsync(pipe.Reader))
        {
            frames.Add(Encoding.UTF8.GetString(frame));
        }

        await writeTask;

        Assert.Equal(["{\"hello\":\"world\"}"], frames);
    }

    [Fact]
    public async Task ReadAllFramesAsync_ThrowsOnIncompleteTrailingFrame()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("{\"no-delimiter\":true}"));
        await pipe.Writer.CompleteAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in JsonFrameProtocol.ReadAllFramesAsync(pipe.Reader))
            {
            }
        });
    }

    [Fact]
    public void TryParseFrame_ReturnsFalse_ForEmptyBuffer()
    {
        var buffer = ReadOnlySequence<byte>.Empty;
        var result = JsonFrameProtocol.TryParseFrame(ref buffer, out var frame);

        Assert.False(result);
        Assert.True(frame.IsEmpty);
    }

    [Fact]
    public void TryParseFrame_HandlesEmptyFrame()
    {
        ReadOnlySequence<byte> buffer = new([JsonFrameProtocol.RecordSeparator]);
        var result = JsonFrameProtocol.TryParseFrame(ref buffer, out var frame);

        Assert.True(result);
        Assert.Equal(0, frame.Length);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void WriteFrame_AppendsRecordSeparator()
    {
        var writer = new ArrayBufferWriter<byte>();
        var message = Encoding.UTF8.GetBytes("{\"x\":1}");

        JsonFrameProtocol.WriteFrame(writer, message);

        var written = writer.WrittenSpan.ToArray();
        Assert.Equal(message.Length + 1, written.Length);
        Assert.Equal(JsonFrameProtocol.RecordSeparator, written[^1]);
        Assert.Equal(message, written[..^1]);
    }
}
