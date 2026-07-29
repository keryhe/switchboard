using System.Diagnostics.Metrics;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Observability;

/// <summary>
/// Phase 4 Slice 4 gate (plans/phase-4-management-and-observability.md §4, plan decisions D24/D25/
/// D28): every instrument observed in-process via <see cref="MeterListener"/> against a real
/// end-to-end flow — no collector, no exporter, no network anywhere. Asserts a
/// <c>broadcast.fan_out_size</c> recording matches the actual recipient count, an
/// <c>envelopes.unrouted{reason=...}</c> increment induced by routing to a connection that was
/// never registered, that no OpenTelemetry pipeline exists at all when
/// <see cref="Keryhe.Switchboard.Core.Models.SwitchboardOptions.OtlpEndpoint"/> is unset, and that
/// the two node-local gauges report only their own node's count on a real two-node cluster —
/// double-counting there would be silent and would corrupt every dashboard built on top of it.
/// </summary>
public class SwitchboardMetricsEndToEndTests
{
    private const string HubName = "chatHub-metrics-e2e";

    [Fact]
    public async Task Broadcast_RecordsMessagesRoutedFanOutSizeAndDurations_MatchingActualRecipientCount()
    {
        await using var service = new RealKestrelServerFixture();
        await service.StartAsync();
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServer = await AppServerDouble.ConnectAsync(service.ServerAddress, HubName, serverToken);

        var token1 = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        var token2 = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "bob", null, TimeSpan.FromMinutes(1));
        await using var client1 = BuildClient(service, token1);
        await using var client2 = BuildClient(service, token2);

        var received1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client1.On<string, string>("ReceiveMessage", (_, _) => received1.TrySetResult());
        client2.On<string, string>("ReceiveMessage", (_, _) => received2.TrySetResult());

