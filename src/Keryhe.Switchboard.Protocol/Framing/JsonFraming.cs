using System.Buffers;
using Microsoft.AspNetCore.Connections;

namespace Keryhe.Switchboard.Protocol.Framing;

/// <summary>
/// JSON hub-protocol framing: messages delimited by the ASCII record separator 0x1E
/// (03-protocol.md §1.3). <see cref="IHubProtocolFraming.TryReadFrame"/> returns each frame
/// <b>with its trailing delimiter included</b> — unlike <see cref="JsonFrameProtocol.TryParseFrame"/>,
/// which strips it and is kept only for call sites that genuinely want the delimiter removed.
/// </summary>
public sealed class JsonFraming : IHubProtocolFraming
{
    public static readonly JsonFraming Instance = new();

    public string Name => "json";

    public TransferFormat TransferFormat => TransferFormat.Text;

    public bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        var reader = new SequenceReader<byte>(buffer);

        if (!reader.TryAdvanceTo(JsonFrameProtocol.RecordSeparator, advancePastDelimiter: true))
        {
            frame = default;
            return false;
        }

        frame = buffer.Slice(0, reader.Position);
        buffer = buffer.Slice(reader.Position);
        return true;
    }

    public void WriteFrame(IBufferWriter<byte> writer, ReadOnlySpan<byte> message) =>
        JsonFrameProtocol.WriteFrame(writer, message);
}
