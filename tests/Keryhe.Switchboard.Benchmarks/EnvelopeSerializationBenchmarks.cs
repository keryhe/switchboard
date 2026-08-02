using System.Buffers;
using BenchmarkDotNet.Attributes;
using Keryhe.Switchboard.Protocol;

namespace Keryhe.Switchboard.Benchmarks;

/// <summary>
/// D33 hot-path suite 1: <see cref="ServerEnvelopeSerializer"/>'s write/read round-trip — the
/// server-facing wire format, on every message that crosses the app-server/service boundary.
/// </summary>
[MemoryDiagnoser]
public class EnvelopeSerializationBenchmarks
{
    private ServerEnvelope _broadcastEnvelope = null!;
    private byte[] _serializedBroadcast = null!;

    [GlobalSetup]
    public void Setup()
    {
        var payload = new byte[128];
        Random.Shared.NextBytes(payload);

        _broadcastEnvelope = new ServerEnvelope
        {
            Type = ServerEnvelopeType.Broadcast,
            HubName = "chatHub",
            HubProtocol = "json",
            Payload = payload,
            Payloads = new Dictionary<string, byte[]>
            {
                ["json"] = payload,
                ["messagepack"] = payload,
            },
            ExcludedConnectionIds = ["conn-a", "conn-b"],
        };

        var writer = new ArrayBufferWriter<byte>();
        ServerEnvelopeSerializer.Write(writer, _broadcastEnvelope);
        _serializedBroadcast = writer.WrittenSpan.ToArray();
    }

    [Benchmark]
    public byte[] Write()
    {
        var writer = new ArrayBufferWriter<byte>();
        ServerEnvelopeSerializer.Write(writer, _broadcastEnvelope);
        return writer.WrittenSpan.ToArray();
    }

    [Benchmark]
    public ServerEnvelope? Read()
    {
        var sequence = new ReadOnlySequence<byte>(_serializedBroadcast);
        ServerEnvelopeSerializer.TryParseEnvelope(sequence, out var envelope, out _, out _);
        return envelope;
    }

    [Benchmark]
    public ServerEnvelope? WriteThenRead()
    {
        var writer = new ArrayBufferWriter<byte>();
        ServerEnvelopeSerializer.Write(writer, _broadcastEnvelope);
        var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory);
        ServerEnvelopeSerializer.TryParseEnvelope(sequence, out var envelope, out _, out _);
        return envelope;
    }
}
