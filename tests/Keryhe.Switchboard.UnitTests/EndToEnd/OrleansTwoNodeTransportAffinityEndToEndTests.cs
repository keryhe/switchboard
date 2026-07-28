using System.Text;
using System.Text.Json;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Orleans.Grains;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Phase 3 Slice 5 gate (plan decision D19): with requests deliberately round-robined across two
/// nodes and no session affinity anywhere, Long Polling and SSE both complete the full flow —
/// establishing GET on one node, later POST/GET(poll)/DELETE requests landing on the other and
/// getting transparently forwarded to the node that actually holds the transport. Every request in
/// these tests is hand-rolled (not a real <c>HubConnection</c>, which always talks to a single base
/// address) specifically so the establishing node and the later requests' node can be chosen
/// independently, exactly like <see cref="OrleansTwoNodeEndToEndTests"/> does for negotiate vs.
/// WebSocket connect.
///
/// An app server double is connected to <i>both</i> nodes in every test here, not just one — the
/// same reason <see cref="AppServerDoubleWaits"/> exists: cluster-wide server-connection assignment
/// (plan decision D18) means <c>OpenConnection</c> can land on either node's app server regardless
/// of which node the client itself is talking to, and each node's <c>ObserverHeartbeatService</c>
/// only subscribes to hubs it locally knows about — a hub with a live server connection on only one
/// node never reaches subscriber count 2.
/// </summary>
public class OrleansTwoNodeTransportAffinityEndToEndTests : IAsyncLifetime
{
    private RealKestrelServerFixture _nodeA = null!;
    private RealKestrelServerFixture _nodeB = null!;
    private const string HubName = "chatHub-slice5-affinity";

