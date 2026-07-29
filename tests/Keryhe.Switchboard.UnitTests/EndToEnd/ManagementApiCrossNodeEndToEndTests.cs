using System.Net.Http.Headers;
using System.Net.Http.Json;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Orleans.Grains;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Phase 4 Slice 1's mandatory gate (plans/phase-4-management-and-observability.md §4, plan
/// decision D23): adding a connection owned by a <em>different</em> node to a group, through the
/// management API, on a real two-silo cluster — the exact scenario the Phase 3 Slice 7 bug lived
/// in (<c>RoutingServerEnvelopeDispatcher</c> used to mutate this node's
/// <see cref="ILocalTransportRegistry"/> unconditionally, silently never taking effect for a
/// connection that lived elsewhere). <see cref="IGroupMembershipService"/> is the single
/// implementation both the envelope dispatcher and the management endpoint call, so this test is
/// really asserting the extraction didn't drop the owner-node forward — a hand-rolled second
/// implementation in the management endpoint would very plausibly have reproduced the bug.
/// </summary>
public class ManagementApiCrossNodeEndToEndTests : IAsyncLifetime
{
    private RealKestrelServerFixture _nodeA = null!;
    private RealKestrelServerFixture _nodeB = null!;
    private const string HubName = "chatHub-management-2node-e2e";
    private const string ManagementSigningKey = "dev-only-management-signing-key-change-me-32+";

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
            "--Switchboard:EnableManagementApi", "true",
            "--Switchboard:ManagementSigningKey", ManagementSigningKey,
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
    public async Task AddToGroup_ThroughManagementApiOnNodeA_ForConnectionOwnedByNodeB_DeliversGroupMessage()
    {
        var tokenService = _nodeA.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        await using var appServerA = await AppServerDouble.ConnectAsync(_nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB.ServerAddress, HubName, serverToken);

        await WaitForBothNodesSubscribedAsync();

        // The client connects through node B, so the connection is owned by node B — but the
        // management call below is made against node A, deliberately the other node.
        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "erin", null, TimeSpan.FromMinutes(1));
        await using var client = BuildClient(_nodeB, clientToken);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<string, string>("ReceiveMessage", (_, text) => received.TrySetResult(text));
        await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var openOnB = await AppServerDoubleWaits.WaitForOpenConnectionAsync("erin", appServerA, appServerB);
        var connectionId = openOnB.ConnectionId!;

        using var http = new HttpClient { BaseAddress = _nodeA.ServerAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);
        var response = await http.PutAsync($"/api/v1/hubs/{HubName}/groups/cross-node-group/connections/{connectionId}", content: null);
        Assert.True(response.IsSuccessStatusCode, $"Expected success, got {response.StatusCode}");

        // The PUT's own response only confirms the grain-side AddToGroupAsync completed — proves
        // node B's local index (the one delivery actually reads) picked it up, same race rationale
        // as OrleansTwoNodeGroupUserTargetedEndToEndTests.
        var nodeBLocalTransportRegistry = _nodeB.Services.GetRequiredService<ILocalTransportRegistry>();
        await WaitUntilAsync(
            () => Task.FromResult(nodeBLocalTransportRegistry.GetGroupMembers(HubName, "cross-node-group").Count()),
            count => count == 1,
            TimeSpan.FromSeconds(10));

        // Whichever app server the group send happens to be issued from doesn't matter — send it
        // from node A, the same node the management call targeted, for the clearest reproduction
        // of "a caller on this node named a connection that lives on the other one".
        await appServerA.SendToGroupAsync(HubName, "cross-node-group", "ReceiveMessage", null, "System", "cross-node-hello");

