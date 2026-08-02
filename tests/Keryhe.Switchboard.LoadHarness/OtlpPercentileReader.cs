using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Keryhe.Switchboard.LoadHarness;

public sealed record HistogramPercentiles(double P50Ms, double P95Ms, double P99Ms, long SampleCount);

/// <summary>
/// Plan §"Latency percentiles come from the service, not the harness" (D33): Phase 4 already ships
/// <c>signalr.message.inbound_duration</c>/<c>outbound_duration</c> as OTLP histograms — this reads
/// them back via a throwaway <c>otel/opentelemetry-collector</c> container's Prometheus exporter
/// (the same no-Testcontainers-dependency, <c>IsAvailable=false</c>-when-Docker-absent pattern as
/// <c>OtlpCollectorContainerFixture</c>/<c>PostgresContainerFixture</c>) rather than re-deriving
/// latency from client-side timestamps. Percentiles are approximated by linear interpolation across
/// the OTel SDK's default histogram bucket boundaries — coarse, but the point of this cross-check
/// is catching a large divergence from <see cref="FanOutLoad"/>'s independent end-to-end
/// measurement, not a precise SLO number.
/// </summary>
public sealed class OtlpPercentileReader : IAsyncDisposable
{
    private const string CollectorConfig = """
        receivers:
          otlp:
            protocols:
              grpc:
                endpoint: 0.0.0.0:4317
        exporters:
          prometheus:
            endpoint: 0.0.0.0:8889
        service:
          pipelines:
            metrics:
              receivers: [otlp]
              exporters: [prometheus]
        """;

    private string? _containerName;
    private string? _configFilePath;
    private int? _prometheusPort;

    public bool IsAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }
    public string OtlpGrpcEndpoint { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (!await RunSucceedsAsync("docker", "version --format {{.Server.Version}}"))
        {
            UnavailableReason = "docker CLI not available or daemon not running";
            return;
        }

        _configFilePath = Path.Combine(Path.GetTempPath(), $"switchboard-loadharness-otelcol-{Guid.NewGuid():n}.yaml");
        await File.WriteAllTextAsync(_configFilePath, CollectorConfig);

        _containerName = $"switchboard-loadharness-otelcol-{Guid.NewGuid():n}";
        // Explicit -p mappings, not -P (publish-all-exposed) — the base image only declares
        // EXPOSE for the default OTLP ports, not our custom Prometheus exporter port 8889, so -P
        // alone silently never publishes it (verified: docker port then finds nothing to resolve).
        var run = await RunAsync("docker",
            $"run -d --rm --name {_containerName} -v {_configFilePath}:/etc/otelcol/config.yaml -p 0:4317 -p 0:8889 otel/opentelemetry-collector:latest");
        if (run.ExitCode != 0)
        {
            UnavailableReason = $"docker run failed: {run.StdErr}";
            _containerName = null;
            return;
        }

        var grpcPort = ParseHostPort((await RunAsync("docker", $"port {_containerName} 4317/tcp")).StdOut);
        _prometheusPort = ParseHostPort((await RunAsync("docker", $"port {_containerName} 8889/tcp")).StdOut);
        if (grpcPort is null || _prometheusPort is null)
        {
            UnavailableReason = "could not resolve published collector ports";
            return;
        }

        OtlpGrpcEndpoint = $"http://127.0.0.1:{grpcPort}";

        if (!await WaitUntilAcceptingConnectionsAsync(grpcPort.Value, TimeSpan.FromSeconds(30)))
        {
            UnavailableReason = "OTLP collector did not become reachable within 30s";
            return;
        }

        IsAvailable = true;
    }

    /// <summary>Scrapes the collector's own <c>/metrics</c> endpoint and computes approximate
    /// percentiles for whichever metric name fragment matches (e.g. <c>"outbound_duration"</c>) —
    /// matched by substring rather than an exact Prometheus name, since the OTel collector's name
    /// translation (dots → underscores, unit suffixing) isn't a contract this harness should
    /// depend on precisely.</summary>
    public async Task<HistogramPercentiles?> ReadHistogramAsync(string metricNameFragment)
    {
        if (!IsAvailable)
        {
            return null;
        }

        using var http = new HttpClient();
        var text = await http.GetStringAsync($"http://127.0.0.1:{_prometheusPort}/metrics");

        var buckets = new SortedDictionary<double, long>();
        long? totalCount = null;

        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains(metricNameFragment, StringComparison.Ordinal) || line.StartsWith('#'))
            {
                continue;
            }

            var bucketMatch = Regex.Match(line, """_bucket\{[^}]*le="([^"]+)"[^}]*\}\s+([0-9.eE+]+)""");
            if (bucketMatch.Success)
            {
                var le = bucketMatch.Groups[1].Value == "+Inf" ? double.PositiveInfinity : double.Parse(bucketMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                var count = (long)double.Parse(bucketMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                buckets[le] = count;
                continue;
            }

            var countMatch = Regex.Match(line, @"_count(\{[^}]*\})?\s+([0-9.eE+]+)$");
            if (countMatch.Success)
            {
                totalCount = (long)double.Parse(countMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            }
        }

        if (buckets.Count == 0 || totalCount is null or 0)
        {
            return null;
        }

        return new HistogramPercentiles(
            InterpolatePercentile(buckets, totalCount.Value, 0.50),
            InterpolatePercentile(buckets, totalCount.Value, 0.95),
            InterpolatePercentile(buckets, totalCount.Value, 0.99),
            totalCount.Value);
    }

    private static double InterpolatePercentile(SortedDictionary<double, long> cumulativeBuckets, long totalCount, double percentile)
    {
        var target = totalCount * percentile;
        double previousBoundary = 0;
        long previousCount = 0;

        foreach (var (boundary, cumulativeCount) in cumulativeBuckets)
        {
            if (cumulativeCount >= target)
            {
                if (double.IsPositiveInfinity(boundary))
                {
                    return previousBoundary;
                }

                var bucketRange = boundary - previousBoundary;
                var countInBucket = cumulativeCount - previousCount;
                var fraction = countInBucket == 0 ? 0 : (target - previousCount) / countInBucket;
                return previousBoundary + fraction * bucketRange;
            }

            previousBoundary = boundary;
            previousCount = cumulativeCount;
        }

        return previousBoundary;
    }

    public async ValueTask DisposeAsync()
    {
        if (_containerName is not null)
        {
            await RunAsync("docker", $"rm -f {_containerName}");
        }

        if (_configFilePath is not null && File.Exists(_configFilePath))
        {
            File.Delete(_configFilePath);
        }
    }

    private static async Task<bool> WaitUntilAcceptingConnectionsAsync(int port, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync("127.0.0.1", port, cts.Token);
                return true;
            }
            catch
            {
                try
                {
                    await Task.Delay(500, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        return false;
    }

    private static int? ParseHostPort(string dockerPortOutput)
    {
        var firstLine = dockerPortOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var separatorIndex = firstLine?.LastIndexOf(':') ?? -1;
        return separatorIndex >= 0 && int.TryParse(firstLine![(separatorIndex + 1)..].Trim(), out var port) ? port : null;
    }

    private static async Task<bool> RunSucceedsAsync(string fileName, string arguments) =>
        (await RunAsync(fileName, arguments)).ExitCode == 0;

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string fileName, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };

            process.Start();
            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(cts.Token);

            return (process.ExitCode, await stdOutTask, await stdErrTask);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
