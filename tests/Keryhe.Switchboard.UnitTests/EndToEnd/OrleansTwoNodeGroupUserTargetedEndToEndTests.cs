using System.Linq;
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
/// Phase 3 Slice 3 gate: cross-node group, user, and targeted (<c>send_to_connection</c>) sends,
/// on the same two-silos-in-one-process topology <see cref="OrleansTwoNodeEndToEndTests"/> proved
/// for broadcast in Slice 2. Group/user membership is published by name (plan decision D17) —
/// each node resolves against its own local index, so a member registered through the app server
/// connection physically wired to node B must have its <c>AddToGroup</c> sent over that same
/// connection (mirrors how the real Connector is already node-affine per client connection). A
/// brand-new node pair is spun up per test (mirroring <see cref="OrleansTwoNodeEndToEndTests"/>,
/// not the single shared silo <see cref="OrleansTestSiloFixture"/> conformance tests use) — a
/// class-fixture-shared pair was tried and abandoned: it produced a genuine indefinite hang rather
/// than just added flakiness, on this suite's real Kestrel+Orleans hosts sharing state across
/// sequential test methods. Occasional slowness under this suite's own CPU load remains a real,
/// separately-confirmed pre-existing characteristic of running many real Kestrel+Orleans hosts
/// concurrently (also observed in unrelated, non-Orleans tests under the same load) — a bounded,
/// self-terminating timeout, not a hang.
/// </summary>
public class OrleansTwoNodeGroupUserTargetedEndToEndTests : IAsyncLifetime
{
    private RealKestrelServerFixture _nodeA = null!;
    private RealKestrelServerFixture _nodeB = null!;
    private const string HubName = "chatHub-2node-group-e2e";

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
    public async Task GroupSend_WithMembersOnBothNodes_ReachesBoth_ExcludesRemoteConnection_MixedProtocol()
    {
        var tokenService = _nodeA.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerA = await AppServerDouble.ConnectAsync(_nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB.ServerAddress, HubName, serverToken);

        await WaitForBothNodesSubscribedAsync();

        const string groupName = "group-both-nodes";

        // JSON client on node A.
        var tokenOnA = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        await using var clientOnA = BuildClient(_nodeA, tokenOnA, useMessagePack: false);
        var receivedOnA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientOnA.On<string, string>("ReceiveMessage", (_, text) => receivedOnA.TrySetResult(text));
        await clientOnA.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var openOnA = await AppServerDoubleWaits.WaitForOpenConnectionAsync("alice", appServerA, appServerB);

        // MessagePack client on node B — the group member that must receive a differently-encoded
        // payload than the JSON client above (plan decision D7), resolved across the backplane.
        var tokenOnB = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "bob", null, TimeSpan.FromMinutes(1));
        await using var clientOnB = BuildClient(_nodeB, tokenOnB, useMessagePack: true);
        var receivedOnB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientOnB.On<string, string>("ReceiveMessage", (_, text) => receivedOnB.TrySetResult(text));
        await clientOnB.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var openOnB = await AppServerDoubleWaits.WaitForOpenConnectionAsync("bob", appServerA, appServerB);