        await client1.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServer.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(10));
        await client2.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServer.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(10));

        var metrics = service.Services.GetRequiredService<SwitchboardMetrics>();
        using var collector = new MetricsCollector(metrics.Meter);

        // Round trip through the router twice: client->server (inbound_duration) via a client
        // message, then server->clients fan-out (outbound_duration, fan_out_size) via a broadcast.
        await client1.SendAsync("Ping");
        await appServer.ReceiveEnvelopeAsync(ServerEnvelopeType.ClientMessage, TimeSpan.FromSeconds(10));

        await appServer.BroadcastAsync(HubName, "ReceiveMessage", "System", "hello-everyone");
        await received1.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await received2.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A counter Add()/histogram Record() call happens on the same async call stack that sends
        // the bytes an assertion elsewhere is already waiting on, but under real thread-pool
        // scheduling contention (this whole suite runs its test classes in parallel) that
        // instruction can still be a few scheduler ticks behind the network write it precedes —
        // so poll briefly rather than asserting on the very first snapshot.
        await WaitUntilAsync(() =>
        {
            collector.RecordObservableInstruments();
            return Task.FromResult(
                collector.LongMeasurements("signalr.broadcast.fan_out_size").Any(m => m.Value == 2) &&
                collector.LongMeasurements("signalr.messages.routed").Any(m => TagEquals(m.Tags, "direction", "inbound")) &&
                collector.LongMeasurements("signalr.messages.routed").Any(m => TagEquals(m.Tags, "direction", "outbound")) &&
                collector.DoubleMeasurements("signalr.message.inbound_duration").Count > 0 &&
                collector.DoubleMeasurements("signalr.message.outbound_duration").Count > 0);
        }, ready => ready, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task RouteClientMessage_ToUnknownConnection_RecordsEnvelopesUnroutedWithReasonTag()
    {
        await using var service = new RealKestrelServerFixture();
        await service.StartAsync();
        var router = service.Services.GetRequiredService<IMessageRouter>();
        var metrics = service.Services.GetRequiredService<SwitchboardMetrics>();

        using var collector = new MetricsCollector(metrics.Meter);

        await router.RouteClientMessageAsync(
            "connection-that-was-never-registered", new byte[] { 1, 2, 3 }, "json", CancellationToken.None);

        Assert.Contains(collector.LongMeasurements("signalr.envelopes.unrouted"),
            m => TagEquals(m.Tags, "reason", "unknown_connection"));
    }

    [Fact]
    public async Task NoOtlpEndpointConfigured_NoOpenTelemetryPipelineIsConstructed()
    {
        await using var service = new RealKestrelServerFixture();
        await service.StartAsync();

        // Verified failure mode (finding 3): a misconfigured OTLP endpoint fails completely
        // silently. The only way to actually prove nothing was wired up is to check that no
        // MeterProvider (or TracerProvider) exists in the container at all — with no endpoint
        // configured, Program.BuildApp never calls AddOpenTelemetry() in the first place.
        Assert.Null(service.Services.GetService<OpenTelemetry.Metrics.MeterProvider>());
    }

    /// <summary>Two real nodes, each with its own client connected through it — the node-local
    /// gauges must report exactly 1 for each node, never 2 (which would mean cluster-wide state
    /// leaked into what plan decision D24 requires to be a purely node-local read).</summary>
    [Fact]
    public async Task Gauges_AreNodeLocal_TwoNodesEachReportOwnClientConnectionCountOnly()
    {
        var siloPortA = RealKestrelServerFixture.GetFreeTcpPort();
        var gatewayPortA = RealKestrelServerFixture.GetFreeTcpPort();
        var siloPortB = RealKestrelServerFixture.GetFreeTcpPort();
        var gatewayPortB = RealKestrelServerFixture.GetFreeTcpPort();
        var clusterId = $"switchboard-test-{Guid.NewGuid():n}";

        var commonArgs = new[]
        {
            "--Switchboard:UseOrleansCluster", "true",
            "--Switchboard:OrleansClusterId", clusterId,
            "--Switchboard:OrleansServiceId", clusterId,
            "--Switchboard:ClientKeepAliveInterval", "00:05:00",
            "--Switchboard:ShutdownTimeout", "00:00:02",
        };

        await using var nodeA = new RealKestrelServerFixture();
        await nodeA.StartAsync(commonArgs.Concat(
        [
            "--Switchboard:OrleansSiloPort", siloPortA.ToString(),
            "--Switchboard:OrleansGatewayPort", gatewayPortA.ToString(),
        ]).ToArray());

        await using var nodeB = new RealKestrelServerFixture();
        await nodeB.StartAsync(commonArgs.Concat(
        [
            "--Switchboard:OrleansSiloPort", siloPortB.ToString(),
            "--Switchboard:OrleansGatewayPort", gatewayPortB.ToString(),
            "--Switchboard:OrleansPrimarySiloEndpoint", $"127.0.0.1:{siloPortA}",
        ]).ToArray());

        var tokenService = nodeA.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerA = await AppServerDouble.ConnectAsync(nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(nodeB.ServerAddress, HubName, serverToken);

        var tokenA = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "on-node-a", null, TimeSpan.FromMinutes(1));
        var tokenB = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "on-node-b", null, TimeSpan.FromMinutes(1));
        await using var clientA = BuildClient(nodeA, tokenA);
        await using var clientB = BuildClient(nodeB, tokenB);

        await clientA.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await AppServerDoubleWaits.WaitForOpenConnectionAsync("on-node-a", appServerA, appServerB);
        await clientB.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await AppServerDoubleWaits.WaitForOpenConnectionAsync("on-node-b", appServerA, appServerB);

        var metricsA = nodeA.Services.GetRequiredService<SwitchboardMetrics>();
        var metricsB = nodeB.Services.GetRequiredService<SwitchboardMetrics>();

        using var collectorA = new MetricsCollector(metricsA.Meter);
        using var collectorB = new MetricsCollector(metricsB.Meter);
        collectorA.RecordObservableInstruments();
        collectorB.RecordObservableInstruments();

        var nodeATotal = collectorA.LongMeasurements("signalr.client_connections.active").Sum(m => m.Value);
        var nodeBTotal = collectorB.LongMeasurements("signalr.client_connections.active").Sum(m => m.Value);

        Assert.Equal(1, nodeATotal);
        Assert.Equal(1, nodeBTotal);
    }

    private static HubConnection BuildClient(RealKestrelServerFixture node, string clientToken)
    {
        var url = new Uri(node.ServerAddress, $"/{HubName}");
        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(clientToken);
                options.Transports = HttpTransportType.WebSockets;
            })
            .Build();
    }

    private static async Task WaitUntilAsync<T>(Func<Task<T>> poll, Func<T, bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!predicate(await poll()))
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    private static bool TagEquals(IReadOnlyList<KeyValuePair<string, object?>> tags, string key, string value) =>
        tags.Any(t => t.Key == key && Equals(t.Value?.ToString(), value));

    /// <summary>Thin <see cref="MeterListener"/> wrapper scoped to one specific <see cref="Meter"/>
    /// instance — every <c>SwitchboardMetrics</c> singleton creates a <em>same-named</em>
    /// (<see cref="SwitchboardMetrics.MeterName"/>) but distinct <see cref="Meter"/> object, one per
    /// process/fixture, so filtering by name alone would conflate two nodes' instruments in the
    /// two-node gauge test. <see cref="MeterListener"/> is otherwise process-wide (it observes every
    /// <see cref="Meter"/> in the process), so instance identity is the only thing that actually
    /// scopes a collector to one node.</summary>
    private sealed class MetricsCollector : IDisposable
    {
        private readonly MeterListener _listener = new();

        // ConcurrentQueue, not List<T>: measurement callbacks fire synchronously on whichever
        // ASP.NET Core thread-pool thread is executing the Counter.Add()/Histogram.Record() call
        // that triggered them — genuinely concurrent with this test's own polling thread reading
        // the same collection. A plain List<T> here isn't just theoretically unsafe: it produced a
        // real, reproducible failure — a rare InvalidOperationException thrown out of the
        // production Add()/Record() call itself (from inside this test-only callback), silently
        // aborting the rest of that method body (e.g. the paired Record() call right after an
        // Add()) and making the test hang for its entire timeout waiting on a measurement that was
        // never going to arrive.
        private readonly System.Collections.Concurrent.ConcurrentQueue<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> _longMeasurements = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> _doubleMeasurements = new();

        public MetricsCollector(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
                _longMeasurements.Enqueue((instrument.Name, measurement, tags.ToArray().Select(t => new KeyValuePair<string, object?>(t.Key, t.Value)).ToArray())));
            _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
                _doubleMeasurements.Enqueue((instrument.Name, measurement, tags.ToArray().Select(t => new KeyValuePair<string, object?>(t.Key, t.Value)).ToArray())));
            _listener.Start();
        }

        public void RecordObservableInstruments() => _listener.RecordObservableInstruments();

        public IReadOnlyList<(long Value, KeyValuePair<string, object?>[] Tags)> LongMeasurements(string instrumentName) =>
            _longMeasurements.Where(m => m.Name == instrumentName).Select(m => (m.Value, m.Tags)).ToList();

        public IReadOnlyList<(double Value, KeyValuePair<string, object?>[] Tags)> DoubleMeasurements(string instrumentName) =>
            _doubleMeasurements.Where(m => m.Name == instrumentName).Select(m => (m.Value, m.Tags)).ToList();

        public void Dispose() => _listener.Dispose();
    }
}
