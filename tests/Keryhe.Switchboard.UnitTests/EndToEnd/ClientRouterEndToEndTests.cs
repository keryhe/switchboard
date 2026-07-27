using System.Net.WebSockets;
using System.Text;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Slice 4 gate: a real .NET HubConnection connects to the service against a hand-rolled app-server
/// "double" (standing in for the real Connector, which is Slice 5). Verifies open_connection is
/// observed before client_message (plan decision D6), that a targeted send from the double reaches
/// the client, and that a broadcast reaches multiple connected clients.
/// </summary>
public class ClientRouterEndToEndTests : IAsyncLifetime
{
    private RealKestrelServerFixture _factory = null!;
    private const string HubName = "chatHub-e2e";

    public async Task InitializeAsync()
    {
        _factory = new RealKestrelServerFixture();
        await _factory.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task OpenConnectionPrecedesClientMessage_AndSendToConnectionReachesClient()
    {
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerDouble = await AppServerDouble.ConnectAsync(_factory.ServerAddress, HubName, serverToken);

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));

        await using var connection = BuildClient(clientToken);

        var received = new TaskCompletionSource<(string From, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string, string>("ReceiveMessage", (from, text) => received.SetResult((from, text)));

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var openConnection = await appServerDouble.ReceiveAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ServerEnvelopeType.OpenConnection, openConnection.Type);
        Assert.Equal("json", openConnection.HubProtocol);
        Assert.Equal("alice", openConnection.UserId);
        var connectionId = openConnection.ConnectionId!;

        await connection.SendAsync("Echo", "hello").WaitAsync(TimeSpan.FromSeconds(5));

