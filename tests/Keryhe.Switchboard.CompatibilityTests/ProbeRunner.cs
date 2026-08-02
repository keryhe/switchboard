using System.Diagnostics;
using System.Text;

namespace Keryhe.Switchboard.CompatibilityTests;

/// <summary>
/// Spawns a client probe (ClientProbeContract.md) as a real out-of-process executable and parses
/// its final <c>RESULT OK|FAIL ...</c> line. Mirrors <c>ProcessFixture</c>'s own spawn/drain shape
/// rather than inventing a second out-of-process mechanism (plan decision D30).
/// </summary>
public static class ProbeRunner
{
    public sealed record ProbeResult(bool Success, IReadOnlyList<string> Steps, string RawLine, string Output, int ExitCode);

    public static async Task<ProbeResult> RunAsync(
        string command, string arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo(command, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        string? resultLine = null;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stdout)
            {
                stdout.AppendLine(e.Data);
            }

            if (e.Data.StartsWith("RESULT ", StringComparison.Ordinal))
            {
                resultLine = e.Data;
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            var combined = $"{stdout}\n--- stderr ---\n{stderr}";
            return new ProbeResult(false, [], "RESULT FAIL step=timeout reason=probe-did-not-exit", combined, -1);
        }

        var output = $"{stdout}\n--- stderr ---\n{stderr}";

        if (resultLine is null)
        {
            return new ProbeResult(false, [], "RESULT FAIL step=unknown reason=no-result-line", output, process.ExitCode);
        }

        var success = resultLine.StartsWith("RESULT OK", StringComparison.Ordinal);
        var steps = ParseSteps(resultLine);
        return new ProbeResult(success, steps, resultLine, output, process.ExitCode);
    }

    private static IReadOnlyList<string> ParseSteps(string resultLine)
    {
        const string marker = "steps=";
        var index = resultLine.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return [];
        }

        var value = resultLine[(index + marker.Length)..].Trim();
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