    public async Task InitializeAsync()
    {
        var siloPortA = RealKestrelServerFixture.GetFreeTcpPort();
        var gatewayPortA = RealKestrelServerFixture.GetFreeTcpPort();
        var siloPortB = RealKestrelServerFixture.GetFreeTcpPort();
        var gatewayPortB = RealKestrelServerFixture.GetFreeTcpPort();
        var clusterId = $"switchboard-test-{Guid.NewGuid():n}";

        var commonArgs = new[]
        {
            "--Switchboard:UseOrleansCluster", "true",
            "--Switchboard:OrleansClusterId", clusterId,
            "--Switchboard:OrleansServiceId", clusterId,
            "--Switchboard:ObserverHeartbeatInterval", "00:00:00.300",
            "--Switchboard:ClientKeepAliveInterval", "00:05:00",
            "--Switchboard:ShutdownTimeout", "00:00:02",
            // Long enough that the handful of cross-node HTTP hops in the general-flow tests below
            // never trip the reaper between polls even under parallel test-suite CPU contention,
            // short enough that the dedicated reaper test still completes well within its own wait
            // window (AppServerDoubleWaits' default 30s).
            "--Switchboard:DisconnectTimeout", "00:00:15",
            "--Switchboard:LongPollTimeout", "00:00:20",
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
        await _nodeA.DisposeAsync();
        await _nodeB.DisposeAsync();
    }

    [Fact]
    public async Task LongPolling_EstablishedOnB_ButPolledPostedAndDeleted_ThroughA_CompletesTheFullFlow()
    {
        var tokenService = _nodeA.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        await using var appServerA = await AppServerDouble.ConnectAsync(_nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB.ServerAddress, HubName, serverToken);

        await WaitForBothNodesSubscribedAsync();

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        using var http = new HttpClient();

        // Negotiate on A...
        var connectionToken = await NegotiateAsync(http, _nodeA, clientToken);

        // ...establish (the connection's owning node from here on) on B...
        var establishResp = await http.GetAsync(RequestUri(_nodeB, connectionToken, clientToken));
        Assert.True(establishResp.IsSuccessStatusCode);

        // ...and every subsequent request goes to A instead — legal only because A forwards each
        // one to B, which actually holds the transport.
        var handshake = "{\"protocol\":\"json\",\"version\":1}\x1e";
        var handshakeResp = await http.PostAsync(RequestUri(_nodeA, connectionToken, clientToken), new StringContent(handshake, Encoding.UTF8, "application/json"));
        Assert.True(handshakeResp.IsSuccessStatusCode);

        var openConnection = await AppServerDoubleWaits.WaitForOpenConnectionAsync("alice", appServerA, appServerB);
        var connectionId = openConnection.ConnectionId!;

        // First poll (forwarded) drains the buffered handshake-ack frame — not the payload under
        // test, just needs draining before a direct-push frame can be observed cleanly.
        var handshakeAckPoll = await http.GetAsync(RequestUri(_nodeA, connectionToken, clientToken));
        Assert.Equal(System.Net.HttpStatusCode.OK, handshakeAckPoll.StatusCode);

        // AddToGroup must travel over the server connection physically wired to the same node that
        // holds the target client's transport (appServerB, here — B is where this connection
        // established, per D19) — RoutingServerEnvelopeDispatcher's AddToGroup case updates whichever
        // node's own ILocalTransportRegistry receives the envelope, exactly matching how the real
        // Connector's Hub code runs on the app server instance that is actually driving this
        // connection. Same requirement OrleansTwoNodeGroupUserTargetedEndToEndTests documents.
        await appServerB.AddToGroupAsync(connectionId, "room-1");

        // AddToGroupAsync's own await only confirms the bytes were flushed to the app-server socket
        // — not that node B's own local index (what group fan-out actually reads, plan decisions
        // D14/D17) has processed it yet. Same race, same fix, as OrleansTwoNodeGroupUserTargetedEndToEndTests.
        var nodeBLocalTransportRegistry = _nodeB.Services.GetRequiredService<ILocalTransportRegistry>();
        await WaitUntilGroupMemberCountAsync(nodeBLocalTransportRegistry, 1);

        await appServerA.SendToConnectionAsync(connectionId, "ReceiveMessage", "System", "direct-forward-hello");
        var directPoll = await http.GetAsync(RequestUri(_nodeA, connectionToken, clientToken));
        Assert.Equal(System.Net.HttpStatusCode.OK, directPoll.StatusCode);
        var directBody = await directPoll.Content.ReadAsStringAsync();
        Assert.Contains("direct-forward-hello", directBody);

        await appServerA.SendToGroupAsync(HubName, "room-1", "ReceiveGroupMessage", null, "System", "group-forward-hello");
        var groupPoll = await http.GetAsync(RequestUri(_nodeA, connectionToken, clientToken));
        Assert.Equal(System.Net.HttpStatusCode.OK, groupPoll.StatusCode);
        var groupBody = await groupPoll.Content.ReadAsStringAsync();
        Assert.Contains("group-forward-hello", groupBody);

        // Close, forwarded through A too.
        var deleteResp = await http.DeleteAsync(RequestUri(_nodeA, connectionToken, clientToken));
        Assert.True(deleteResp.IsSuccessStatusCode);

        await AppServerDoubleWaits.WaitForCloseConnectionAsync(connectionId, appServerA, appServerB);
    }

    [Fact]
    public async Task LongPolling_AbandonedAfterAForwardedPoll_IsStillReapedWithinDisconnectTimeout()
    {
        var tokenService = _nodeA.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        await using var appServerA = await AppServerDouble.ConnectAsync(_nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB.ServerAddress, HubName, serverToken);

        await WaitForBothNodesSubscribedAsync();

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "bob", null, TimeSpan.FromMinutes(1));
        using var http = new HttpClient();

        var connectionToken = await NegotiateAsync(http, _nodeA, clientToken);

        var establishResp = await http.GetAsync(RequestUri(_nodeB, connectionToken, clientToken));
        Assert.True(establishResp.IsSuccessStatusCode);

        var handshake = "{\"protocol\":\"json\",\"version\":1}\x1e";
        var handshakeResp = await http.PostAsync(RequestUri(_nodeA, connectionToken, clientToken), new StringContent(handshake, Encoding.UTF8, "application/json"));
        Assert.True(handshakeResp.IsSuccessStatusCode);

        var openConnection = await AppServerDoubleWaits.WaitForOpenConnectionAsync("bob", appServerA, appServerB);

        // One forwarded poll to prove the tracker on B (the owning node) really is being updated by
        // requests arriving via A's forward hop, not just direct ones.
        var poll = await http.GetAsync(RequestUri(_nodeA, connectionToken, clientToken));
        Assert.Equal(System.Net.HttpStatusCode.OK, poll.StatusCode);

        // Then abandon entirely — never poll again, never DELETE. Node B's own
        // LongPollingReaperService (Phase 2) is the only thing that will ever notice, exactly as it
        // would for a request that was never forwarded at all.
        await AppServerDoubleWaits.WaitForCloseConnectionAsync(openConnection.ConnectionId!, appServerA, appServerB);
    }

    [Fact]
    public async Task ARequestCarryingTheMarkerHeader_ForAConnectionThisNodeDoesNotOwn_IsRejected_NotForwardedAgain()
    {
        var tokenService = _nodeA.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        await using var appServerA = await AppServerDouble.ConnectAsync(_nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB.ServerAddress, HubName, serverToken);

        await WaitForBothNodesSubscribedAsync();

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "carol", null, TimeSpan.FromMinutes(1));
        using var http = new HttpClient();

        var connectionToken = await NegotiateAsync(http, _nodeA, clientToken);

        // Established, and stays, on B.
        var establishResp = await http.GetAsync(RequestUri(_nodeB, connectionToken, clientToken));
        Assert.True(establishResp.IsSuccessStatusCode);

        var handshake = "{\"protocol\":\"json\",\"version\":1}\x1e";
        var handshakeResp = await http.PostAsync(RequestUri(_nodeB, connectionToken, clientToken), new StringContent(handshake, Encoding.UTF8, "application/json"));
        Assert.True(handshakeResp.IsSuccessStatusCode);

        await AppServerDoubleWaits.WaitForOpenConnectionAsync("carol", appServerA, appServerB);