        // The client may interleave its own keep-alive Ping frames with real invocations; skip
        // past any that aren't the Echo invocation we're waiting for.
        ServerEnvelope clientMessage;
        do
        {
            clientMessage = await appServerDouble.ReceiveAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ServerEnvelopeType.ClientMessage, clientMessage.Type);
            Assert.Equal(connectionId, clientMessage.ConnectionId);
        }
        while (!Encoding.UTF8.GetString(clientMessage.Payload!).Contains("Echo"));

        // send_to_connection: the double replies directly to the client.
        await appServerDouble.SendToConnectionAsync(connectionId, "ReceiveMessage", "System", "direct-hello");

        var (from, text) = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("System", from);
        Assert.Equal("direct-hello", text);
    }

    [Fact]
    public async Task Broadcast_ReachesTwoConnectedClients()
    {
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerDouble = await AppServerDouble.ConnectAsync(_factory.ServerAddress, HubName, serverToken);

        var token1 = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "bob", null, TimeSpan.FromMinutes(1));
        var token2 = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "carol", null, TimeSpan.FromMinutes(1));

        await using var client1 = BuildClient(token1);
        await using var client2 = BuildClient(token2);

        var received1 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received2 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client1.On<string, string>("ReceiveMessage", (_, text) => received1.SetResult(text));
        client2.On<string, string>("ReceiveMessage", (_, text) => received2.SetResult(text));

        await client1.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServerDouble.ReceiveAsync(TimeSpan.FromSeconds(5)); // open_connection for client1

        await client2.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServerDouble.ReceiveAsync(TimeSpan.FromSeconds(5)); // open_connection for client2

        await appServerDouble.BroadcastAsync(HubName, "ReceiveMessage", "System", "broadcast-hello");

        var text1 = await received1.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var text2 = await received2.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("broadcast-hello", text1);
        Assert.Equal("broadcast-hello", text2);
    }

    [Fact]
    public async Task SendToGroup_ReachesMembers_ExcludesExcludedConnection_AndSurvivesUnrelatedDisconnect()
    {
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerDouble = await AppServerDouble.ConnectAsync(_factory.ServerAddress, HubName, serverToken);

        var tokenA = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "member-a", null, TimeSpan.FromMinutes(1));
        var tokenB = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "member-b", null, TimeSpan.FromMinutes(1));
        var tokenExcluded = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "excluded", null, TimeSpan.FromMinutes(1));
        var tokenUnrelated = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "unrelated", null, TimeSpan.FromMinutes(1));

        await using var clientA = BuildClient(tokenA);
        await using var clientB = BuildClient(tokenB);
        await using var clientExcluded = BuildClient(tokenExcluded);
        var clientUnrelated = BuildClient(tokenUnrelated);

        var receivedA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedExcluded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.On<string, string>("ReceiveMessage", (_, text) => receivedA.SetResult(text));
        clientB.On<string, string>("ReceiveMessage", (_, text) => receivedB.SetResult(text));
        clientExcluded.On<string, string>("ReceiveMessage", (_, text) => receivedExcluded.SetResult(text));

        await clientA.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var connectionIdA = (await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5))).ConnectionId!;

        await clientB.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var connectionIdB = (await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5))).ConnectionId!;

        await clientExcluded.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var connectionIdExcluded = (await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5))).ConnectionId!;

        await clientUnrelated.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5)); // for the unrelated connection

        const string room = "room-42";
        await appServerDouble.AddToGroupAsync(connectionIdA, room);
        await appServerDouble.AddToGroupAsync(connectionIdB, room);
        await appServerDouble.AddToGroupAsync(connectionIdExcluded, room);

        // An unrelated connection (never joined the group) disconnects — group membership for the
        // real members must not be disturbed by cleanup of an unrelated connection.
        await clientUnrelated.DisposeAsync();
        await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.CloseConnection, TimeSpan.FromSeconds(5)); // for the unrelated connection

        await appServerDouble.SendToGroupAsync(HubName, room, "ReceiveMessage", [connectionIdExcluded], "System", "room-hello");

        var textA = await receivedA.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var textB = await receivedB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("room-hello", textA);
        Assert.Equal("room-hello", textB);

        var excludedGotIt = await Task.WhenAny(receivedExcluded.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.False(ReferenceEquals(excludedGotIt, receivedExcluded.Task), "excluded connection should not have received the group send");
    }

    [Fact]
    public async Task SendToUser_ReachesAllOfThatUsersConnections_AndNoOthers()
    {
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerDouble = await AppServerDouble.ConnectAsync(_factory.ServerAddress, HubName, serverToken);

        // Two connections for the same user (e.g. two browser tabs), one connection for another user.
        var tokenDave1 = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "dave", null, TimeSpan.FromMinutes(1));
        var tokenDave2 = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "dave", null, TimeSpan.FromMinutes(1));
        var tokenEve = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "eve", null, TimeSpan.FromMinutes(1));

        await using var dave1 = BuildClient(tokenDave1);
        await using var dave2 = BuildClient(tokenDave2);
        await using var eve = BuildClient(tokenEve);

        var receivedDave1 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedDave2 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedEve = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        dave1.On<string, string>("ReceiveMessage", (_, text) => receivedDave1.SetResult(text));
        dave2.On<string, string>("ReceiveMessage", (_, text) => receivedDave2.SetResult(text));
        eve.On<string, string>("ReceiveMessage", (_, text) => receivedEve.SetResult(text));

        await dave1.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));
        await dave2.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));
        await eve.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));

        await appServerDouble.SendToUserAsync(HubName, "dave", "ReceiveMessage", "System", "user-hello");

        var textDave1 = await receivedDave1.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var textDave2 = await receivedDave2.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("user-hello", textDave1);
        Assert.Equal("user-hello", textDave2);

        var eveGotIt = await Task.WhenAny(receivedEve.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.False(ReferenceEquals(eveGotIt, receivedEve.Task), "a different user's connection should not receive Clients.User(dave)'s message");
    }

    private HubConnection BuildClient(string clientToken)
    {
        var url = new Uri(_factory.ServerAddress, $"/{HubName}");
        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(clientToken);
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
            })
            .Build();
    }

}
