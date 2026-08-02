using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Keryhe.Switchboard.CompatibilityTests;

/// <summary>
/// Runs the Java probe (tests/clients/java) inside a throwaway <c>maven:3-eclipse-temurin-17</c>
/// container via the <c>docker</c> CLI — the same no-Testcontainers pattern as
/// <c>PostgresContainerFixture</c>/<c>OtlpCollectorContainerFixture</c> (D34), needed because
/// neither <c>mvn</c> nor <c>gradle</c> is installed on this host (finding 5). A named volume
/// caches the resolved Maven dependencies (including the exec plugin) across the handful of probe
/// invocations one test run makes, so only the first cell pays the cold-resolve cost.
/// <see cref="IsAvailable"/> is <c>false</c> (never throws) when Docker isn't present — an
/// unavailable Java row must show up in the compatibility matrix as "untested", not silently
/// vanish (D36).
/// </summary>
public sealed class JavaClientContainerFixture : IAsyncLifetime
{
    private const string Image = "maven:3-eclipse-temurin-17";

    public bool IsAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }

    private string _probeDir = null!;
    private string _m2VolumeName = null!;

    public async Task InitializeAsync()
    {
        if (!await RunSucceedsAsync("docker", "version --format {{.Server.Version}}"))
        {
            UnavailableReason = "docker CLI not available or daemon not running";
            return;
        }

        _probeDir = FindProbeDir();
        _m2VolumeName = $"switchboard-compat-java-m2-{Guid.NewGuid():n}";

        var volumeCreate = await RunAsync("docker", $"volume create {_m2VolumeName}");
        if (volumeCreate.ExitCode != 0)
        {
            UnavailableReason = $"docker volume create failed: {volumeCreate.StdErr}";
            return;
        }

        // Warms the dependency cache and compiles Probe.java once, online, so every
        // RunProbeAsync call after this can run `exec:java` directly, offline (`-o`). Three
        // things, verified individually while writing this fixture because each one's absence
        // fails a different, non-obvious way: `compile` produces target/classes (`exec:java` is a
        // bare goal invocation, not bound to any lifecycle phase in this pom, so it never compiles
        // on its own — skipping this leaves target/classes empty and every probe run fails with
        // "An exception occurred while executing the Java class", a ClassNotFoundException);
        // `dependency:resolve` pulls every *runtime*-scope transitive jar (okhttp, kotlin-stdlib,
        // slf4j-api) that `compile` alone does not need and therefore does not fetch, so without
        // it the offline exec:java fails on missing runtime dependencies even though compilation
        // itself succeeded; `exec:help` forces the exec-maven-plugin's own jar to resolve.
        var warm = await RunAsync("docker", BuildDockerArgs("mvn -q compile dependency:resolve exec:help"), TimeSpan.FromMinutes(5));
        if (warm.ExitCode != 0)
        {
            UnavailableReason = $"maven dependency warm-up failed: {warm.StdOut}\n{warm.StdErr}";
            return;
        }

        IsAvailable = true;
    }

    /// <summary>
    /// Runs the probe against <paramref name="apiBaseUrl"/> (a host-loopback address, e.g.
    /// <c>http://127.0.0.1:54231</c> from <c>ProcessFixture.BaseUrl</c>) — rewritten to
    /// <c>host.docker.internal</c> so the container, which cannot reach the host's loopback
    /// interface directly, can still reach it.
    /// </summary>
    public async Task<ProbeRunner.ProbeResult> RunProbeAsync(string apiBaseUrl, string transport, string protocol, TimeSpan timeout)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("JavaClientContainerFixture is not available — check IsAvailable/UnavailableReason first.");
        }

        var containerUrl = Regex.Replace(apiBaseUrl, @"(127\.0\.0\.1|localhost)", "host.docker.internal");
        var result = await RunAsync(
            "docker",
            BuildDockerArgs($"mvn -q -o exec:java -Dexec.mainClass=Probe -Dexec.args=\"{containerUrl} {transport} {protocol}\""),
            timeout);

        var lastResultLine = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.StartsWith("RESULT ", StringComparison.Ordinal));

        if (lastResultLine is null)
        {
            return new ProbeRunner.ProbeResult(false, [], "RESULT FAIL step=unknown reason=no-result-line",
                $"{result.StdOut}\n--- stderr ---\n{result.StdErr}", result.ExitCode);
        }

        var success = lastResultLine.StartsWith("RESULT OK", StringComparison.Ordinal);
        const string marker = "steps=";
        var index = lastResultLine.IndexOf(marker, StringComparison.Ordinal);
        var steps = index < 0
            ? []
            : lastResultLine[(index + marker.Length)..].Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new ProbeRunner.ProbeResult(success, steps, lastResultLine, $"{result.StdOut}\n--- stderr ---\n{result.StdErr}", result.ExitCode);
    }

    private string BuildDockerArgs(string mvnCommand) =>
        // --add-host is a no-op on Docker Desktop (host.docker.internal already resolves there)
        // and required on Linux Docker, where it doesn't exist by default.
        $"run --rm --add-host=host.docker.internal:host-gateway " +
        $"-v {_probeDir}:/probe -v {_m2VolumeName}:/root/.m2 -w /probe {Image} {mvnCommand}";

    public async Task DisposeAsync()
    {
        if (_m2VolumeName is not null)
        {
            await RunAsync("docker", $"volume rm -f {_m2VolumeName}");
        }
    }

    private static string FindProbeDir()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "clients", "java");
        var full = Path.GetFullPath(candidate);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Java probe directory not found at {full}.");
        }

        return full;
    }

    private static async Task<bool> RunSucceedsAsync(string fileName, string arguments) =>
        (await RunAsync(fileName, arguments)).ExitCode == 0;

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string fileName, string arguments) =>
        RunAsync(fileName, arguments, TimeSpan.FromSeconds(60));

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string fileName, string arguments, TimeSpan timeout)
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
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);

            return (process.ExitCode, await stdOutTask, await stdErrTask);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