        // A second member on node B that joins the same group but must be excluded — proving
        // exclusion is applied by the *remote* node's own observer, not just locally by the sender.
        var tokenExcluded = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "carol", null, TimeSpan.FromMinutes(1));
        await using var clientExcludedOnB = BuildClient(_nodeB, tokenExcluded, useMessagePack: false);
        var excludedReceived = false;
        clientExcludedOnB.On<string, string>("ReceiveMessage", (_, _) => excludedReceived = true);
        await clientExcludedOnB.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var openExcluded = await AppServerDoubleWaits.WaitForOpenConnectionAsync("carol", appServerA, appServerB);

        // AddToGroup must travel over the server connection physically wired to the same node as
        // the target client — exactly how the real Connector is already node-affine per client.
        await appServerA.AddToGroupAsync(openOnA.ConnectionId!, groupName);
        await appServerB.AddToGroupAsync(openOnB.ConnectionId!, groupName);
        await appServerB.AddToGroupAsync(openExcluded.ConnectionId!, groupName);

        // AddToGroupAsync's own await only confirms the bytes were flushed to the app-server
        // socket — not that the server's envelope dispatch has actually run yet. Delivery reads
        // ILocalTransportRegistry, not the IGroupGrain (plan decision D14/D17) — polling the exact
        // registry delivery consults (rather than the grain, which lands slightly earlier in the
        // same dispatch continuation) removes that race instead of narrowing it.
        var nodeBLocalTransportRegistry = _nodeB.Services.GetRequiredService<ILocalTransportRegistry>();
        await WaitUntilAsync(
            () => Task.FromResult(nodeBLocalTransportRegistry.GetGroupMembers(HubName, groupName).Count()),
            count => count == 2,
            TimeSpan.FromSeconds(10));

        var nodeALocalTransportRegistry = _nodeA.Services.GetRequiredService<ILocalTransportRegistry>();
        await WaitUntilAsync(
            () => Task.FromResult(nodeALocalTransportRegistry.GetGroupMembers(HubName, groupName).Count()),
            count => count == 1,
            TimeSpan.FromSeconds(10));

        await appServerA.SendToGroupAsync(HubName, groupName, "ReceiveMessage", [openExcluded.ConnectionId!], "System", "group-hello");

        Assert.Equal("group-hello", await receivedOnA.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("group-hello", await receivedOnB.Task.WaitAsync(TimeSpan.FromSeconds(30)));

        // Give the excluded client a beat to (not) receive anything before asserting its absence.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(excludedReceived);
    }

    [Fact]
    public async Task UserSend_WithConnectionsOnBothNodes_ReachesBoth()
    {
        var tokenService = _nodeA.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerA = await AppServerDouble.ConnectAsync(_nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB.ServerAddress, HubName, serverToken);

        await WaitForBothNodesSubscribedAsync();

        const string userId = "dave";
        var tokenA = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, userId, null, TimeSpan.FromMinutes(1));
        var tokenB = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, userId, null, TimeSpan.FromMinutes(1));

        await using var clientOnA = BuildClient(_nodeA, tokenA, useMessagePack: false);
        var receivedOnA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientOnA.On<string, string>("ReceiveMessage", (_, text) => receivedOnA.TrySetResult(text));

        await using var clientOnB = BuildClient(_nodeB, tokenB, useMessagePack: false);
        var receivedOnB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientOnB.On<string, string>("ReceiveMessage", (_, text) => receivedOnB.TrySetResult(text));

        await clientOnA.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await AppServerDoubleWaits.WaitForOpenConnectionsAsync(userId, 1, appServerA, appServerB);

        await clientOnB.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await AppServerDoubleWaits.WaitForOpenConnectionsAsync(userId, 2, appServerA, appServerB);

        await appServerA.SendToUserAsync(HubName, userId, "ReceiveMessage", "System", "user-hello");

        Assert.Equal("user-hello", await receivedOnA.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("user-hello", await receivedOnB.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task TargetedSend_ToConnectionOnRemoteNode_ReachesIt()
    {
        var tokenService = _nodeA.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerA = await AppServerDouble.ConnectAsync(_nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB.ServerAddress, HubName, serverToken);

        await WaitForBothNodesSubscribedAsync();

        var tokenOnB = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "erin", null, TimeSpan.FromMinutes(1));
        await using var clientOnB = BuildClient(_nodeB, tokenOnB, useMessagePack: false);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientOnB.On<string, string>("ReceiveMessage", (_, text) => received.TrySetResult(text));

        await clientOnB.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var openOnB = await AppServerDoubleWaits.WaitForOpenConnectionAsync("erin", appServerA, appServerB);

        // Sent from node A's app server connection, for a connectionId that only exists on node B:
        // a local miss in RouteToConnectionAsync, resolved via IConnectionGrain.GetOwnerNodeAsync
        // and delivered through node B's IHubObserver.OnConnectionMessage (plan decision D17).
        await appServerA.SendToConnectionUsingProtocolAsync(openOnB.ConnectionId!, "json", "ReceiveMessage", "System", "direct-hello");

        Assert.Equal("direct-hello", await received.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task TargetedSend_ToDisconnectedConnection_DoesNotThrow_AndSubsequentRoutingStillWorks()
    {
        var tokenService = _nodeA.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerA = await AppServerDouble.ConnectAsync(_nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB.ServerAddress, HubName, serverToken);

        await WaitForBothNodesSubscribedAsync();

        var tokenOnB = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "frank", null, TimeSpan.FromMinutes(1));
        var clientOnB = BuildClient(_nodeB, tokenOnB, useMessagePack: false);
        await clientOnB.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var openOnB = await AppServerDoubleWaits.WaitForOpenConnectionAsync("frank", appServerA, appServerB);
        var connectionId = openOnB.ConnectionId!;

        // Disconnect and wait for the registry-side cleanup (IConnectionGrain.UnregisterAsync) to
        // actually clear the record — otherwise the send below could race a still-registered grain
        // and hit the positive path instead of the one being tested.
        await clientOnB.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await AppServerDoubleWaits.WaitForCloseConnectionAsync(connectionId, appServerA, appServerB);

        var grainFactory = _nodeA.Services.GetRequiredService<IGrainFactory>();
        await WaitUntilAsync(
            () => grainFactory.GetGrain<IConnectionGrain>(connectionId).GetOwnerNodeAsync(),
            location => location is null,
            TimeSpan.FromSeconds(10));

        // Sent from node A, for a connectionId that no longer exists anywhere: GetOwnerNodeAsync
        // returns null, logged once and dropped — must complete without throwing or hanging, not
        // retried, and must not leave the router in a bad state for anything that follows.
        await appServerA.SendToConnectionUsingProtocolAsync(connectionId, "json", "ReceiveMessage", "System", "into-the-void")
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Prove the server connection (and the router generally) is still healthy afterwards —
        // an ordinary broadcast from the same app server connection still completes normally.
        var tokenAlive = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "grace", null, TimeSpan.FromMinutes(1));
        await using var stillAliveClient = BuildClient(_nodeA, tokenAlive, useMessagePack: false);
        var stillReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        stillAliveClient.On<string, string>("ReceiveMessage", (_, text) => stillReceived.TrySetResult(text));
        await stillAliveClient.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await AppServerDoubleWaits.WaitForOpenConnectionAsync("grace", appServerA, appServerB);

        await appServerA.BroadcastAsync(HubName, "ReceiveMessage", "System", "still-alive");
        Assert.Equal("still-alive", await stillReceived.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// Waits until both nodes' <c>ObserverHeartbeatService</c> has actually subscribed to the hub
    /// grain — called before any client connects, which is a correctness requirement rather than
    /// tidiness as of Phase 3 Slice 4.
    /// </summary>
    /// <remarks>
    /// Server-connection assignment is cluster-wide now (plan decision D18), so a brand-new
    /// client's OpenConnection is announced over whichever node owns the assigned connection — and
    /// a targeted observer call to a node with no active subscription is logged and dropped, never
    /// queued or retried (<c>HubGrain.InvokeObserverAsync</c>). Connecting before both nodes have
    /// subscribed loses the announcement outright, and since assignment is sticky the client stays
    /// wired to that connection for its whole life rather than recovering on the next heartbeat.
    /// The first subscribe pass runs at host startup, before any app server has connected, so
    /// <c>IHubRegistry.GetAllHubs()</c> is empty and it subscribes nothing — the real subscription
    /// only lands on a later tick.
    /// </remarks>
    private async Task WaitForBothNodesSubscribedAsync()
    {
        var grainFactory = _nodeA.Services.GetRequiredService<IGrainFactory>();
        await WaitUntilAsync(
            () => grainFactory.GetGrain<IHubGrain>(HubName).GetSubscriberCountAsync(),
            count => count == 2,
            TimeSpan.FromSeconds(10));
    }

    private static HubConnection BuildClient(RealKestrelServerFixture node, string clientToken, bool useMessagePack)
    {
        var url = new Uri(node.ServerAddress, $"/{HubName}");
        var builder = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(clientToken);
                options.Transports = HttpTransportType.WebSockets;
            });

        if (useMessagePack)
        {
            builder.AddMessagePackProtocol();
        }

        return builder.Build();
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
