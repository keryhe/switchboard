using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace Phase0.Spike.Tests.WorkstreamB;

/// <summary>Minimal hand-rolled JSON hub-protocol frame reader/writer for driving the synthetic pipe directly in tests.</summary>
public static class JsonFrameIO
{
    private const byte RecordSeparator = 0x1e;

    public static async Task WriteRawAsync(PipeWriter writer, string json, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await writer.WriteAsync(bytes, ct);
        await writer.WriteAsync(new[] { RecordSeparator }, ct);
    }

    public static Task WriteInvocationAsync(PipeWriter writer, string invocationId, string target, object[] arguments, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = 1,
            invocationId,
            target,
            arguments
        });
        return WriteRawAsync(writer, json, ct);
    }

    /// <summary>Reads the next \x1e-delimited frame, blocking until one arrives or the pipe completes.</summary>
    public static async Task<string> ReadNextFrameAsync(PipeReader reader, CancellationToken ct = default)
    {
        while (true)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            var delimiterPos = buffer.PositionOf(RecordSeparator);

            if (delimiterPos != null)
            {
                var messageBuffer = buffer.Slice(0, delimiterPos.Value);
                var text = Encoding.UTF8.GetString(messageBuffer.ToArray());
                reader.AdvanceTo(buffer.GetPosition(1, delimiterPos.Value));
                return text;
            }

            if (result.IsCompleted)
            {
                throw new InvalidOperationException("Pipe completed before a full frame was read.");
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>Reads frames, skipping the handshake response ({}) and Ping (type 6), until a non-trivial frame is found.</summary>
    public static async Task<JsonElement> ReadNextSignificantMessageAsync(PipeReader reader, CancellationToken ct = default)
    {
        while (true)
        {
            var frame = await ReadNextFrameAsync(reader, ct);
            if (frame == "{}")
            {
                continue; // handshake response
            }

            var element = JsonSerializer.Deserialize<JsonElement>(frame);
            if (element.TryGetProperty("type", out var typeProp) && typeProp.GetInt32() == 6)
            {
                continue; // Ping
            }

            return element;
        }
    }
}
