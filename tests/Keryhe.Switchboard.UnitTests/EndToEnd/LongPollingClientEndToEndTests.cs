using System.Text;
using System.Text.Json;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Slice 6 gate: a real .NET HubConnection pinned to <see cref="HttpTransportType.LongPolling"/>
/// runs the full flow — negotiate advertising Long Polling, the establishing GET, the handshake
/// over POST, a server push delivered on a later poll — over both JSON and MessagePack, exactly
/// like <see cref="SseClientEndToEndTests"/> does for SSE. Also verifies that a client which stops
/// polling without ever sending DELETE produces <c>close_connection</c> within
/// <c>DisconnectTimeout</c> — the one failure mode unique to this transport, driven directly with
/// raw HTTP requests since no real client would deliberately abandon a connection like this.
/// </summary>
public class LongPollingClientEndToEndTests : IAsyncLifetime
{
    private RealKestrelServerFixture _factory = null!;
    private const string HubName = "chatHub-longpoll-e2e";

    public async Task InitializeAsync()
    {
        _factory = new RealKestrelServerFixture();
        await _factory.StartAsync("--Switchboard:DisconnectTimeout", "00:00:01");
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task LongPollingClient_ReceivesDirectPush_AndGroupMessage_OverJson()
    {
        await RunDirectPushAndGroupMessageAsync(useMessagePack: false);
    }

    [Fact]
    public async Task LongPollingClient_ReceivesDirectPush_OverMessagePack()
    {
        await RunDirectPushAndGroupMessageAsync(useMessagePack: true);
    }

    private async Task RunDirectPushAndGroupMessageAsync(bool useMessagePack)
    {
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerDouble = await AppServerDouble.ConnectAsync(_factory.ServerAddress, HubName, serverToken);

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        await using var connection = BuildClient(clientToken, useMessagePack);

        var directReceived = new TaskCompletionSource<(string From, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string, string>("ReceiveMessage", (from, text) => directReceived.SetResult((from, text)));

        var groupReceived = new TaskCompletionSource<(string From, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string, string>("ReceiveGroupMessage", (from, text) => groupReceived.SetResult((from, text)));

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var openConnection = await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(10));
        Assert.Equal(useMessagePack ? "messagepack" : "json", openConnection.HubProtocol);
        Assert.Equal("alice", openConnection.UserId);
        var connectionId = openConnection.ConnectionId!;

        await appServerDouble.AddToGroupAsync(connectionId, "room-1");

        await appServerDouble.SendToConnectionUsingProtocolAsync(connectionId, useMessagePack ? "messagepack" : "json", "ReceiveMessage", "System", "direct-hello");
        var (directFrom, directText) = await directReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("System", directFrom);
        Assert.Equal("direct-hello", directText);

        await appServerDouble.SendToGroupAsync(HubName, "room-1", "ReceiveGroupMessage", null, "System", "group-hello");
        var (groupFrom, groupText) = await groupReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("System", groupFrom);
        Assert.Equal("group-hello", groupText);
    }

    [Fact]
    public async Task AbandoningPolls_ProducesCloseConnection_WithinDisconnectTimeout()
    {
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerDouble = await AppServerDouble.ConnectAsync(_factory.ServerAddress, HubName, serverToken);

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "bob", null, TimeSpan.FromMinutes(1));

        using var http = new HttpClient { BaseAddress = _factory.ServerAddress };

        var negotiateReq = new HttpRequestMessage(HttpMethod.Post, $"/{HubName}/negotiate?negotiateVersion=1");
        negotiateReq.Headers.Add("Authorization", $"Bearer {clientToken}");
        var negotiateResp = await http.SendAsync(negotiateReq);
        var negotiateBody = await negotiateResp.Content.ReadAsStringAsync();
        var connectionToken = JsonDocument.Parse(negotiateBody).RootElement.GetProperty("connectionToken").GetString();

        // Establishing GET.
        var establishResp = await http.GetAsync($"/{HubName}?id={connectionToken}&access_token={clientToken}");
        Assert.True(establishResp.IsSuccessStatusCode);

        // Handshake, sent over POST exactly like a real client would.
        var handshake = "{\"protocol\":\"json\",\"version\":1}\x1e";
        var handshakeResp = await http.PostAsync(
            $"/{HubName}?id={connectionToken}&access_token={clientToken}",
            new StringContent(handshake, Encoding.UTF8, "application/json"));
        Assert.True(handshakeResp.IsSuccessStatusCode);

        var openConnection = await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(10));
        var connectionId = openConnection.ConnectionId!;

        // Abandon the connection: never poll again, never send DELETE. The reaper is the only
        // thing that will ever notice.
        var closeConnection = await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.CloseConnection, TimeSpan.FromSeconds(15));
        Assert.Equal(connectionId, closeConnection.ConnectionId);
    }

    private HubConnection BuildClient(string clientToken, bool useMessagePack)
    {
        var url = new Uri(_factory.ServerAddress, $"/{HubName}");
        var builder = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(clientToken);
                options.Transports = HttpTransportType.LongPolling;
            });

        if (useMessagePack)
        {
            builder.AddMessagePackProtocol();
        }

        return builder.Build();
    }
}
