using System.Diagnostics;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.TestSupport;

/// <summary>
/// Starts a throwaway <c>otel/opentelemetry-collector</c> container (via the <c>docker</c> CLI,
/// mirroring <see cref="PostgresContainerFixture"/>'s no-Testcontainers-dependency pattern) for the
/// Phase 4 Slice 6 milestone: "metrics from a running two-node cluster are received by a real OTLP
/// collector" (plans/phase-4-management-and-observability.md §4). A <c>debug</c> exporter writes
/// every received metric to the collector's own stdout, verbosity <c>detailed</c> — the milestone's
/// assertion is on the collector's received data (via <c>docker logs</c>), never on the service
/// having merely started, per finding 3: a misconfigured OTLP endpoint fails completely silently,
/// so "the exporter is configured" proves nothing on its own.
/// <see cref="IsAvailable"/> is <c>false</c> (rather than throwing) when Docker isn't present, the
/// same posture <see cref="PostgresContainerFixture"/> uses.
/// </summary>
public sealed class OtlpCollectorContainerFixture : IAsyncLifetime
{
    private const string CollectorConfig = """
        receivers:
          otlp:
            protocols:
              grpc:
                endpoint: 0.0.0.0:4317
        exporters:
          debug:
            verbosity: detailed
        service:
          pipelines:
            metrics:
              receivers: [otlp]
              exporters: [debug]
        """;

    private string? _containerName;
    private string? _configFilePath;

    public bool IsAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }

    /// <summary>gRPC OTLP endpoint (<c>http://127.0.0.1:{port}</c>) — the exporter protocol
    /// <c>AddOtlpExporter</c> defaults to, so no protocol override is needed at the call site.</summary>
    public string OtlpEndpoint { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (!await RunSucceedsAsync("docker", "version --format {{.Server.Version}}"))
        {
            UnavailableReason = "docker CLI not available or daemon not running";
            return;
        }

        _configFilePath = Path.Combine(Path.GetTempPath(), $"switchboard-otelcol-{Guid.NewGuid():n}.yaml");
        await File.WriteAllTextAsync(_configFilePath, CollectorConfig);

        _containerName = $"switchboard-slice6-otelcol-{Guid.NewGuid():n}";
        var run = await RunAsync("docker",
            $"run -d --rm --name {_containerName} -v {_configFilePath}:/etc/otelcol/config.yaml -P otel/opentelemetry-collector:latest");
        if (run.ExitCode != 0)
        {
            UnavailableReason = $"docker run failed: {run.StdErr}";
            _containerName = null;
            return;
        }

        var portResult = await RunAsync("docker", $"port {_containerName} 4317/tcp");
        var hostPort = ParseHostPort(portResult.StdOut);
        if (hostPort is null)
        {
            UnavailableReason = $"could not resolve published OTLP gRPC port: {portResult.StdOut} {portResult.StdErr}";
            return;
        }

        OtlpEndpoint = $"http://127.0.0.1:{hostPort}";

        if (!await WaitUntilAcceptingConnectionsAsync(hostPort.Value, TimeSpan.FromSeconds(30)))
        {
            UnavailableReason = "OTLP collector did not become reachable within 30s";
            return;
        }

        IsAvailable = true;
    }

    /// <summary>Polls <c>docker logs</c> for a substring — the collector's <c>debug</c> exporter
    /// prints each received metric's name in its stdout output, so this is how the milestone
    /// asserts on the collector's actual received data rather than on the service having started.</summary>
    public async Task<bool> WaitForLogsToContainAsync(string substring, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            var logs = await RunAsync("docker", $"logs {_containerName}");
            if (logs.StdOut.Contains(substring, StringComparison.Ordinal) || logs.StdErr.Contains(substring, StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                await Task.Delay(500, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    public async Task DisposeAsync()
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
