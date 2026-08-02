using System.Text;

namespace Keryhe.Switchboard.LoadHarness;

/// <summary>
/// Writes docs/docs/12-performance.md from one real run's observations (plan §7) — "the tuning
/// guide is written from these observations," not from general advice, which is what makes it
/// worth having.
/// </summary>
public static class PerformanceReportWriter
{
    public static async Task WriteAsync(
        string path,
        HostLimits hostLimits,
        RampResult ramp,
        long baselineWorkingSetBytes,
        long plateauWorkingSetBytes,
        FanOutResult? fanOut,
        HistogramPercentiles? serviceInboundPercentiles,
        HistogramPercentiles? serviceOutboundPercentiles,
        string? otlpUnavailableReason,
        DateTimeOffset generatedAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Observed Limits and Tuning Guide");
        sb.AppendLine();
        sb.AppendLine("**Generated from one real run, not general advice** — plan decision D33/D35"
            + " ([plans/phase-5-compatibility-testing-and-benchmarking.md](../../plans/phase-5-compatibility-testing-and-benchmarking.md))."
            + " Produced by `tests/Keryhe.Switchboard.LoadHarness` against a real out-of-process"
            + " `Keryhe.Switchboard.Server` + `SampleChatApp.Api` pair on a single machine. Every number"
            + " below came from that run; none are estimates.");
        sb.AppendLine();
        sb.AppendLine($"Last generated: {generatedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        sb.AppendLine("## Host this run came from");
        sb.AppendLine();
        sb.AppendLine($"- `ulimit -n` (max open files): **{hostLimits.MaxOpenFiles}**");
        sb.AppendLine($"- Ephemeral port range: **{hostLimits.EphemeralPortFirst}–{hostLimits.EphemeralPortLast}** ({hostLimits.EphemeralPortRangeSize} ports total)");
        sb.AppendLine();
        sb.AppendLine("A number from this document is only comparable to a number from a different machine if both carry these limits — see plan decision D35's finding 8: the ephemeral port range, not `ulimit -n`, is usually what a large connection ramp hits first, since it's shared with the service's own outbound connections (app-server pool, Orleans silo-to-silo, OTLP exporter).");
        sb.AppendLine();

        sb.AppendLine("## Connection ramp");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Requested | {ramp.Requested:N0} |");
        sb.AppendLine($"| Connected | {ramp.Connected:N0} |");
        sb.AppendLine($"| Stop reason | {DescribeStopReason(ramp)} |");
        sb.AppendLine($"| Duration | {ramp.Duration.TotalSeconds:N1} s |");
        sb.AppendLine($"| Negotiate throughput | {ramp.NegotiateThroughputPerSecond:N1} connections/sec |");
        sb.AppendLine();

        if (ramp.FailuresByCategory.Count > 0)
        {
            sb.AppendLine("Failures, classified by cause (plan decision D35) — only `HandshakeTimeout` and `Other` are candidate service defects; everything else is either the host's own ceiling or the service's documented, correct backpressure:");
            sb.AppendLine();
            sb.AppendLine("| Category | Count |");
            sb.AppendLine("|---|---|");
            foreach (var (category, count) in ramp.FailuresByCategory.OrderByDescending(kv => kv.Value))
            {
                sb.AppendLine($"| {category} | {count:N0} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Memory per connection");
        sb.AppendLine();
        var deltaBytes = plateauWorkingSetBytes - baselineWorkingSetBytes;
        var perConnectionBytes = ramp.Connected > 0 ? deltaBytes / (double)ramp.Connected : 0;
        sb.AppendLine($"- Baseline RSS (service process, before ramp): {FormatBytes(baselineWorkingSetBytes)}");
        sb.AppendLine($"- Plateau RSS (service process, after ramp of {ramp.Connected:N0}): {FormatBytes(plateauWorkingSetBytes)}");
        sb.AppendLine($"- Delta: {FormatBytes(deltaBytes)}");
        sb.AppendLine($"- **Memory per connection: {FormatBytes((long)perConnectionBytes)}** (RSS delta ÷ connection count, a real measurement of the service process, not an estimate)");
        sb.AppendLine();

        if (fanOut is not null)
        {
            sb.AppendLine("## Sustained fan-out");
            sb.AppendLine();
            sb.AppendLine("| Metric | Value |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| Targets | {fanOut.TargetCount:N0} |");
            sb.AppendLine($"| Delivered | {fanOut.DeliveredCount:N0} ({(fanOut.DeliveredToAll ? "all" : "not all — see time-to-delivery, likely timed out")}) |");
            sb.AppendLine($"| Time to full delivery (or timeout) | {fanOut.TimeToFullOrTimeout.TotalMilliseconds:N0} ms |");
            sb.AppendLine($"| Throughput | {fanOut.MessagesPerSecond:N0} messages/sec |");
            sb.AppendLine();
            sb.AppendLine("Harness-observed end-to-end latency (client-side: send timestamp → receive timestamp), independent of the service's own instrumentation:");
            sb.AppendLine();
            sb.AppendLine("| Percentile | Harness-observed | Service-reported (`outbound_duration`) |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine($"| P50 | {fanOut.P50Latency.TotalMilliseconds:N1} ms | {FormatServiceMs(serviceOutboundPercentiles?.P50Ms)} |");
            sb.AppendLine($"| P95 | {fanOut.P95Latency.TotalMilliseconds:N1} ms | {FormatServiceMs(serviceOutboundPercentiles?.P95Ms)} |");
            sb.AppendLine($"| P99 | {fanOut.P99Latency.TotalMilliseconds:N1} ms | {FormatServiceMs(serviceOutboundPercentiles?.P99Ms)} |");
            sb.AppendLine();

            if (serviceOutboundPercentiles is null)
            {
                var reason = otlpUnavailableReason ?? "no samples were recorded during this run's fan-out";
                sb.AppendLine($"Service-reported percentiles: **untested** — {reason}. The OTLP collector cross-check (plan §\"Latency percentiles come from the service, not the harness,\" D33) did not produce a comparison for this run. The harness-observed column above is still real; it just has no independent cross-check here.");
            }
            else
            {
                var divergence = Math.Abs(fanOut.P95Latency.TotalMilliseconds - serviceOutboundPercentiles.P95Ms);
                sb.AppendLine(divergence > 20
                    ? $"**Divergence at P95: {divergence:N1} ms.** Per plan §4, a large divergence between the harness's own end-to-end timing and the service's own routing-cost histogram is itself a finding, not something to reconcile by construction — the harness's timing includes client scheduling, network, and SignalR client deserialization overhead the service's `outbound_duration` histogram was deliberately designed to exclude ([04-design.md §13](../04-design.md#13-observability-phase-4)), so some divergence is expected; investigate only if it's large relative to the absolute latency."
                    : $"Divergence at P95: {divergence:N1} ms — small relative to the absolute latency, consistent with the service's `outbound_duration` histogram measuring only its own honest contribution rather than the full client-observed round trip.");
            }

            sb.AppendLine();
            if (serviceInboundPercentiles is not null)
            {
                sb.AppendLine($"Service-reported `inbound_duration` (client → assigned server connection, not exercised directly by this fan-out — included for completeness): P50 {serviceInboundPercentiles.P50Ms:N1} ms, P95 {serviceInboundPercentiles.P95Ms:N1} ms, P99 {serviceInboundPercentiles.P99Ms:N1} ms, n={serviceInboundPercentiles.SampleCount}.");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("## Sustained fan-out");
            sb.AppendLine();
            sb.AppendLine("Not run — no connections were established.");
            sb.AppendLine();
        }

        sb.AppendLine("## Tuning guide (written from the numbers above)");
        sb.AppendLine();
        sb.AppendLine(BuildTuningGuide(hostLimits, ramp));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, sb.ToString());
    }

    private static string DescribeStopReason(RampResult ramp) => ramp.StopReason switch
    {
        RampStopReason.ReachedTarget => "reached target — not stopped by any limit",
        RampStopReason.HostLimit => $"**host limit** — {ramp.StopDetail}",
        RampStopReason.ServiceLimit => $"service backpressure (D5, correct behavior) — {ramp.StopDetail}",
        _ => "unknown",
    };

    private static string FormatServiceMs(double? ms) => ms is null ? "untested" : $"{ms:N1} ms";

    private static string FormatBytes(long bytes)
    {
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB"];
        var unitIndex = 0;
        while (Math.Abs(value) >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:N1} {units[unitIndex]}";
    }

    private static string BuildTuningGuide(HostLimits hostLimits, RampResult ramp)
    {
        var sb = new StringBuilder();

        if (ramp.StopReason == RampStopReason.HostLimit)
        {
            sb.AppendLine($"This run stopped at **{ramp.Connected:N0}** connections because of a **host** limit, not a service limit: {ramp.StopDetail}");
            sb.AppendLine();
            sb.AppendLine("To push past this on the same machine:");
            sb.AppendLine();
            sb.AppendLine($"- Raise `ulimit -n` (currently {hostLimits.MaxOpenFiles}) — each client connection and each of the service's own outbound connections (app-server pool, Orleans silo-to-silo if clustered, OTLP exporter) holds a file descriptor.");
            sb.AppendLine($"- The ephemeral port range (currently {hostLimits.EphemeralPortFirst}–{hostLimits.EphemeralPortLast}, {hostLimits.EphemeralPortRangeSize} ports) is shared by every loopback connection this machine makes during the run, including the harness's own HTTP client and the service's outbound connections — widen it (macOS: `sudo sysctl -w net.inet.ip.portrange.first=32768`) or run client and service on separate machines so they draw from independent port spaces.");
            sb.AppendLine("- `TIME_WAIT` from a previous run can exhaust the range even before this run starts — avoid back-to-back runs on the same host without a pause (plan decision D35's finding 8).");
        }
        else if (ramp.StopReason == RampStopReason.ServiceLimit)
        {
            sb.AppendLine($"This run stopped at **{ramp.Connected:N0}** connections because the service returned 503 during negotiate — this is D5's documented backpressure under overload, not a defect. {ramp.StopDetail}");
            sb.AppendLine();
            sb.AppendLine("If more concurrent connections are needed in production, scale the app-server pool (`ServerConnectionsPerHub`) and/or add nodes to the cluster (`UseOrleansCluster=true`) rather than treating this as something to tune away on a single node.");
        }
        else
        {
            sb.AppendLine($"This run reached its full target of **{ramp.Requested:N0}** connections without hitting either a host or a service limit. To find the actual ceiling on this machine, re-run with a higher `--target-clients`.");
        }

        return sb.ToString();
    }
}
