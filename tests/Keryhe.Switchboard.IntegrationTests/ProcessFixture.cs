using System.Diagnostics;

namespace Keryhe.Switchboard.IntegrationTests;

/// <summary>
/// Spawns a real out-of-process Kestrel host (`dotnet &lt;assembly&gt;.dll`) — an unmodified
/// HubConnection needs a real socket, which TestServer's in-memory transport doesn't provide.
/// Mirrors the proven pattern from spike/Phase0.Spike.Tests/WorkstreamA/HostProcessFixture.cs.
/// </summary>
public sealed class ProcessFixture(string assemblyPath, int port, IReadOnlyDictionary<string, string> environment) : IAsyncDisposable
{
    private Process? _process;

    public string BaseUrl { get; } = $"http://127.0.0.1:{port}";

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

        using var client = new HttpClient();
        var deadline = DateTime.UtcNow.Add(timeout);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                var stderr = await _process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"Process '{assemblyPath}' exited early (code {_process.ExitCode}): {stderr}");
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
