using System.Diagnostics;
using System.Text;

namespace Keryhe.Switchboard.IntegrationTests;

/// <summary>
/// Spawns a real out-of-process Kestrel host (`dotnet &lt;assembly&gt;.dll`) — an unmodified
/// HubConnection needs a real socket, which TestServer's in-memory transport doesn't provide.
/// Mirrors the proven pattern from spike/Phase0.Spike.Tests/WorkstreamA/HostProcessFixture.cs.
/// </summary>
public sealed class ProcessFixture(string assemblyPath, int port, IReadOnlyDictionary<string, string> environment) : IAsyncDisposable
{
    private Process? _process;
    private readonly StringBuilder _log = new();

    public string BaseUrl { get; } = $"http://127.0.0.1:{port}";

    /// <summary>Debug-only: everything written to stdout/stderr since the process last started, for
    /// diagnosing a hung test rather than for any assertion.</summary>
    public string RecentOutput
    {
        get
        {
            lock (_log)
            {
                return _log.ToString();
            }
        }
    }

    public Task StartAsync(string healthCheckPath, TimeSpan timeout) => SpawnAsync(healthCheckPath, timeout);

    /// <summary>
    /// Re-spawns the same process (same assembly/port/environment) after <see cref="StopGracefullyAsync"/>
    /// or <see cref="DisposeAsync"/> — Phase 3 Slice 7's rolling-restart milestone needs to bring a
    /// killed node back up on the same address rather than starting a brand-new fixture, the same
    /// way a real deployment restarts the same node rather than provisioning a new one.
    /// </summary>
    public Task RestartAsync(string healthCheckPath, TimeSpan timeout)
    {
        if (_process is { HasExited: false })
        {
            throw new InvalidOperationException("RestartAsync: the process is still running — stop it first.");
        }

        return SpawnAsync(healthCheckPath, timeout);
    }

    private async Task SpawnAsync(string healthCheckPath, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"\"{assemblyPath}\" --urls {BaseUrl}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var (key, value) in environment)
        {
            startInfo.EnvironmentVariables[key] = value;
        }

        _process = new Process { StartInfo = startInfo };
        lock (_log)
        {
            _log.Clear();
        }

        _process.Start();
        _ = DrainAsync(_process.StandardOutput, "OUT");
        _ = DrainAsync(_process.StandardError, "ERR");

        await WaitUntilHealthyAsync(healthCheckPath, timeout);
    }

    private async Task DrainAsync(StreamReader reader, string label)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                lock (_log)
                {
                    _log.AppendLine($"[{label}] {line}");
                }
            }
        }
        catch
        {
            // Best-effort diagnostics only — a drain failure (e.g. the process was killed mid-read)
            // must never surface as a test failure of its own.
        }
    }

    /// <summary>
    /// Sends SIGTERM (via the <c>kill</c> CLI, not <see cref="Process.Kill()"/> — .NET's own API has
    /// no cross-platform "request graceful shutdown" primitive, only immediate termination) so the
    /// host's real <c>IHostedService.StopAsync</c>/graceful-shutdown path actually runs, exactly what
    /// a rolling restart behind a real process supervisor does. Falls back to a hard kill if the
    /// process has not exited within <paramref name="timeout"/> (mirrors <c>Switchboard:ShutdownTimeout</c>
    /// existing for the identical reason on the host side). Unix-only — this repo's dev/CI
    /// environment; Windows has no equivalent of SIGTERM without native interop, so a real graceful
    /// stop there would need a different mechanism entirely.
    /// </summary>
    public async Task StopGracefullyAsync(TimeSpan timeout)
    {
        if (_process is not { HasExited: false } process)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            process.Kill(entireProcessTree: true);
        }
        else
        {
            using var killProcess = Process.Start(new ProcessStartInfo("kill", $"-TERM {process.Id}")
            {
                UseShellExecute = false,
            })!;
            await killProcess.WaitForExitAsync();
        }

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    private async Task WaitUntilHealthyAsync(string healthCheckPath, TimeSpan timeout)
    {
        var process = _process ?? throw new InvalidOperationException("Process was not started.");

        using var client = new HttpClient();
        var deadline = DateTime.UtcNow.Add(timeout);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Process '{assemblyPath}' exited early (code {process.ExitCode}): {RecentOutput}");
            }

            try
            {
                var response = await client.GetAsync($"{BaseUrl}{healthCheckPath}");
                if ((int)response.StatusCode < 500)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(250);
        }

        throw new InvalidOperationException($"Process '{assemblyPath}' did not become ready in time.", lastError);
    }

    public ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
        }

        _process?.Dispose();
        return ValueTask.CompletedTask;
    }
}
