using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Registry;
using Keryhe.Switchboard.Server.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Benchmarks;

/// <summary>
/// D33 hot-path suite 4: <see cref="DefaultMessageRouter"/> fan-out over an in-memory
/// <see cref="ILocalTransportRegistry"/> at 1/100/1k/10k local targets — isolates routing cost
/// from socket cost (no real transport ever opens a socket; <see cref="IBackplane"/> is
/// <see cref="NoOpBackplane"/>, so the one publish call at the end of every fan-out is a no-op
/// too). <see cref="DropWriteFakeClientTransport"/>'s channels are exactly the bounded,
/// <c>DropWrite</c>-mode channels every real transport uses (00-review-findings.md) and are never
/// drained by a reader — a broadcast to 10,000 of them must not block on that, which is the whole
/// point of <c>DropWrite</c> in production.
/// </summary>
[MemoryDiagnoser]
public class FanOutBenchmarks
{
    private const string HubName = "chatHub";

    [Params(1, 100, 1_000, 10_000)]
    public int TargetCount { get; set; }

    private DefaultMessageRouter _router = null!;
    private byte[] _payload = null!;

    [GlobalSetup]
    public void Setup()
    {
        var localTransportRegistry = new LocalTransportRegistry();
        for (var i = 0; i < TargetCount; i++)
        {
            var transport = new DropWriteFakeClientTransport($"conn-{i}", HubName);
            localTransportRegistry.Register(transport, HubName, userId: null);
            localTransportRegistry.SetHubProtocol(transport.ConnectionId, "json");
        }

        var options = Microsoft.Extensions.Options.Options.Create(new SwitchboardOptions
        {
            PublicUrl = "http://localhost",
            TokenSigningKey = "benchmark-only-not-a-real-key-benchmark-only-not-a-real-key",
            ServerSigningKey = "benchmark-only-not-a-real-key-benchmark-only-not-a-real-key",
        });

        _router = new DefaultMessageRouter(
            new InMemoryConnectionRegistry(),
            new InMemoryHubRegistry(),
            localTransportRegistry,
            new NoOpBackplane(),
            options,
            new SwitchboardMetrics(),
            new SwitchboardTracing(),
            NullLogger<DefaultMessageRouter>.Instance);

        _payload = new byte[128];
        Random.Shared.NextBytes(_payload);
    }

    [Benchmark]
    public async Task BroadcastAsync()
    {
        await _router.BroadcastAsync(HubName, _payload, "json", payloadsByProtocol: null, excludedConnectionIds: null, CancellationToken.None);
    }

    /// <summary>Bounded, <c>DropWrite</c>-mode <see cref="IClientTransport"/> fake matching every
    /// real transport's channel configuration (<c>SwitchboardOptions.WriteChannelCapacity</c>/
    /// <c>WriteChannelFullMode</c> defaults) — deliberately never read from, so a full channel
    /// drops the newest write instead of blocking the whole fan-out batch.</summary>
    private sealed class DropWriteFakeClientTransport(string connectionId, string hubName) : IClientTransport
    {
        public string ConnectionId { get; } = connectionId;
        public string HubName { get; } = hubName;
        public string? UserId => null;

        public Channel<ReadOnlyMemory<byte>> Output { get; } = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite });

        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken ct) => throw new NotSupportedException();

        public ValueTask CloseAsync(string? error = null) => ValueTask.CompletedTask;
    }
}
