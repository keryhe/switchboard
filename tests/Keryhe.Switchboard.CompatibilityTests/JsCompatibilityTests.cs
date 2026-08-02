using Xunit;

namespace Keryhe.Switchboard.CompatibilityTests;

/// <summary>
/// Slice 2 gate (plan §4, finding 9): the JS probe (ClientProbeContract.md) against every valid
/// cell, for both <c>@microsoft/signalr</c> **8.0.17 and 10.0.0** — finding 1 is the entire
/// argument for varying the SDK version, not just the SDK; a matrix that only ever ran 10.0.0
/// would have the same "never seen red" blind spot the .NET rows had before Slice 1. Also absorbs
/// <c>js-redirect-check</c> (retired — its negotiate-redirect assertion is a strict subset of this
/// scenario, and unlike the standalone script this runs automatically, per finding 9).
/// </summary>
public class JsCompatibilityTests : IAsyncLifetime, IClassFixture<JsClientFixture>
{
    private readonly JsClientFixture _js;
    private SampleAppHost _host = null!;

    public JsCompatibilityTests(JsClientFixture js)
    {
        _js = js;
    }

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
        foreach (var version in new[] { "v8", "v10" })
        {
            yield return [version, "websockets", "json"];
            yield return [version, "websockets", "messagepack"];
            yield return [version, "sse", "json"];
            // (sse, messagepack) omitted — SSE is Text-only by design (03-protocol.md §1.5).
            yield return [version, "longpolling", "json"];
            yield return [version, "longpolling", "messagepack"];
        }
    }

    [Theory]
    [MemberData(nameof(ValidCells))]
    public async Task JsProbe_PassesEveryValidCell(string jsVersion, string transport, string protocol)
    {
        var probeDir = jsVersion == "v8" ? _js.V8ProbeDir : _js.V10ProbeDir;
        var result = await ProbeRunner.RunAsync(
            "node", $"\"{Path.Combine(probeDir, "probe.mjs")}\" {_host.Api.BaseUrl} {transport} {protocol}", TimeSpan.FromSeconds(45));

        Assert.True(result.Success,
            $"probe failed for ({jsVersion}, {transport}, {protocol}): {result.RawLine}\n{result.Output}\n--- api ---\n{_host.Api.RecentOutput}\n--- service ---\n{_host.Service.RecentOutput}");

        Assert.Equal(
            new[] { "connect", "receive_push", "join_group", "invoke", "receive_group", "disconnect" },
            result.Steps);
    }
}
