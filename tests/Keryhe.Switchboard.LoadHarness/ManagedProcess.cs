using System.Diagnostics;

namespace Keryhe.Switchboard.LoadHarness;

/// <summary>
/// Spawns a real out-of-process <c>dotnet &lt;assembly&gt;.dll</c> host — the same shape
/// <c>tests/Keryhe.Switchboard.IntegrationTests/ProcessFixture.cs</c> uses, reimplemented here
/// rather than referenced because this project is a standalone console app with no xunit
/// dependency, and the server itself has no test-framework coupling to route through (verified
/// while researching this slice: it's plain <c>IConfiguration</c> from env vars/CLI args).
/// </summary>
public sealed class ManagedProcess(string assemblyPath, string baseUrl, IReadOnlyDictionary<string, string> environment) : IAsyncDisposable
{
    private Process? _process;

    public string BaseUrl { get; } = baseUrl;

    /// <summary>Real, current RSS of the running process — used for the memory-per-connection
    /// measurement (plan decision D33). <c>Process.Refresh()</c> is required; <c>WorkingSet64</c>
    /// is a snapshot from the last refresh, not live, on some platforms.</summary>
    public long CurrentWorkingSetBytes()
    {
        if (_process is not { HasExited: false } process)
        {
            throw new InvalidOperationException("Process is not running.");
        }

        process.Refresh();
        return process.WorkingSet64;
    }

    public async Task StartAsync(string healthCheckPath, TimeSpan timeout)
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
        _process.Start();

        // Drain, don't capture — a load harness run can produce megabytes of request logging at
        // high connection counts; keeping it would just be a slow memory leak in this process.
        _ = DrainAsync(_process.StandardOutput);
        _ = DrainAsync(_process.StandardError);

        await WaitUntilHealthyAsync(healthCheckPath, timeout);
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is not null)
            {
            }
        }
        catch
        {
            // Best-effort only.
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
                throw new InvalidOperationException($"Process '{assemblyPath}' exited early (code {process.ExitCode}).");
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
