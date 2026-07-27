using System.Buffers;
using System.Threading.Channels;
using Keryhe.Switchboard.Server.ClientConnections;
using Microsoft.AspNetCore.SignalR.Protocol;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.ClientConnections;

/// <summary>
/// Slice 5 gate: SSE is text-only (03-protocol.md §1.5) — a MessagePack handshake request over it
/// must be rejected the same way an unrecognized protocol name would be, not accepted and then
/// mis-framed. Exercised directly against <see cref="ClientConnectionLifecycle.NegotiateHandshakeAsync"/>
/// rather than through a full end-to-end MessagePack client, since the real SignalR client library
/// has no way to request a transport/protocol combination it doesn't itself consider valid.
/// </summary>
public class ClientConnectionLifecycleTests
{
    [Fact]
    public async Task NegotiateHandshakeAsync_RejectsMessagePack_WhenTransportDoesNotSupportBinary()
    {
        var transport = new SseClientTransport("conn-1", "chatHub", null, 16, BoundedChannelFullMode.Wait);

        var writer = new ArrayBufferWriter<byte>();
        HandshakeProtocol.WriteRequestMessage(new HandshakeRequestMessage("messagepack", 1), writer);

        var protocol = await ClientConnectionLifecycle.NegotiateHandshakeAsync(transport, writer.WrittenMemory);

        Assert.Null(protocol);

        transport.Output.Writer.TryComplete();
        var frames = new List<ReadOnlyMemory<byte>>();
        await foreach (var frame in transport.Output.Reader.ReadAllAsync())
        {
            frames.Add(frame);
        }

        var errorFrame = Assert.Single(frames);
        var sequence = new ReadOnlySequence<byte>(errorFrame);
        Assert.True(HandshakeProtocol.TryParseResponseMessage(ref sequence, out var response));
        Assert.NotNull(response.Error);
    }

    [Fact]
    public async Task NegotiateHandshakeAsync_AcceptsJson_OnATextOnlyTransport()
    {
        var transport = new SseClientTransport("conn-2", "chatHub", null, 16, BoundedChannelFullMode.Wait);

        var writer = new ArrayBufferWriter<byte>();
        HandshakeProtocol.WriteRequestMessage(new HandshakeRequestMessage("json", 1), writer);

        var protocol = await ClientConnectionLifecycle.NegotiateHandshakeAsync(transport, writer.WrittenMemory);

        Assert.Equal("json", protocol);
    }
}
