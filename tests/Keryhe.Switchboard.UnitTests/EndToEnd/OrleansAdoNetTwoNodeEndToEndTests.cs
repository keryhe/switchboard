using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Orleans.Grains;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Xunit;
using Xunit.Abstractions;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Phase 3 Slice 6 gate: "two nodes clustering through a real database (container or local
/// instance) complete Slice 2's and Slice 3's scenarios." Two real silos, each its own
/// <see cref="RealKestrelServerFixture"/>, both pointed at one throwaway
/// <c>postgres:16-alpine</c> container (<see cref="PostgresContainerFixture"/>) running the vendored
/// <c>Sql/PostgreSQL/*.sql</c> schema — <c>UseAdoNetClustering</c> +
/// <c>AddAdoNetGrainStorage</c> (plan decision D20) instead of every other Orleans test class's
/// in-memory <c>UseLocalhostClustering</c>/<c>AddMemoryGrainStorage</c>. If Docker isn't available
/// in the environment this runs in, the single test here no-ops with an explanatory message rather
/// than failing the whole suite — per the plan, "if no database is available ... that is called out
/// explicitly as untested rather than assumed working."
/// </summary>
public class OrleansAdoNetTwoNodeEndToEndTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly PostgresContainerFixture _database = new();
    private RealKestrelServerFixture? _nodeA;
    private RealKestrelServerFixture? _nodeB;
    private const string HubName = "chatHub-adonet-2node-e2e";

    public OrleansAdoNetTwoNodeEndToEndTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        if (!_database.IsAvailable)
        {
            return;
        }

        var siloPortA = RealKestrelServerFixture.GetFreeTcpPort();
        var gatewayPortA = RealKestrelServerFixture.GetFreeTcpPort();
        var siloPortB = RealKestrelServerFixture.GetFreeTcpPort();
        var gatewayPortB = RealKestrelServerFixture.GetFreeTcpPort();

        // A fresh cluster/service id per run so a re-run against the same long-lived database (a
        // local instance rather than this fixture's throwaway container) never sees another run's
        // stale membership rows.
        var clusterId = $"switchboard-adonet-test-{Guid.NewGuid():n}";

        var commonArgs = new[]
        {
            "--Switchboard:UseOrleansCluster", "true",
            "--Switchboard:OrleansClusterId", clusterId,
            "--Switchboard:OrleansServiceId", clusterId,
            "--Switchboard:OrleansAdoNetConnectionString", _database.ConnectionString,
            "--Switchboard:OrleansAdoNetInvariant", "Npgsql",
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

        // No primarySiloEndpoint wiring here, unlike the in-memory two-silo tests — the whole
        // point of ADO.NET clustering is that silos discover each other through the shared
        // membership table, not through a manually-supplied peer address.
        _nodeB = new RealKestrelServerFixture();
        await _nodeB.StartAsync(commonArgs.Concat(
        [
            "--Switchboard:OrleansSiloPort", siloPortB.ToString(),
            "--Switchboard:OrleansGatewayPort", gatewayPortB.ToString(),
        ]).ToArray());
    }

    public async Task DisposeAsync()
    {
        if (_nodeA is not null)
        {
            await _nodeA.DisposeAsync();
        }

        if (_nodeB is not null)
        {
            await _nodeB.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    [Fact]
    public async Task TwoNodes_OverAdoNetClustering_CompleteCrossNodeBroadcastAndGroupSendWithExclusion()
    {
        if (!_database.IsAvailable)
        {
            _output.WriteLine($"Skipping Slice 6 ADO.NET gate test — no real database available ({_database.UnavailableReason}). Untested in this environment, per plan §Slice 6.");
            return;
        }

        var tokenService = _nodeA!.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerA = await AppServerDouble.ConnectAsync(_nodeA.ServerAddress, HubName, serverToken);
        await using var appServerB = await AppServerDouble.ConnectAsync(_nodeB!.ServerAddress, HubName, serverToken);

        var grainFactory = _nodeA.Services.GetRequiredService<IGrainFactory>();
        await WaitUntilAsync(() => grainFactory.GetGrain<IHubGrain>(HubName).GetSubscriberCountAsync(), count => count == 2, TimeSpan.FromSeconds(15));

        // --- Slice 2 scenario: cross-node broadcast over the observer backplane. ---
        var tokenAlice = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        await using var clientOnA = BuildClient(_nodeA, tokenAlice);
        var broadcastReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientOnA.On<string, string>("ReceiveMessage", (_, text) => broadcastReceived.TrySetResult(text));
        await clientOnA.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var openOnA = await AppServerDoubleWaits.WaitForOpenConnectionAsync("alice", appServerA, appServerB);

        await appServerB.BroadcastAsync(HubName, "ReceiveMessage", "System", "adonet-cross-node-hello");
        Assert.Equal("adonet-cross-node-hello", await broadcastReceived.Task.WaitAsync(TimeSpan.FromSeconds(30)));

        // --- Slice 3 scenario: cross-node group send, with a remote-node member correctly excluded. ---
        var tokenBob = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "bob", null, TimeSpan.FromMinutes(1));
        await using var clientOnB = BuildClient(_nodeB, tokenBob);
        var groupReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientOnB.On<string, string>("ReceiveGroupMessage", (_, text) => groupReceived.TrySetResult(text));
        await clientOnB.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var openOnB = await AppServerDoubleWaits.WaitForOpenConnectionAsync("bob", appServerA, appServerB);

        var tokenCarol = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "carol", null, TimeSpan.FromMinutes(1));
        await using var clientExcludedOnB = BuildClient(_nodeB, tokenCarol);
        var excludedReceived = false;
        clientExcludedOnB.On<string, string>("ReceiveGroupMessage", (_, _) => excludedReceived = true);
        await clientExcludedOnB.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var openExcluded = await AppServerDoubleWaits.WaitForOpenConnectionAsync("carol", appServerA, appServerB);

        const string groupName = "room-adonet";
        await appServerA.AddToGroupAsync(openOnA.ConnectionId!, groupName);
        await appServerB.AddToGroupAsync(openOnB.ConnectionId!, groupName);
        await appServerB.AddToGroupAsync(openExcluded.ConnectionId!, groupName);

        var nodeALocalTransportRegistry = _nodeA.Services.GetRequiredService<ILocalTransportRegistry>();
        var nodeBLocalTransportRegistry = _nodeB.Services.GetRequiredService<ILocalTransportRegistry>();
        await WaitUntilAsync(() => Task.FromResult(nodeALocalTransportRegistry.GetGroupMembers(HubName, groupName).Count()), count => count == 1, TimeSpan.FromSeconds(15));
        await WaitUntilAsync(() => Task.FromResult(nodeBLocalTransportRegistry.GetGroupMembers(HubName, groupName).Count()), count => count == 2, TimeSpan.FromSeconds(15));

        await appServerA.SendToGroupAsync(HubName, groupName, "ReceiveGroupMessage", [openExcluded.ConnectionId!], "System", "adonet-group-hello");

        Assert.Equal("adonet-group-hello", await groupReceived.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(excludedReceived);
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
        while (true)
        {
            if (predicate(await poll()))
            {
                return;
            }

            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, cts.Token);
        }
    }
}
