using System.Buffers;
using Keryhe.Switchboard.Protocol.Framing;
using Microsoft.AspNetCore.SignalR.Protocol;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Protocol.Framing;

/// <summary>D13: the service must classify hub-level Ping without deserializing anything else
/// in the message.</summary>
public class HubMessageClassifierTests
{
    private static readonly IHubProtocol Json = new JsonHubProtocol();
    private static readonly IHubProtocol MessagePack = new MessagePackHubProtocol();

    [Fact]
    public void IsPing_True_ForJsonPingFrame()
    {
        var frame = new ReadOnlySequence<byte>(ClientFrameWriter.Ping("json"));

        Assert.True(HubMessageClassifier.IsPing("json", frame));
    }

    [Fact]
    public void IsPing_False_ForJsonInvocationFrame()
    {
        var frame = new ReadOnlySequence<byte>(Write(Json, new InvocationMessage("Echo", new object?[] { "hi" })));

        Assert.False(HubMessageClassifier.IsPing("json", frame));
    }

    [Fact]
    public void IsPing_False_ForJsonCompletionFrame()
    {
        var frame = new ReadOnlySequence<byte>(Write(Json, CompletionMessage.WithResult("1", null)));

        Assert.False(HubMessageClassifier.IsPing("json", frame));
    }

    [Fact]
    public void IsPing_True_ForMessagePackPingFrame()
    {
        var frame = new ReadOnlySequence<byte>(ClientFrameWriter.Ping("messagepack"));

        Assert.True(HubMessageClassifier.IsPing("messagepack", frame));
    }

    [Fact]
    public void IsPing_False_ForMessagePackInvocationFrame()
    {
        var frame = new ReadOnlySequence<byte>(Write(MessagePack, new InvocationMessage("Echo", new object?[] { "hi" })));

        Assert.False(HubMessageClassifier.IsPing("messagepack", frame));
    }

    [Fact]
    public void IsPing_False_ForGarbageBytes_DoesNotThrow()
    {
        var frame = new ReadOnlySequence<byte>(new byte[] { 0xFF, 0xFF, 0xFF });

        Assert.False(HubMessageClassifier.IsPing("messagepack", frame));
        Assert.False(HubMessageClassifier.IsPing("json", frame));
    }

    [Fact]
    public void IsPing_False_ForUnknownProtocol()
    {
        var frame = new ReadOnlySequence<byte>(ClientFrameWriter.Ping("json"));

        Assert.False(HubMessageClassifier.IsPing("xml", frame));
    }

    private static byte[] Write(IHubProtocol protocol, HubMessage message)
    {
        var writer = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(message, writer);
        return writer.WrittenSpan.ToArray();
    }
}