        Assert.Equal("cross-node-hello", await received.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// Phase 4 Slice 2's cross-node gate (plan decision D22): a management broadcast issued
    /// against node A must reach a client connected to node B — the send endpoints hand off to
    /// <c>IMessageRouter.BroadcastAsync</c>, whose local-index-then-one-backplane-publish shape
    /// (plan decision D14) was already proven node-agnostic by Phase 3; this pins the management
    /// API's own call site into that same path.
    /// </summary>
    [Fact]
    public async Task Broadcast_IssuedAgainstNodeA_ReachesClientConnectedToNodeB()
    {
        var tokenService = _nodeA.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        await using var appServerA = await AppServerDouble.ConnectAsync(_nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB.ServerAddress, HubName, serverToken);

        await WaitForBothNodesSubscribedAsync();

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "heidi", null, TimeSpan.FromMinutes(1));
        await using var client = BuildClient(_nodeB, clientToken);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<string, string>("ReceiveMessage", (_, text) => received.TrySetResult(text));
        await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await AppServerDoubleWaits.WaitForOpenConnectionAsync("heidi", appServerA, appServerB);

        using var http = new HttpClient { BaseAddress = _nodeA.ServerAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);
        var response = await http.PostAsJsonAsync(
            $"/api/v1/hubs/{HubName}/send",
            new { target = "ReceiveMessage", arguments = new object[] { "System", "cross-node-broadcast" } });
        Assert.True(response.IsSuccessStatusCode, $"Expected success, got {response.StatusCode}");

        Assert.Equal("cross-node-broadcast", await received.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// Phase 4 Slice 3's mandatory gate (plan decision D27, finding 5): a hub known only to node
    /// B — no app server connection on node A at all, only a client physically connected through
    /// node B — must still appear in <c>GET /api/v1/health</c> and its connection must still appear
    /// in <c>GET /api/v1/hubs/{hub}/connections</c> when both are requested from node A. Neither
    /// <c>IHubRegistry.GetAllHubs()</c> nor <c>ILocalTransportRegistry.GetKnownHubNames()</c> on
    /// node A could ever answer this on their own — both are node-local, and node A has never seen
    /// this hub at all. Only the <see cref="INodeRegistryGrain"/> directory
    /// <c>NodeRegistryPublisherService</c> republishes on a cadence makes it visible.
    /// </summary>
    [Fact]
    public async Task Health_And_Connections_RequestedFromNodeA_SeeHubAndConnectionKnownOnlyToNodeB()
    {
        var tokenService = _nodeA.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        // Deliberately no app server on node A at all — node A's own IHubRegistry.GetAllHubs()
        // and ILocalTransportRegistry.GetKnownHubNames() are both empty for this hub, so its only
        // route to visibility is the cluster-wide INodeRegistryGrain directory.
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB.ServerAddress, HubName, serverToken);

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "ivan", null, TimeSpan.FromMinutes(1));
        await using var client = BuildClient(_nodeB, clientToken);
        await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var openOnB = await AppServerDoubleWaits.WaitForOpenConnectionAsync("ivan", appServerB);
        var connectionId = openOnB.ConnectionId!;

        using var http = new HttpClient { BaseAddress = _nodeA.ServerAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);

        // NodeRegistryPublisherService republishes on ObserverHeartbeatInterval (300ms here) — the
        // directory is eventually, not immediately, consistent, so poll rather than assert on the
        // first request.
        await WaitUntilAsync(
            async () =>
            {
                var health = await http.GetFromJsonAsync<Keryhe.Switchboard.Management.Models.ManagementHealthResponse>("/api/v1/health");
                return health!.ServerConnections.TryGetValue(HubName, out var count) && count == 1 && health.ClientConnections == 1;
            },
            found => found,
            TimeSpan.FromSeconds(10));

        var connectionsResponse = await http.GetAsync($"/api/v1/hubs/{HubName}/connections");
        Assert.True(connectionsResponse.IsSuccessStatusCode);
        var connectionsBody = await connectionsResponse.Content.ReadAsStringAsync();
        Assert.Contains(connectionId, connectionsBody);
    }

    private async Task WaitForBothNodesSubscribedAsync()
    {
        var grainFactory = _nodeA.Services.GetRequiredService<IGrainFactory>();
        await WaitUntilAsync(
            () => grainFactory.GetGrain<IHubGrain>(HubName).GetSubscriberCountAsync(),
            count => count == 2,
            TimeSpan.FromSeconds(10));
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

    private static async Task WaitUntilAsync<T>(Func<Task<T>> poll, Func<T, bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!predicate(await poll()))
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, cts.Token);
        }
    }
}
