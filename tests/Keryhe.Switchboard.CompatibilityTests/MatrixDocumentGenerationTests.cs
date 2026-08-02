using Xunit;

namespace Keryhe.Switchboard.CompatibilityTests;

/// <summary>
/// Plan decision D36: runs the whole compatibility matrix — every SDK this project has a probe for
/// — against real, freshly-started out-of-process hosts, and writes the observed result to
/// docs/docs/11-compatibility-matrix.md. This intentionally re-runs the same cells
/// <see cref="CompatibilityMatrixTests"/>/<see cref="JsCompatibilityTests"/>/<see cref="JavaCompatibilityTests"/>
/// already gate individually — the alternative (aggregating their results after the fact) would
/// make the document's content depend on xunit's cross-class execution ordering, which isn't
/// guaranteed. This test's own assertions are what "a failing cell fails the build" means for the
/// document: an unexpected probe failure fails this test before <see cref="MatrixDocumentWriter"/>
/// is ever called, so a red cell can never end up looking like one of the three documented states.
/// </summary>
public class MatrixDocumentGenerationTests : IAsyncLifetime, IClassFixture<JsClientFixture>, IClassFixture<JavaClientContainerFixture>
{
    private static readonly string[] ExpectedSteps = ["connect", "receive_push", "join_group", "invoke", "receive_group", "disconnect"];
    private static readonly string DocumentPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "docs", "11-compatibility-matrix.md"));

    private readonly JsClientFixture _js;
    private readonly JavaClientContainerFixture _java;
    private SampleAppHost _host = null!;
    private SampleAppHost? _containerHost;

    public MatrixDocumentGenerationTests(JsClientFixture js, JavaClientContainerFixture java)
    {
        _js = js;
        _java = java;
    }

    public async Task InitializeAsync()
    {
        _host = await SampleAppHost.StartAsync();
        if (_java.IsAvailable)
        {
            _containerHost = await SampleAppHost.StartAsync(forContainerizedClient: true);
        }
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        if (_containerHost is not null)
        {
            await _containerHost.DisposeAsync();
        }
    }

    [Fact]
    public async Task GenerateMatrixDocument()
    {
        var rows = new List<MatrixRow>();

        await RunDotNet8CellsAsync(rows);
        await RunJsCellsAsync(rows);
        await RunJavaCellsAsync(rows);

        var knownIncompatibilities = new List<KnownIncompatibility>
        {
            new("Context.GetHttpContext() is always null",
                "There is no IHttpContextFeature on the Connector's synthetic connection — no HTTP request exists on the app server for a proxied client. Hub code that reads headers, cookies, or RemoteIpAddress from GetHttpContext() will NPE. Pinned by KnownIncompatibilityTests.GetHttpContext_IsAlwaysNull_OnASyntheticConnection. Pass claims that data as claims through the negotiate forwarding instead."),
            new("A custom IUserIdProvider silently diverges from the service's user index",
                "The service's send_to_user routing uses the userId captured at negotiate; Context.UserIdentifier is computed independently by IUserIdProvider on the app server. A custom provider that derives the id differently makes Clients.User(Context.UserIdentifier) target a user the service's index doesn't recognize — sends land nowhere, with no exception. Pinned by KnownIncompatibilityTests.CustomUserIdProvider_DivergesFromServicesUserIndex_SilentlyDropsSends. Apps with a custom provider must apply the same logic when supplying userId on the forwarded negotiate."),
            new("Stateful reconnect falls back to standard reconnect",
                "A client requesting .WithStatefulReconnect() still connects and works normally — it just doesn't get buffered-message replay on resume, which this project treats as a non-goal (message persistence/replay). Pinned by KnownIncompatibilityTests.StatefulReconnectRequest_FallsBackGracefully_ConnectionStillWorks."),
            new("Client results (Clients.Client(id).InvokeAsync<T>(...)) are not supported",
                "Hub code that calls it gets a Switchboard-specific NotSupportedException naming the limitation, not the framework's bare NotImplementedException. Correctly routing the completion back to the originating app server needs a new correlated-completion path across the cluster-wide server-connection assignment (plan decision D18) — out of scope for this phase. Pinned by ClientResultsTests.HubCallingClientResults_SurfacesASwitchboardSpecificError_NotABareNotImplementedException. See 04-design.md §14."),
            new("SSE is Text-only; SSE+MessagePack is refused, not silently broken",
                "Negotiate advertises ServerSentEvents with transferFormats: [\"Text\"] only, so a client cannot request MessagePack over SSE at all (03-protocol.md §1.5)."),
        };

        await MatrixDocumentWriter.WriteAsync(DocumentPath, rows, knownIncompatibilities, DateTimeOffset.UtcNow);

        Assert.True(File.Exists(DocumentPath));
    }

    private async Task RunDotNet8CellsAsync(List<MatrixRow> rows)
    {
        var probeDll = FindDotNet8ProbeAssembly();

        foreach (var (transport, protocol) in ValidTransportProtocolCells())
        {
            var result = await ProbeRunner.RunAsync(
                "dotnet", $"\"{probeDll}\" {_host.Api.BaseUrl} {transport} {protocol}", TimeSpan.FromSeconds(45));

            AssertFullScenario(result, ".NET 8", transport, protocol);
            rows.Add(new MatrixRow(".NET 8 (Microsoft.AspNetCore.SignalR.Client 8.0.29)", transport, protocol, MatrixStatus.Pass));
        }

        rows.Add(new MatrixRow(".NET 8 (Microsoft.AspNetCore.SignalR.Client 8.0.29)", "sse", "messagepack", MatrixStatus.NotApplicable, "SSE is Text-only by design"));
    }

    private async Task RunJsCellsAsync(List<MatrixRow> rows)
    {
        foreach (var version in new[] { "v8", "v10" })
        {
            var sdkLabel = version == "v8" ? "JavaScript (@microsoft/signalr 8.0.17)" : "JavaScript (@microsoft/signalr 10.0.0)";
            var probeDir = version == "v8" ? _js.V8ProbeDir : _js.V10ProbeDir;

            foreach (var (transport, protocol) in ValidTransportProtocolCells())
            {
                var result = await ProbeRunner.RunAsync(
                    "node", $"\"{Path.Combine(probeDir, "probe.mjs")}\" {_host.Api.BaseUrl} {transport} {protocol}", TimeSpan.FromSeconds(45));

                AssertFullScenario(result, sdkLabel, transport, protocol);
                rows.Add(new MatrixRow(sdkLabel, transport, protocol, MatrixStatus.Pass));
            }

            rows.Add(new MatrixRow(sdkLabel, "sse", "messagepack", MatrixStatus.NotApplicable, "SSE is Text-only by design"));
        }
    }

    private async Task RunJavaCellsAsync(List<MatrixRow> rows)
    {
        const string sdkLabel = "Java (com.microsoft.signalr 9.0.6)";

        if (!_java.IsAvailable)
        {
            foreach (var (transport, protocol) in AllTransportProtocolCells())
            {
                rows.Add(new MatrixRow(sdkLabel, transport, protocol, MatrixStatus.Untested, $"Docker unavailable: {_java.UnavailableReason}"));
            }

            return;
        }

        var passResult = await _java.RunProbeAsync(_containerHost!.Api.BaseUrl, "websockets", "json", TimeSpan.FromSeconds(90));
        AssertFullScenario(passResult, sdkLabel, "websockets", "json");
        rows.Add(new MatrixRow(sdkLabel, "websockets", "json", MatrixStatus.Pass));

        var longPollingResult = await _java.RunProbeAsync(_containerHost!.Api.BaseUrl, "longpolling", "json", TimeSpan.FromSeconds(90));
        Assert.False(longPollingResult.Success, "Java long polling was expected to still fail its known auth limitation — if this now passes, promote it to Pass and update 04-design.md/00-review-findings.md.");
        rows.Add(new MatrixRow(sdkLabel, "longpolling", "json", MatrixStatus.NotApplicable,
            "Known SDK limitation, verified: this client version's LongPollingTransport does not authenticate its establishing request"));

        rows.Add(new MatrixRow(sdkLabel, "sse", "json", MatrixStatus.NotApplicable, "SDK's TransportEnum has no SERVER_SENT_EVENTS member"));
        rows.Add(new MatrixRow(sdkLabel, "websockets", "messagepack", MatrixStatus.NotApplicable, "SDK ships no MessagePack hub protocol"));
        rows.Add(new MatrixRow(sdkLabel, "longpolling", "messagepack", MatrixStatus.NotApplicable, "SDK ships no MessagePack hub protocol"));
    }

    private static IEnumerable<(string Transport, string Protocol)> ValidTransportProtocolCells()
    {
        yield return ("websockets", "json");
        yield return ("websockets", "messagepack");
        yield return ("sse", "json");
        yield return ("longpolling", "json");
        yield return ("longpolling", "messagepack");
    }

    private static IEnumerable<(string Transport, string Protocol)> AllTransportProtocolCells()
    {
        foreach (var t in new[] { "websockets", "sse", "longpolling" })
        foreach (var p in new[] { "json", "messagepack" })
        {
            yield return (t, p);
        }
    }

    private static void AssertFullScenario(ProbeRunner.ProbeResult result, string sdk, string transport, string protocol)
    {
        Assert.True(result.Success, $"probe failed for ({sdk}, {transport}, {protocol}): {result.RawLine}\n{result.Output}");
        Assert.Equal(ExpectedSteps, result.Steps);
    }

    private static string FindDotNet8ProbeAssembly()
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
