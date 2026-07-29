using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Phase 4's milestone (plans/phase-4-management-and-observability.md §9, Definition of Done #4):
/// "metrics from a running two-node cluster are received by a real OTLP collector and visible on a
/// dashboard; a <c>curl</c> broadcast with a CLI-generated management token reaches a live client."
/// The assertion is on the collector's own received data (finding 3 — a misconfigured OTLP endpoint
/// fails completely silently, so "the exporter is configured" proves nothing on its own), and the
/// broadcast half uses the exact same code path <c>token generate</c> and a plain <c>curl</c>
/// process would, not an in-process shortcut. If Docker isn't available in this environment, the
/// test no-ops with an explanatory message rather than failing the whole suite, matching
/// <see cref="OrleansAdoNetTwoNodeEndToEndTests"/>'s precedent.
/// </summary>
public class Phase4MilestoneEndToEndTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly OtlpCollectorContainerFixture _collector = new();
    private RealKestrelServerFixture? _nodeA;
    private RealKestrelServerFixture? _nodeB;
    private const string HubName = "chatHub-phase4-milestone";
    private const string ManagementSigningKey = "dev-only-management-signing-key-change-me-32+";

    public Phase4MilestoneEndToEndTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await _collector.InitializeAsync();
        if (!_collector.IsAvailable)
        {
            return;
        }

        // Exports on a 1s interval rather than the OTLP SDK's 60s default — an environment
        // variable, not a new production configuration knob, read by the SDK's own
        // PeriodicExportingMetricReader when the code (Program.cs) configures no explicit
        // override. Only this test enables OtlpEndpoint at all, so no other concurrently-running
        // test is affected by a faster export cadence.
        Environment.SetEnvironmentVariable("OTEL_METRIC_EXPORT_INTERVAL", "1000");

        var siloPortA = RealKestrelServerFixture.GetFreeTcpPort();
        var gatewayPortA = RealKestrelServerFixture.GetFreeTcpPort();
        var siloPortB = RealKestrelServerFixture.GetFreeTcpPort();
        var gatewayPortB = RealKestrelServerFixture.GetFreeTcpPort();
        var clusterId = $"switchboard-phase4-milestone-{Guid.NewGuid():n}";

        var commonArgs = new[]
        {
            "--Switchboard:UseOrleansCluster", "true",
            "--Switchboard:OrleansClusterId", clusterId,
            "--Switchboard:OrleansServiceId", clusterId,
            "--Switchboard:ClientKeepAliveInterval", "00:05:00",
            "--Switchboard:ShutdownTimeout", "00:00:02",
            "--Switchboard:EnableManagementApi", "true",
            "--Switchboard:ManagementSigningKey", ManagementSigningKey,
            "--Switchboard:OtlpEndpoint", _collector.OtlpEndpoint,
        };

        _nodeA = new RealKestrelServerFixture();
        await _nodeA.StartAsync(commonArgs.Concat(
        [
            "--Switchboard:OrleansSiloPort", siloPortA.ToString(),
            "--Switchboard:OrleansGatewayPort", gatewayPortA.ToString(),
        ]).ToArray());

        _nodeB = new RealKestrelServerFixture();
        await _nodeB.StartAsync(commonArgs.Concat(
        [
            "--Switchboard:OrleansSiloPort", siloPortB.ToString(),
            "--Switchboard:OrleansGatewayPort", gatewayPortB.ToString(),
            "--Switchboard:OrleansPrimarySiloEndpoint", $"127.0.0.1:{siloPortA}",
        ]).ToArray());
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("OTEL_METRIC_EXPORT_INTERVAL", null);

        if (_nodeA is not null)
        {
            await _nodeA.DisposeAsync();
        }

        if (_nodeB is not null)
        {
            await _nodeB.DisposeAsync();
        }

        await _collector.DisposeAsync();
    }

    [Fact]
    public async Task MetricsFromTwoNodeCluster_ReachRealOtlpCollector_AndCurlBroadcastReachesLiveClient()
    {
        if (!_collector.IsAvailable)
        {
            _output.WriteLine($"Skipping Phase 4 milestone test — no Docker/OTLP collector available ({_collector.UnavailableReason}). Untested in this environment.");
            return;
        }

        var tokenService = _nodeA!.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerA = await AppServerDouble.ConnectAsync(_nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB!.ServerAddress, HubName, serverToken);

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "milestone-user", null, TimeSpan.FromMinutes(1));
        await using var client = BuildClient(_nodeA, clientToken);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<string, string>("ReceiveMessage", (_, text) => received.TrySetResult(text));
        await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await AppServerDoubleWaits.WaitForOpenConnectionAsync("milestone-user", appServerA, appServerB);

        // ---- Half 1: metrics from this real two-node cluster reach the real collector ----
        // client_connections.active is a gauge, re-exported every interval regardless of activity,
        // so it's the one metric guaranteed to show up from just the connect above.
        var sawClientConnections = await _collector.WaitForLogsToContainAsync("client_connections.active", TimeSpan.FromSeconds(90));
        Assert.True(sawClientConnections, "Collector never logged signalr.client_connections.active — metrics did not reach it.");

        // A real client-originated message, routed inbound (client → app server) — the
        // messages.routed{direction=inbound} instrument (RoutingServerEnvelopeDispatcher only
        // records the outbound half for app-server-originated envelopes; the management API's own
        // broadcast below bypasses app servers entirely, per D22, and was never meant to touch it).
        await client.SendAsync("Ping");
        await appServerA.ReceiveEnvelopeAsync(Keryhe.Switchboard.Protocol.ServerEnvelopeType.ClientMessage, TimeSpan.FromSeconds(10));

        var sawMessagesRouted = await _collector.WaitForLogsToContainAsync("messages.routed", TimeSpan.FromSeconds(15));
        Assert.True(sawMessagesRouted, "Collector never logged signalr.messages.routed.");

        // ---- Half 2: a CLI-generated management token, used from a plain curl process, reaches a live client ----
        // broadcast.fan_out_size only has something to report once this broadcast actually
        // happens, so it's checked after it, not before.
        var managementToken = GenerateManagementTokenViaCli();

        var curlResult = await RunCurlBroadcastAsync(_nodeA.ServerAddress, HubName, managementToken);
        Assert.Equal(0, curlResult.ExitCode);
        Assert.Contains("202", curlResult.StdOut);

        var receivedText = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("Server is restarting in 5 minutes", receivedText);

        var sawFanOutSize = await _collector.WaitForLogsToContainAsync("broadcast.fan_out_size", TimeSpan.FromSeconds(15));
        Assert.True(sawFanOutSize, "Collector never logged signalr.broadcast.fan_out_size for the curl-issued broadcast.");
    }

    /// <summary>Invokes the exact same <c>TokenCommand.Run</c> code path <c>dotnet ... -- token
    /// generate</c> would from a shell — in-process rather than a second spawned <c>dotnet</c>
    /// process purely to save the milestone test a slow cold-start, but the CLI's own code, not a
    /// shortcut around it.</summary>
    private static string GenerateManagementTokenViaCli()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            var exitCode = Keryhe.Switchboard.Server.Cli.TokenCommand.Run(
            [
                "token", "generate",
                "--role", "management",
                "--subject", "on-call-engineer",
                "--ttl", "1h",
                "--key", ManagementSigningKey,
            ]);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return writer.ToString().Trim();
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCurlBroadcastAsync(Uri serverAddress, string hubName, string managementToken)
    {
        var url = new Uri(serverAddress, $"/api/v1/hubs/{hubName}/send");
        var body = """{"target":"ReceiveMessage","arguments":["System","Server is restarting in 5 minutes"]}""";

        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("curl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("-s");
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("/dev/null");
        process.StartInfo.ArgumentList.Add("-w");
        process.StartInfo.ArgumentList.Add("%{http_code}");
        process.StartInfo.ArgumentList.Add("-X");
        process.StartInfo.ArgumentList.Add("POST");
        process.StartInfo.ArgumentList.Add(url.ToString());
        process.StartInfo.ArgumentList.Add("-H");
        process.StartInfo.ArgumentList.Add($"Authorization: Bearer {managementToken}");
        process.StartInfo.ArgumentList.Add("-H");
        process.StartInfo.ArgumentList.Add("Content-Type: application/json");
        process.StartInfo.ArgumentList.Add("-d");
        process.StartInfo.ArgumentList.Add(body);

        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, await stdOutTask, await stdErrTask);
    }

    private static HubConnection BuildClient(RealKestrelServerFixture node, string clientToken)
    {
        var url = new Uri(node.ServerAddress, $"/{HubName}");
        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(clientToken);
                options.Transports = HttpTransportType.WebSockets;
            })
            .Build();
    }
}
