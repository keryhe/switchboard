using Keryhe.Switchboard.LoadHarness;
using Microsoft.AspNetCore.SignalR.Client;

// Plan decision D33: the concurrent-load half of "benchmarking" — negotiate throughput, sustained
// fan-out, connection ramp, memory per connection — none of which BenchmarkDotNet (a
// single-threaded microbenchmark harness) can answer. Run with:
//   dotnet run -c Release --project tests/Keryhe.Switchboard.LoadHarness -- --target-clients 10000

var targetClients = 10_000;
var skipOtlp = false;
var documentPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "docs", "12-performance.md"));

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--target-clients" when i + 1 < args.Length:
            targetClients = int.Parse(args[++i]);
            break;
        case "--skip-otlp":
            skipOtlp = true;
            break;
        case "--output" when i + 1 < args.Length:
            documentPath = Path.GetFullPath(args[++i]);
            break;
    }
}

Console.WriteLine("=== Switchboard load harness (Phase 5 Slice 5, plan decisions D33/D35) ===");
Console.WriteLine();

Console.WriteLine("--- Host limits ---");
var hostLimits = await HostLimitsReport.CollectAsync();
Console.WriteLine(hostLimits);
Console.WriteLine();

OtlpPercentileReader? otlpReader = null;
var otlpUnavailableReason = skipOtlp ? "the --skip-otlp flag was passed for this run" : null;
if (!skipOtlp)
{
    Console.WriteLine("--- Starting OTLP collector (Docker) for the service-reported latency cross-check ---");
    otlpReader = new OtlpPercentileReader();
    await otlpReader.InitializeAsync();
    Console.WriteLine(otlpReader.IsAvailable
        ? $"Collector ready at {otlpReader.OtlpGrpcEndpoint}."
        : $"Skipping OTLP cross-check (untested, D34-style): {otlpReader.UnavailableReason}");
    if (!otlpReader.IsAvailable)
    {
        otlpUnavailableReason = otlpReader.UnavailableReason;
    }

    Console.WriteLine();
}

Console.WriteLine("--- Starting Keryhe.Switchboard.Server + SampleChatApp.Api ---");
await using var host = await LoadTestHost.StartAsync(otlpReader is { IsAvailable: true } ? otlpReader.OtlpGrpcEndpoint : null);
Console.WriteLine($"Service: {host.Service.BaseUrl}   API: {host.Api.BaseUrl}");
Console.WriteLine();

// Let the process settle (JIT warmup, initial GC) before taking the baseline — otherwise the
// "per connection" delta below double-counts one-time startup cost.
await Task.Delay(TimeSpan.FromSeconds(2));
var baselineWorkingSet = host.Service.CurrentWorkingSetBytes();
Console.WriteLine($"Baseline service RSS: {baselineWorkingSet / 1024.0 / 1024.0:N1} MB");
Console.WriteLine();

Console.WriteLine($"--- Connection ramp (target {targetClients:N0}) ---");
var ramp = new ConnectionRamp(host.Api.BaseUrl);
var lastReported = 0;
var progress = new Progress<int>(connected =>
{
    if (connected - lastReported >= 500 || connected == targetClients)
    {
        Console.WriteLine($"  connected: {connected:N0}");
        lastReported = connected;
    }
});

var (rampResult, connections) = await ramp.RunAsync(targetClients, progress);
Console.WriteLine($"Connected {rampResult.Connected:N0}/{rampResult.Requested:N0} in {rampResult.Duration.TotalSeconds:N1}s ({rampResult.NegotiateThroughputPerSecond:N1}/s). Stop reason: {rampResult.StopReason}{(rampResult.StopDetail is null ? "" : $" — {rampResult.StopDetail}")}");
foreach (var (category, count) in rampResult.FailuresByCategory)
{
    Console.WriteLine($"  {category}: {count}");
}

Console.WriteLine();

var plateauWorkingSet = host.Service.CurrentWorkingSetBytes();
Console.WriteLine($"Plateau service RSS: {plateauWorkingSet / 1024.0 / 1024.0:N1} MB (delta {(plateauWorkingSet - baselineWorkingSet) / 1024.0 / 1024.0:N1} MB over {rampResult.Connected:N0} connections)");
Console.WriteLine();

FanOutResult? fanOutResult = null;
if (connections.Count > 0)
{
    Console.WriteLine("--- Sustained fan-out (one broadcast to every connected client) ---");
    var fanOut = new FanOutLoad(host.Service.BaseUrl, host.ManagementToken);
    fanOutResult = await fanOut.RunAsync(connections, TimeSpan.FromSeconds(60));
    Console.WriteLine($"Delivered {fanOutResult.DeliveredCount:N0}/{fanOutResult.TargetCount:N0} in {fanOutResult.TimeToFullOrTimeout.TotalMilliseconds:N0}ms ({fanOutResult.MessagesPerSecond:N0}/s). P50={fanOutResult.P50Latency.TotalMilliseconds:N1}ms P95={fanOutResult.P95Latency.TotalMilliseconds:N1}ms P99={fanOutResult.P99Latency.TotalMilliseconds:N1}ms");
    Console.WriteLine();
}

HistogramPercentiles? inboundPercentiles = null;
HistogramPercentiles? outboundPercentiles = null;
if (otlpReader is { IsAvailable: true })
{
    Console.WriteLine("--- Reading service-reported latency histograms from the OTLP collector ---");
    // The export interval was set to 1s (LoadTestHost), but give the pipeline a moment past that
    // to guarantee at least one export cycle completed after the fan-out above.
    await Task.Delay(TimeSpan.FromSeconds(3));
    inboundPercentiles = await otlpReader.ReadHistogramAsync("inbound_duration");
    outboundPercentiles = await otlpReader.ReadHistogramAsync("outbound_duration");
    Console.WriteLine(outboundPercentiles is null
        ? "No outbound_duration samples observed (expected if no client-to-server messages triggered inbound routing during this run's fan-out, which is server-originated)."
        : $"outbound_duration: P50={outboundPercentiles.P50Ms:N1}ms P95={outboundPercentiles.P95Ms:N1}ms P99={outboundPercentiles.P99Ms:N1}ms n={outboundPercentiles.SampleCount}");
    Console.WriteLine();
}

Console.WriteLine($"--- Writing {documentPath} ---");
await PerformanceReportWriter.WriteAsync(
    documentPath, hostLimits, rampResult, baselineWorkingSet, plateauWorkingSet, fanOutResult,
    inboundPercentiles, outboundPercentiles, otlpUnavailableReason, DateTimeOffset.UtcNow);

Console.WriteLine("--- Cleaning up connections ---");
await Task.WhenAll(connections.Select(async c =>
{
    try
    {
        await c.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    catch
    {
        // Best-effort — the process is about to be torn down anyway.
    }

    await c.DisposeAsync();
}));

if (otlpReader is not null)
{
    await otlpReader.DisposeAsync();
}

Console.WriteLine("Done.");
