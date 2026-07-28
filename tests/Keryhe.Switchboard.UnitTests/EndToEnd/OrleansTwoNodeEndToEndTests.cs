using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Orleans.Grains;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Phase 3 Slice 2 gate: two real silos in one test process (<c>UseLocalhostClustering(siloPort,
/// gatewayPort, primarySiloEndpoint)</c> — verified working in this exact configuration during
/// planning), each its own <see cref="RealKestrelServerFixture"/>. Every test gets a brand-new pair
/// of nodes (<see cref="IAsyncLifetime"/> runs per test method), so there is no shared-silo state to
/// namespace around, unlike the Slice 1 conformance suites.
/// </summary>
public class OrleansTwoNodeEndToEndTests : IAsyncLifetime
{
    private RealKestrelServerFixture _nodeA = null!;
    private RealKestrelServerFixture _nodeB = null!;
    private const string HubName = "chatHub-2node-e2e";

    public async Task InitializeAsync()
    {
        var siloPortA = RealKestrelServerFixture.GetFreeTcpPort();
        var gatewayPortA = RealKestrelServerFixture.GetFreeTcpPort();
        var siloPortB = RealKestrelServerFixture.GetFreeTcpPort();
        var gatewayPortB = RealKestrelServerFixture.GetFreeTcpPort();

        // A fresh cluster/service id per test, not the shared default — every prior two-node test
        // in this class kills a silo mid-test, and reusing the same (clusterId, serviceId) pair
        // let a torn-down cluster's identity bleed into the next test's brand-new one despite the
        // ports being entirely different, which is a real interference source worth isolating
        // against rather than assuming away.
        var clusterId = $"switchboard-test-{Guid.NewGuid():n}";

        // ClientKeepAliveInterval's real 15s default is short enough to fire mid-test once three
        // multi-host tests are stacked sequentially in one class run — found the hard way via a raw
        // client receiving {"type":6} (a keep-alive Ping) instead of the broadcast it was waiting
        // for. Pushed out here rather than added to every raw receive's skip-and-retry loop alone,
        // the same way ClientKeepAliveEndToEndTests overrides the interval in the other direction
        // to test it specifically.
        //
        // ShutdownTimeout: verified during this slice's testing — when this cluster's only other
        // silo is also gone (a test disposing both nodes, or one node killed outright while the
        // other is still up), graceful silo shutdown can block for the full default host shutdown
        // timeout (30s) rather than detecting the peer's absence quickly. Shortened here so tearing
        // a node down in a test costs milliseconds, not thirty seconds each.
        var commonArgs = new[]
        {
            "--Switchboard:UseOrleansCluster", "true",
            "--Switchboard:OrleansClusterId", clusterId,
            "--Switchboard:OrleansServiceId", clusterId,
            "--Switchboard:ObserverHeartbeatInterval", "00:00:00.300",
            "--Switchboard:ClientKeepAliveInterval", "00:05:00",
            "--Switchboard:ShutdownTimeout", "00:00:02",
        };

        _nodeA = new RealKestrelServerFixture();
        await _nodeA.StartAsync(commonArgs.Concat(
        [
            "--Switchboard:OrleansSiloPort", siloPortA.ToString(),
            "--Switchboard:OrleansGatewayPort", gatewayPortA.ToString(),
        ]).ToArray());

        // Joins node A's cluster (primarySiloEndpoint) rather than starting its own — this is what
        // makes UseLocalhostClustering() form one logical cluster across two processes' worth of
        // silos hosted in one test process.
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
        await _nodeA.DisposeAsync();
        await _nodeB.DisposeAsync();
    }

    [Fact]
    public async Task Broadcast_FromAppServerOnNodeA_ReachesClient_NegotiatedOnA_ButConnectedThroughNodeB()
    {
        var tokenService = _nodeA.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        // Both nodes need their own local app-server connection: negotiate step 2's
        // HasActiveServerConnection check and the transport-upgrade server-connection selector are
        // both still node-local IHubRegistry lookups (plan decision D18 is Slice 4).
        await using var appServerA = await AppServerDouble.ConnectAsync(_nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB.ServerAddress, HubName, serverToken);

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));

        // ObserverHeartbeatService's very first subscribe pass runs at host startup, before either
        // app server has connected — so IHubRegistry.GetAllHubs() is empty and it subscribes
        // nothing on that pass. The actual subscription only happens on the *next* heartbeat tick
        // (300ms). Waited for *before* connecting, not just before the broadcast below: as of
        // Phase 3 Slice 4 (plan decision D18), server-connection assignment is cluster-wide, so the
        // connect flow itself — not just the broadcast — can need the cross-node observer path
        // (OpenConnection routed via IHubObserver.OnServerEnvelope when the least-loaded pick isn't
        // local to node B). Without this wait, that race silently drops OpenConnection exactly the
        // way an unawaited broadcast used to.
        var grainFactory = _nodeA.Services.GetRequiredService<IGrainFactory>();
        await WaitUntilAsync(() => grainFactory.GetGrain<IHubGrain>(HubName).GetSubscriberCountAsync(), count => count == 2, TimeSpan.FromSeconds(10));

        // Step 2 negotiate against node A mints connectionToken there...
        var connectionToken = await NegotiateAsync(_nodeA, HubName, clientToken);

        // ...and the transport upgrade presents it to node B instead — legal only because the
        // pending-connection grain (Phase 3 Slice 1) is shared cluster state, not node-local.
        await using var rawClient = await RawSignalRClient.ConnectAsync(_nodeB, HubName, connectionToken, clientToken);

        // Cluster-wide least-loaded assignment (plan decision D18) may pick either app server's
        // connection regardless of which node the client physically connected through — no longer
        // guaranteed local to node B the way it was under Slice 2's node-local assignment.
        var openConnections = await WaitForOpenConnectionsAsync(1, appServerA, appServerB);
        Assert.Equal("alice", openConnections[0].UserId);

        // A broadcast from the app server physically connected to node A must still reach the
        // client physically connected to node B — the whole point of ADR-003's observer backplane.
        await appServerA.BroadcastAsync(HubName, "ReceiveMessage", "System", "cross-node-hello");

        var frame = await rawClient.ReceiveTextFrameAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("cross-node-hello", frame);
    }

    [Fact]
    public async Task Broadcast_SurvivesNodeBDying_EvictsItAfterFirstFailure_AndDoesNotRetryOnTheNextBroadcast()
    {
        var tokenService = _nodeA.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerA = await AppServerDouble.ConnectAsync(_nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB.ServerAddress, HubName, serverToken);

        var tokenAlice = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        var tokenBob = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "bob", null, TimeSpan.FromMinutes(1));

        await using var clientOnA = BuildClient(_nodeA, tokenAlice);
        var receivedOnA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientOnA.On<string, string>("ReceiveMessage", (_, text) => receivedOnA.TrySetResult(text));

        var clientOnB = BuildClient(_nodeB, tokenBob);
        clientOnB.On<string, string>("ReceiveMessage", (_, _) => { });

        await clientOnA.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await WaitForOpenConnectionsAsync(1, appServerA, appServerB);

        await clientOnB.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await WaitForOpenConnectionsAsync(2, appServerA, appServerB);

        // Let both nodes' heartbeats subscribe before killing anything.
        var grainFactory = _nodeA.Services.GetRequiredService<IGrainFactory>();
        await WaitUntilAsync(() => grainFactory.GetGrain<IHubGrain>(HubName).GetSubscriberCountAsync(), count => count == 2, TimeSpan.FromSeconds(10));

        // Kill node B outright — no unsubscribe, exactly the failure mode finding 3 verified.
        await _nodeB.DisposeAsync();

        // Which silo the hub grain happened to be activated on is not under this test's control
        // (Orleans' default placement strategy, not something this project configures). If it was
        // B, killing B destroys the grain's in-memory observer dict along with it, and the very
        // next call transparently reactivates a fresh instance on the survivor with zero
        // subscribers rather than "2 minus the evicted one" — still correct, just a different path
        // to the same steady state. Either way, once node A's own heartbeat (300ms) has had a
        // chance to run post-death, the steady state must be exactly one subscriber (node A) and
        // node B gone for good — that steady state, not the exact transition, is what's asserted.
        await appServerA.BroadcastAsync(HubName, "ReceiveMessage", "System", "first-after-death");
        Assert.Equal("first-after-death", await receivedOnA.Task.WaitAsync(TimeSpan.FromSeconds(30)));

        await WaitUntilAsync(() => grainFactory.GetGrain<IHubGrain>(HubName).GetSubscriberCountAsync(), count => count == 1, TimeSpan.FromSeconds(10));

        // Stable at 1, not fluctuating — node B must never be able to reappear as a subscriber.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await grainFactory.GetGrain<IHubGrain>(HubName).GetSubscriberCountAsync());

        // A further broadcast still reaches the survivor promptly — no hang on an RPC to node B's
        // now-nonexistent socket, which is the concrete failure finding 3 is pinning against.
        var receivedSecond = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientOnA.On<string, string>("ReceiveMessage", (_, text) => receivedSecond.TrySetResult(text));
        await appServerA.BroadcastAsync(HubName, "ReceiveMessage", "System", "second-after-death");
        Assert.Equal("second-after-death", await receivedSecond.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task DeactivatingTheHubGrain_ResubscribesWithinOneHeartbeat_AndDeliveryResumes()
    {
        var tokenService = _nodeA.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerA = await AppServerDouble.ConnectAsync(_nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB.ServerAddress, HubName, serverToken);

        var tokenAlice = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        var tokenBob = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "bob", null, TimeSpan.FromMinutes(1));

        await using var clientOnA = BuildClient(_nodeA, tokenAlice);
        await using var clientOnB = BuildClient(_nodeB, tokenBob);

        var receivedOnB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientOnB.On<string, string>("ReceiveMessage", (_, text) => receivedOnB.TrySetResult(text));

        // Waited for *before* connecting, not just before the broadcast below: as of Phase 3 Slice
        // 4 (plan decision D18), server-connection assignment is cluster-wide, so a brand-new
        // connection's OpenConnection announcement can itself need the cross-node observer path —
        // racing the same "heartbeat hasn't subscribed yet" window an unawaited broadcast used to.
        var grainFactory = _nodeA.Services.GetRequiredService<IGrainFactory>();
        await WaitUntilAsync(() => grainFactory.GetGrain<IHubGrain>(HubName).GetSubscriberCountAsync(), count => count == 2, TimeSpan.FromSeconds(10));

        await clientOnA.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await WaitForOpenConnectionsAsync(1, appServerA, appServerB);

        await clientOnB.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await WaitForOpenConnectionsAsync(2, appServerA, appServerB);

        // Verified (Phase 3 plan finding 5): deactivation silently drops every subscription.
        await grainFactory.GetGrain<IHubGrain>(HubName).DeactivateForTestingAsync();
        await WaitUntilAsync(() => grainFactory.GetGrain<IHubGrain>(HubName).GetSubscriberCountAsync(), count => count == 0, TimeSpan.FromSeconds(2));

        // Both nodes' heartbeats (300ms interval, configured in InitializeAsync) re-subscribe
        // without any restart or manual intervention.
        await WaitUntilAsync(() => grainFactory.GetGrain<IHubGrain>(HubName).GetSubscriberCountAsync(), count => count == 2, TimeSpan.FromSeconds(3));

        await appServerA.BroadcastAsync(HubName, "ReceiveMessage", "System", "delivery-resumed");
        Assert.Equal("delivery-resumed", await receivedOnB.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// Cluster-wide server-connection assignment (plan decision D18, Phase 3 Slice 4) means the
    /// OpenConnection announcement for a brand-new client can land on whichever app server the
    /// least-loaded pick happened to choose — no longer guaranteed to be the one physically nearest
    /// the client. Waits until <paramref name="expected"/> of them have arrived across both app
    /// servers combined and returns them, since which double got which is not the test's business.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="AppServerDoubleWaits"/>, which polls the doubles' non-consuming
    /// <see cref="AppServerDouble.ReceivedEnvelopes"/> record — see there for why a consuming read
    /// on a named app server is no longer safe for a multi-node test.
    /// </remarks>
    private static Task<IReadOnlyList<ServerEnvelope>> WaitForOpenConnectionsAsync(int expected, params AppServerDouble[] appServers) =>
        AppServerDoubleWaits.WaitForEnvelopesAsync(
            envelope => envelope.Type == ServerEnvelopeType.OpenConnection,
            expected,
            appServers);

    private static async Task<string> NegotiateAsync(RealKestrelServerFixture node, string hub, string clientToken)
    {
        using var httpClient = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(node.ServerAddress, $"/{hub}/negotiate"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", clientToken);

        var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("connectionToken").GetString()!;
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

    /// <summary>Polls until <paramref name="predicate"/> holds, reporting the values actually seen
    /// if it never does — a bare "the operation was canceled" says nothing about whether the value
    /// was stuck, oscillating, or merely late, which is the first thing worth knowing here.</summary>
    private static async Task WaitUntilAsync<T>(Func<Task<T>> poll, Func<T, bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var observed = new List<T>();

        while (true)
        {
            var value = await poll();
            if (predicate(value))
            {
                return;
            }

            if (observed.Count == 0 || !EqualityComparer<T>.Default.Equals(observed[^1], value))
            {
                observed.Add(value);
            }

            if (cts.Token.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Condition not met within {timeout}. Distinct values observed, in order: [{string.Join(", ", observed)}].");
            }

            try
            {
                await Task.Delay(50, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Minimal hand-rolled SignalR JSON client — deliberately not <see cref="HubConnection"/>,
    /// which always negotiates and connects its transport against the same base URL. Proving
    /// negotiate and the transport upgrade can legally land on different nodes (Phase 3 Slice 1's
    /// pending-connection grain) requires driving the handshake by hand against a connectionToken
    /// minted by a different node's negotiate call.
    /// </summary>
    private sealed class RawSignalRClient(ClientWebSocket socket) : IAsyncDisposable
    {
        public static async Task<RawSignalRClient> ConnectAsync(RealKestrelServerFixture node, string hub, string connectionToken, string accessToken)
        {
            var socket = new ClientWebSocket();
            var httpUri = new Uri(node.ServerAddress, $"/{hub}?id={connectionToken}&access_token={accessToken}");
            var wsUri = new UriBuilder(httpUri) { Scheme = "ws" }.Uri;
            await socket.ConnectAsync(wsUri, CancellationToken.None);

            var client = new RawSignalRClient(socket);

            var writer = new ArrayBufferWriter<byte>();
            HandshakeProtocol.WriteRequestMessage(new HandshakeRequestMessage("json", 1), writer);
            await socket.SendAsync(writer.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

            using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var handshakeResponseBytes = await client.ReceiveRawFrameAsync(handshakeCts.Token);
            var sequence = new ReadOnlySequence<byte>(handshakeResponseBytes);
            if (!HandshakeProtocol.TryParseResponseMessage(ref sequence, out var response) || response?.Error is not null)
            {
                throw new InvalidOperationException($"Handshake failed: {response?.Error ?? "could not parse response"}.");
            }

            return client;
        }

        /// <summary>
        /// Skips the connection's own hub-level keep-alive <c>Ping</c> (<c>{"type":6}</c>) rather
        /// than assuming the first frame received is always the one being waited for — the same
        /// defensive pattern <c>AppServerDouble.ReceiveEnvelopeAsync</c> already uses on the
        /// server-facing side, needed here because this test can run long enough (stacked behind
        /// other tests in the class) for <c>ClientKeepAliveInterval</c>'s default 15s to elapse
        /// before the broadcast this is actually waiting for arrives.
        /// </summary>
        public async Task<string> ReceiveTextFrameAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                var text = Encoding.UTF8.GetString(await ReceiveRawFrameAsync(cts.Token));
                if (text.Contains("\"type\":6"))
                {
                    continue;
                }

                return text;
            }
        }

        private async Task<byte[]> ReceiveRawFrameAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];
            var result = await socket.ReceiveAsync(buffer, ct);
            return buffer[..result.Count];
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch
            {
                // best-effort
            }

            socket.Dispose();
        }
    }
}
