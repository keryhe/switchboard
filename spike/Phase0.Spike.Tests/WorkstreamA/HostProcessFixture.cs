using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace Phase0.Spike.Tests.WorkstreamA;

/// <summary>
/// A5 needs a real out-of-process Kestrel host so an unmodified HubConnection/@microsoft/signalr
/// client actually opens a real WebSocket -- TestServer's in-memory transport doesn't exercise
/// the real socket upgrade path. Starts `dotnet <Host.dll>` once per test collection.
/// </summary>
public sealed class HostProcessFixture : IAsyncLifetime
{
    private Process? _process;
    public string BaseUrl { get; } = "http://localhost:5559";

    public async Task InitializeAsync()
    {
        var hostDll = FindHostAssembly();

        _process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{hostDll}\" --urls {BaseUrl}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        _process.Start();

        using var client = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await client.GetAsync($"{BaseUrl}/__diag/endpoints");
                if (response.IsSuccessStatusCode)
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

        throw new InvalidOperationException("Host process did not become ready in time.", lastError);
    }

    public async Task<JsonElement> GetStubObservedAsync()
    {
        using var client = new HttpClient();
        var response = await client.GetAsync($"{BaseUrl}/__diag/stub-observed");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public Task DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
        }

        _process?.Dispose();
        return Task.CompletedTask;
    }

    private static string FindHostAssembly()
    {
        var candidate = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "Phase0.Spike.Host", "bin", "Debug", "net10.0", "Phase0.Spike.Host.dll");
        var full = Path.GetFullPath(candidate);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(
                $"Host assembly not found at {full}. Build Phase0.Spike.Host before running A5 tests.", full);
        }

        return full;
    }
}

[CollectionDefinition("HostProcess")]
public sealed class HostProcessCollection : ICollectionFixture<HostProcessFixture>;
