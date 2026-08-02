using System.Diagnostics;
using Xunit;

namespace Keryhe.Switchboard.CompatibilityTests;

/// <summary>
/// Runs <c>npm install</c> (once, idempotently — skipped if <c>node_modules</c> already exists)
/// for both JS probe versions (tests/clients/js/v8, tests/clients/js/v10 — finding 9) before any
/// <c>node probe.mjs</c> invocation. Node/npm are treated as an ordinary required toolchain here,
/// not a Docker-style optional dependency (unlike the Java row, D34) — Slice 2 does not gate them.
/// </summary>
public sealed class JsClientFixture : IAsyncLifetime
{
    public string V8ProbeDir { get; } = FindClientsDir("v8");
    public string V10ProbeDir { get; } = FindClientsDir("v10");

    public async Task InitializeAsync()
    {
        await EnsureInstalledAsync(V8ProbeDir);
        await EnsureInstalledAsync(V10ProbeDir);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task EnsureInstalledAsync(string dir)
    {
        if (Directory.Exists(Path.Combine(dir, "node_modules")))
        {
            return;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("npm", "install --no-audit --no-fund")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(cts.Token);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"npm install failed in {dir} (exit {process.ExitCode}): {await stdOutTask}\n{await stdErrTask}");
        }
    }

    private static string FindClientsDir(string version)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "clients", "js", version);
        var full = Path.GetFullPath(candidate);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"JS probe directory not found at {full}.");
        }

        return full;
    }
}
