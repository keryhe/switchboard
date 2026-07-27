using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Slice 5 gate: a real .NET HubConnection pinned to <see cref="HttpTransportType.ServerSentEvents"/>
/// runs the full flow — negotiate advertising SSE, the handshake over POST, a server push and a
/// group message delivered over the GET event stream — against a hand-rolled app-server double,
/// exactly like <see cref="ClientRouterEndToEndTests"/> does for WebSocket. Also verifies that
/// killing the GET stream produces <c>close_connection</c>.
/// </summary>
public class SseClientEndToEndTests : IAsyncLifetime
{
    private RealKestrelServerFixture _factory = null!;
    private const string HubName = "chatHub-sse-e2e";

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
    public async Task SseClient_ReceivesDirectPush_AndGroupMessage()
    {
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerDouble = await AppServerDouble.ConnectAsync(_factory.ServerAddress, HubName, serverToken);

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        await using var connection = BuildClient(clientToken);

        var directReceived = new TaskCompletionSource<(string From, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string, string>("ReceiveMessage", (from, text) => directReceived.SetResult((from, text)));

        var groupReceived = new TaskCompletionSource<(string From, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string, string>("ReceiveGroupMessage", (from, text) => groupReceived.SetResult((from, text)));

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var openConnection = await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(10));
        Assert.Equal("json", openConnection.HubProtocol);
        Assert.Equal("alice", openConnection.UserId);
        var connectionId = openConnection.ConnectionId!;

        await appServerDouble.AddToGroupAsync(connectionId, "room-1");

        await appServerDouble.SendToConnectionAsync(connectionId, "ReceiveMessage", "System", "direct-hello");
        var (directFrom, directText) = await directReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("System", directFrom);
        Assert.Equal("direct-hello", directText);

        await appServerDouble.SendToGroupAsync(HubName, "room-1", "ReceiveGroupMessage", null, "System", "group-hello");
        var (groupFrom, groupText) = await groupReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("System", groupFrom);
        Assert.Equal("group-hello", groupText);
    }

    [Fact]
    public async Task KillingTheSseStream_ProducesCloseConnection()
    {
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerDouble = await AppServerDouble.ConnectAsync(_factory.ServerAddress, HubName, serverToken);

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "bob", null, TimeSpan.FromMinutes(1));
        var connection = BuildClient(clientToken);

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var openConnection = await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(10));
        var connectionId = openConnection.ConnectionId!;

        await connection.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var closeConnection = await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.CloseConnection, TimeSpan.FromSeconds(15));
        Assert.Equal(connectionId, closeConnection.ConnectionId);

        await connection.DisposeAsync();
    }

    private HubConnection BuildClient(string clientToken)
    {
        var url = new Uri(_factory.ServerAddress, $"/{HubName}");
        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(clientToken);
                options.Transports = HttpTransportType.ServerSentEvents;
            })
            .Build();
    }
}
