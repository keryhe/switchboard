using Xunit;

namespace Keryhe.Switchboard.CompatibilityTests;

/// <summary>
/// Slice 1 gate (plan §4): the .NET 8 probe (ClientProbeContract.md) against every valid
/// {WebSockets, SSE, LongPolling} x {json, messagepack} cell, out-of-process, against a real
/// SampleChatApp.Api + Keryhe.Switchboard.Server pair — the intersection Phase 5 finding 1 found
/// was never exercised (every SSE test elsewhere in the repo drives a .NET 10 client). SSE +
/// MessagePack is not a cell here: SSE is Text-only by design (03-protocol.md §1.5).
/// </summary>
public class CompatibilityMatrixTests : IAsyncLifetime
{
    private SampleAppHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await SampleAppHost.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    public static IEnumerable<object[]> ValidCells()
    {
        yield return ["websockets", "json"];
        yield return ["websockets", "messagepack"];
        yield return ["sse", "json"];
        // ("sse", "messagepack") deliberately omitted — SSE is Text-only by design (03-protocol.md §1.5).
        yield return ["longpolling", "json"];
        yield return ["longpolling", "messagepack"];
    }

    [Theory]
    [MemberData(nameof(ValidCells))]
    public async Task DotNet8Probe_PassesEveryValidCell(string transport, string protocol)
    {
        var probeDll = FindProbeAssembly();
        var result = await ProbeRunner.RunAsync(
            "dotnet", $"\"{probeDll}\" {_host.Api.BaseUrl} {transport} {protocol}", TimeSpan.FromSeconds(45));

        Assert.True(result.Success, $"probe failed for ({transport}, {protocol}): {result.RawLine}\n{result.Output}\n--- api ---\n{_host.Api.RecentOutput}\n--- service ---\n{_host.Service.RecentOutput}");

        // Guards against a probe that silently skips a step and still prints RESULT OK — the
        // scenario in ClientProbeContract.md is fixed at six steps.
        Assert.Equal(
            new[] { "connect", "receive_push", "join_group", "invoke", "receive_group", "disconnect" },
            result.Steps);
    }

    private static string FindProbeAssembly()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "clients", "dotnet8", "bin", "Debug", "net8.0", "DotNet8Probe.dll");
        var full = Path.GetFullPath(candidate);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Probe assembly not found at {full}. Build tests/clients/dotnet8 before running compatibility tests.", full);
        }

        return full;
    }
}
