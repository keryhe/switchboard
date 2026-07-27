using System.Buffers;
using Keryhe.Switchboard.Protocol;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Protocol;

public class ServerEnvelopeSerializerTests
{
    [Fact]
    public void Write_PinsExactByteLayoutForAKnownEnvelope()
    {
        var envelope = new ServerEnvelope
        {
            Type = ServerEnvelopeType.ClientMessage,
            ConnectionId = "conn-1",
            HubProtocol = "json",
            Payload = [1, 2, 3],
        };

        var buffer = new ArrayBufferWriter<byte>();
        ServerEnvelopeSerializer.Write(buffer, envelope);

        // Pinned expected bytes: 4-byte big-endian length prefix followed by the MessagePack
        // array-of-11-fields encoding (Type=5 (ClientMessage), ConnectionId, HubName=nil,
        // GroupName=nil, UserId=nil, HubProtocol, Payload=bin, ExcludedConnectionIds=nil,
        // Claims=nil, Error=nil, Version=nil). A change to this byte sequence means the
        // [Key(n)] contract moved.
        byte[] expected =
        [
            0x00, 0x00, 0x00, 0x1A, // length prefix = 26
            0x9B, // fixarray, 11 elements
            0x05, // Type = ClientMessage (enum ordinal 5)
            0xA6, 0x63, 0x6F, 0x6E, 0x6E, 0x2D, 0x31, // "conn-1"
            0xC0, // HubName = nil
            0xC0, // GroupName = nil
            0xC0, // UserId = nil
            0xA4, 0x6A, 0x73, 0x6F, 0x6E, // "json"
            0xC4, 0x03, 0x01, 0x02, 0x03, // bin8, len 3, {1,2,3}
            0xC0, // ExcludedConnectionIds = nil
            0xC0, // Claims = nil
            0xC0, // Error = nil
            0xC0, // Version = nil
        ];

        Assert.Equal(expected, buffer.WrittenSpan.ToArray());
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var envelope = new ServerEnvelope
        {
            Type = ServerEnvelopeType.Broadcast,
            HubName = "chatHub",
            HubProtocol = "messagepack",
            Payload = [9, 8, 7],
            ExcludedConnectionIds = ["a", "b"],
            Claims = new Dictionary<string, string> { ["role"] = "admin" },
        };

        var buffer = new ArrayBufferWriter<byte>();
        ServerEnvelopeSerializer.Write(buffer, envelope);

        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        var parsed = ServerEnvelopeSerializer.TryParseEnvelope(sequence, out var result, out var consumed, out _);

        Assert.True(parsed);
        Assert.NotNull(result);
        Assert.Equal(envelope.Type, result!.Type);
        Assert.Equal(envelope.HubName, result.HubName);
        Assert.Equal(envelope.HubProtocol, result.HubProtocol);
        Assert.Equal(envelope.Payload, result.Payload);
        Assert.Equal(envelope.ExcludedConnectionIds, result.ExcludedConnectionIds);
        Assert.Equal(envelope.Claims, result.Claims);
        Assert.Equal(buffer.WrittenMemory.Length, sequence.GetOffset(consumed));
    }

    [Fact]
    public void TryParseEnvelope_ReturnsFalse_WhenLengthPrefixIncomplete()
    {
        ReadOnlySequence<byte> buffer = new(new byte[] { 0x00, 0x00 });

        var result = ServerEnvelopeSerializer.TryParseEnvelope(buffer, out var envelope, out _, out _);

        Assert.False(result);
        Assert.Null(envelope);
    }

    [Fact]
    public void TryParseEnvelope_ReturnsFalse_WhenBodyIncomplete()
    {
        var envelope = new ServerEnvelope { Type = ServerEnvelopeType.Ping };
        var buffer = new ArrayBufferWriter<byte>();
        ServerEnvelopeSerializer.Write(buffer, envelope);

        // Chop off the last byte of the body.
        var truncated = new ReadOnlySequence<byte>(buffer.WrittenMemory[..^1]);

        var result = ServerEnvelopeSerializer.TryParseEnvelope(truncated, out var parsed, out _, out _);

        Assert.False(result);
        Assert.Null(parsed);
    }
}