        // A POST to A carrying the marker header up front — as if it had already been forwarded
        // once by some other node — must never be forwarded a second time, even though A's own
        // ownership lookup would otherwise say "forward to B". POST (unlike Long Polling's GET) has
        // no "maybe this is a brand-new connection" fallback, so a refused forward is a clean 404,
        // proving no second hop happened rather than merely not observing one.
        using var request = new HttpRequestMessage(HttpMethod.Post, RequestUri(_nodeA, connectionToken, clientToken))
        {
            Content = new StringContent("ignored", Encoding.UTF8, "application/octet-stream"),
        };
        request.Headers.Add(Keryhe.Switchboard.Server.ClientConnections.ClientConnectionForwarder.ForwardedHeaderName, "1");

        var response = await http.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);

        // The real connection on B is unaffected — a direct poll against B still works.
        var directPoll = await http.GetAsync(RequestUri(_nodeB, connectionToken, clientToken));
        Assert.True(directPoll.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Sse_EstablishedOnB_ButHandshakenAndSentThroughA_DeliversPushesOnTheOpenStream()
    {
        var tokenService = _nodeA.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        await using var appServerA = await AppServerDouble.ConnectAsync(_nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB.ServerAddress, HubName, serverToken);

        await WaitForBothNodesSubscribedAsync();

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "dana", null, TimeSpan.FromMinutes(1));
        using var http = new HttpClient();

        var connectionToken = await NegotiateAsync(http, _nodeA, clientToken);

        // The establishing GET opens the long-lived stream on B and stays there for the rest of
        // this test — SSE never re-establishes or polls, unlike Long Polling. The Accept header is
        // what routes this to SseClientEndpoint rather than LongPollingClientEndpoint
        // (ClientEndpoints.AcceptsEventStream) — a real client always sends it for this transport.
        using var establishRequest = new HttpRequestMessage(HttpMethod.Get, RequestUri(_nodeB, connectionToken, clientToken));
        establishRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var streamResponse = await http.SendAsync(establishRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(streamResponse.IsSuccessStatusCode);
        await using var stream = await streamResponse.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // Handshake and every subsequent send go through A instead, forwarded to B — the node
        // actually holding the SSE transport (plan decision D19).
        var handshake = "{\"protocol\":\"json\",\"version\":1}\x1e";
        var handshakeResp = await http.PostAsync(RequestUri(_nodeA, connectionToken, clientToken), new StringContent(handshake, Encoding.UTF8, "application/json"));
        Assert.True(handshakeResp.IsSuccessStatusCode);

        // First SSE frame on the stream is the handshake ack — drain it before looking for the
        // direct push under test.
        await ReadNextSseFrameAsync(reader);

        var openConnection = await AppServerDoubleWaits.WaitForOpenConnectionAsync("dana", appServerA, appServerB);
        var connectionId = openConnection.ConnectionId!;

        await appServerA.SendToConnectionAsync(connectionId, "ReceiveMessage", "System", "sse-forward-hello");
        var directFrame = await ReadNextSseFrameAsync(reader);
        Assert.Contains("sse-forward-hello", directFrame);

        // Close, forwarded through A too — SSE's DELETE is transport-agnostic, same code path as
        // Long Polling's.
        var deleteResp = await http.DeleteAsync(RequestUri(_nodeA, connectionToken, clientToken));
        Assert.True(deleteResp.IsSuccessStatusCode);

        await AppServerDoubleWaits.WaitForCloseConnectionAsync(connectionId, appServerA, appServerB);
    }

    /// <summary>Reads lines until a blank line closes one <c>"data: "</c>-prefixed SSE frame
    /// (03-protocol.md §1.5) and returns its content.</summary>
    private static async Task<string> ReadNextSseFrameAsync(StreamReader reader)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = new StringBuilder();

        while (true)
        {
            var lineTask = reader.ReadLineAsync();
            var line = await lineTask.WaitAsync(timeoutCts.Token);
            if (line is null)
            {
                throw new InvalidOperationException("SSE stream ended before a frame was received.");
            }

            if (line.Length == 0)
            {
                return builder.ToString();
            }

            builder.Append(line.StartsWith("data: ", StringComparison.Ordinal) ? line["data: ".Length..] : line);
        }
    }

    private static async Task WaitUntilGroupMemberCountAsync(ILocalTransportRegistry registry, int expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (registry.GetGroupMembers(HubName, "room-1").Count() != expected)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, cts.Token);
        }
    }

    private async Task WaitForBothNodesSubscribedAsync()
    {
        var grainFactory = _nodeA.Services.GetRequiredService<IGrainFactory>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await grainFactory.GetGrain<IHubGrain>(HubName).GetSubscriberCountAsync() != 2)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, cts.Token);
        }
    }

    private static Uri RequestUri(RealKestrelServerFixture node, string connectionToken, string clientToken) =>
        new(node.ServerAddress, $"/{HubName}?id={connectionToken}&access_token={clientToken}");

    private static async Task<string> NegotiateAsync(HttpClient http, RealKestrelServerFixture node, string clientToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(node.ServerAddress, $"/{HubName}/negotiate"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", clientToken);

        var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("connectionToken").GetString()!;
    }
}
