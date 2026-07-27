using System.Buffers;
using Keryhe.Switchboard.Protocol.Framing;
using Microsoft.AspNetCore.SignalR.Protocol;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Protocol.Framing;

/// <summary>Byte-pins <see cref="ClientFrameWriter"/> against the framework's own protocol
/// types, since its entire purpose is to match what a real client expects.</summary>
public class ClientFrameWriterTests
{
    private static readonly IHubProtocol Json = new JsonHubProtocol();
    private static readonly IHubProtocol MessagePack = new MessagePackHubProtocol();

    [Fact]
    public void HandshakeResponse_MatchesFrameworkOutput()
    {
        var expected = Write(w => HandshakeProtocol.WriteResponseMessage(HandshakeResponseMessage.Empty, w));

        Assert.Equal(expected, ClientFrameWriter.HandshakeResponse());
    }

    [Fact]
    public void HandshakeError_MatchesFrameworkOutput()
    {
        var expected = Write(w => HandshakeProtocol.WriteResponseMessage(new HandshakeResponseMessage("bad protocol"), w));

        Assert.Equal(expected, ClientFrameWriter.HandshakeError("bad protocol"));
    }

    [Fact]
    public void Close_Json_MatchesFrameworkOutput()
    {
        var expected = Write(w => Json.WriteMessage(new CloseMessage("boom", allowReconnect: true), w));

        Assert.Equal(expected, ClientFrameWriter.Close("json", "boom", allowReconnect: true));
    }

    [Fact]
    public void Close_MessagePack_MatchesFrameworkOutput()
    {
        var expected = Write(w => MessagePack.WriteMessage(new CloseMessage(null, allowReconnect: false), w));

        Assert.Equal(expected, ClientFrameWriter.Close("messagepack", null));
    }

    [Fact]
    public void Ping_Json_MatchesFrameworkOutput()
    {
        var expected = Write(w => Json.WriteMessage(PingMessage.Instance, w));

        Assert.Equal(expected, ClientFrameWriter.Ping("json"));
    }

    [Fact]
    public void Ping_MessagePack_MatchesFrameworkOutput()
    {
        var expected = Write(w => MessagePack.WriteMessage(PingMessage.Instance, w));

        Assert.Equal(expected, ClientFrameWriter.Ping("messagepack"));
    }

    [Fact]
    public void Close_UnknownProtocol_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClientFrameWriter.Close("xml", null));
    }

    private static byte[] Write(Action<ArrayBufferWriter<byte>> write)
    {
        var writer = new ArrayBufferWriter<byte>();
        write(writer);
        return writer.WrittenSpan.ToArray();
    }
}
