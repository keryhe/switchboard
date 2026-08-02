using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Keryhe.Switchboard.LoadHarness;

/// <summary>
/// Plan decision D35: the host's own limits, recorded alongside every run — a number from one
/// machine can be compared honestly against a number from another only if both carry the limits
/// that shaped them. Finding 8: on macOS, the ephemeral port range is 16,384 ports by default
/// (<c>net.inet.ip.portrange.first</c>/<c>.last</c>), shared with the service's own outbound
/// connections (app-server pool, Orleans silo-to-silo, OTLP exporter) — this is the ceiling a
/// large connection ramp runs into first, well before <c>ulimit -n</c>.
/// </summary>
public sealed record HostLimits(int MaxOpenFiles, int? EphemeralPortFirst, int? EphemeralPortLast)
{
    public int? EphemeralPortRangeSize => EphemeralPortFirst is null || EphemeralPortLast is null
        ? null
        : EphemeralPortLast - EphemeralPortFirst + 1;

    public override string ToString() =>
        $"ulimit -n = {MaxOpenFiles}; ephemeral ports = {EphemeralPortFirst}-{EphemeralPortLast} ({EphemeralPortRangeSize} total)";
}

public static class HostLimitsReport
{
    public static async Task<HostLimits> CollectAsync()
    {
        var maxOpenFiles = await RunShellAsync("ulimit -n");
        var maxOpenFilesValue = int.TryParse(maxOpenFiles.Trim(), out var n) ? n : -1;

        int? first = null;
        int? last = null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var output = await RunProcessAsync("sysctl", "net.inet.ip.portrange.first net.inet.ip.portrange.last");
            first = ParseSysctlValue(output, "net.inet.ip.portrange.first");
            last = ParseSysctlValue(output, "net.inet.ip.portrange.last");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var path = "/proc/sys/net/ipv4/ip_local_port_range";
            if (File.Exists(path))
            {
                var parts = (await File.ReadAllTextAsync(path)).Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out var f) && int.TryParse(parts[1], out var l))
                {
                    first = f;
                    last = l;
                }
            }
        }

        return new HostLimits(maxOpenFilesValue, first, last);
    }

    private static int? ParseSysctlValue(string sysctlOutput, string key)
    {
        foreach (var line in sysctlOutput.Split('\n'))
        {
            var prefix = $"{key}:";
            if (line.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(line[prefix.Length..].Trim(), out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static Task<string> RunShellAsync(string command) => RunProcessAsync("bash", $"-c \"{command}\"");

    private static async Task<string> RunProcessAsync(string fileName, string arguments)
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
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }
}
