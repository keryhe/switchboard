using System.Text;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Phase 3 Slice 1 gate: the entire existing end-to-end suite is meant to pass a second time with
/// <c>UseOrleansCluster = true</c> on one node — "same tests, different registry", the substitution
/// ADR-002 promised. Reparameterizing every existing end-to-end test class to run under both
/// registries is the ideal end state but a larger mechanical change than this slice's registry work
/// itself; this class instead re-runs the single-node milestone shape — connect, negotiate,
/// register, invoke a hub method, targeted reply, group join + group send, disconnect — end to end
/// against a real Kestrel host booted with <c>Switchboard:UseOrleansCluster=true</c>, which is
/// exactly the surface <see cref="Keryhe.Switchboard.Orleans.OrleansConnectionRegistry"/> and
/// <see cref="Keryhe.Switchboard.Orleans.OrleansPendingConnectionStore"/> sit behind. Full
/// per-class reparameterization (SSE/Long Polling/MessagePack/Pattern A/CORS under Orleans too) is
/// left as an explicit follow-up rather than silently declared covered by this slice.
/// </summary>
public class OrleansClusterSmokeEndToEndTests : IAsyncLifetime
{
    private RealKestrelServerFixture _factory = null!;
    private const string HubName = "chatHub-orleans-e2e";

    public async Task InitializeAsync()
    {
        _factory = new RealKestrelServerFixture();

        // Slice 6 makes OrleansAdoNetConnectionString mandatory for any "real" (non-test) cluster
        // boot — OrleansSiloPort is the documented test-only escape hatch (SwitchboardOptions'
        // own remarks), so a single-silo in-memory smoke test needs it set too, even with no
        // second silo to join.
        var siloPort = RealKestrelServerFixture.GetFreeTcpPort();
        var gatewayPort = RealKestrelServerFixture.GetFreeTcpPort();
        await _factory.StartAsync(
            "--Switchboard:UseOrleansCluster", "true",
            "--Switchboard:OrleansSiloPort", siloPort.ToString(),
            "--Switchboard:OrleansGatewayPort", gatewayPort.ToString());
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Connect_Invoke_TargetedReply_GroupSend_AndDisconnect_AllWorkAgainstTheOrleansRegistry()
    {
        var connectionRegistry = _factory.Services.GetRequiredService<IConnectionRegistry>();
        Assert.IsType<Keryhe.Switchboard.Orleans.OrleansConnectionRegistry>(connectionRegistry);

        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerDouble = await AppServerDouble.ConnectAsync(_factory.ServerAddress, HubName, serverToken);

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        await using var connection = BuildClient(clientToken);

        var received = new TaskCompletionSource<(string From, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string, string>("ReceiveMessage", (from, text) => received.SetResult((from, text)));

        var groupReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string, string>("ReceiveGroupMessage", (_, text) => groupReceived.SetResult(text));

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var openConnection = await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(10));
        Assert.Equal("alice", openConnection.UserId);
        var connectionId = openConnection.ConnectionId!;

        // The registry substitution's own round trip: a connection registered through the Orleans
        // grain path is readable back through the same IConnectionRegistry interface.
        var registeredState = await connectionRegistry.GetAsync(connectionId, CancellationToken.None);
        Assert.NotNull(registeredState);
        Assert.Equal("alice", registeredState!.UserId);
        Assert.Equal(HubName, registeredState.HubName);

        // client_message routing depends on RouteClientMessageAsync resolving the connection via
        // the (Orleans-backed) registry.
        await connection.SendAsync("Echo", "hello").WaitAsync(TimeSpan.FromSeconds(5));
        ServerEnvelope clientMessage;
        do
        {
            clientMessage = await appServerDouble.ReceiveAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ServerEnvelopeType.ClientMessage, clientMessage.Type);
        }
        while (!Encoding.UTF8.GetString(clientMessage.Payload!).Contains("Echo"));

        await appServerDouble.SendToConnectionAsync(connectionId, "ReceiveMessage", "System", "direct-hello");
        var (from, text) = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("System", from);
        Assert.Equal("direct-hello", text);

        // Group add/membership round-trips through the Orleans connection grain + group grain.
        await appServerDouble.AddToGroupAsync(connectionId, "room-1");
        await appServerDouble.SendToGroupAsync(HubName, "room-1", "ReceiveGroupMessage", null, "System", "room-hello");
        Assert.Equal("room-hello", await groupReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        // Disconnect cleanup: ConnectionGrain.UnregisterAsync must remove itself from the hub/group
        // grains it joined, not just clear its own record.
        await connection.DisposeAsync();
        await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.CloseConnection, TimeSpan.FromSeconds(10));
        Assert.Null(await connectionRegistry.GetAsync(connectionId, CancellationToken.None));
    }

    private HubConnection BuildClient(string clientToken)
    {
        var url = new Uri(_factory.ServerAddress, $"/{HubName}");
        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(clientToken);
                options.Transports = HttpTransportType.WebSockets;
            })
            .Build();
    }
}
