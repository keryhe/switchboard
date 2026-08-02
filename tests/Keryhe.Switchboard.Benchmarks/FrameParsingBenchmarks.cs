using System.Buffers;
using BenchmarkDotNet.Attributes;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.Protocol.Framing;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace Keryhe.Switchboard.Benchmarks;

/// <summary>
/// D33 hot-path suites 2 and 3: <see cref="JsonFrameProtocol.TryParseFrame"/> /
/// <see cref="MessagePackFraming"/> frame parsing, and <see cref="HubMessageClassifier.IsPing"/> —
/// both run on every inbound client frame (04-design.md §6, plan decision D13), so their
/// per-call cost and allocation shape matter more than almost anything else in this codebase.
/// </summary>
[MemoryDiagnoser]
public class FrameParsingBenchmarks
{
    private static readonly IHubProtocol JsonHubProtocol = new JsonHubProtocol();
    private static readonly IHubProtocol MessagePackHubProtocol = new MessagePackHubProtocol();

    private byte[] _jsonInvocationFrame = null!;
    private byte[] _messagePackInvocationFrame = null!;
    private byte[] _jsonPingFrame = null!;
    private byte[] _messagePackPingFrame = null!;

    [GlobalSetup]
    public void Setup()
    {
        // IHubProtocol.WriteMessage emits its own framing (JSON's trailing \x1e, MessagePack's
        // leading varint length prefix) — verified in ClientFrameWriter's own doc remarks — so
        // these are ready to feed straight into TryParseFrame/TryReadFrame/IsPing with no
        // additional wrapping.
        _jsonInvocationFrame = Write(JsonHubProtocol, new InvocationMessage("Echo", ["hello"]));
        _messagePackInvocationFrame = Write(MessagePackHubProtocol, new InvocationMessage("Echo", ["hello"]));
        _jsonPingFrame = ClientFrameWriter.Ping("json");
        _messagePackPingFrame = ClientFrameWriter.Ping("messagepack");
    }

    [Benchmark]
    public bool JsonFrameProtocol_TryParseFrame()
    {
        var buffer = new ReadOnlySequence<byte>(_jsonInvocationFrame);
        return JsonFrameProtocol.TryParseFrame(ref buffer, out _);
    }

    [Benchmark]
    public bool MessagePackFraming_TryReadFrame()
    {
        var buffer = new ReadOnlySequence<byte>(_messagePackInvocationFrame);
        return MessagePackFraming.Instance.TryReadFrame(ref buffer, out _);
    }

    [Benchmark]
    public bool IsPing_Json_ActualPing()
    {
        var frame = new ReadOnlySequence<byte>(_jsonPingFrame);
        return HubMessageClassifier.IsPing("json", frame);
    }

    [Benchmark]
    public bool IsPing_Json_NonPing()
    {
        var frame = new ReadOnlySequence<byte>(_jsonInvocationFrame);
        return HubMessageClassifier.IsPing("json", frame);
    }

    [Benchmark]
    public bool IsPing_MessagePack_ActualPing()
    {
        var frame = new ReadOnlySequence<byte>(_messagePackPingFrame);
        return HubMessageClassifier.IsPing("messagepack", frame);
    }

    [Benchmark]
    public bool IsPing_MessagePack_NonPing()
    {
        var frame = new ReadOnlySequence<byte>(_messagePackInvocationFrame);
        return HubMessageClassifier.IsPing("messagepack", frame);
    }

    private static byte[] Write(IHubProtocol protocol, HubMessage message)
    {
        var writer = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(message, writer);
        return writer.WrittenSpan.ToArray();
    }
}
