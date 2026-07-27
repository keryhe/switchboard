using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Slice 9 gate (plan §4, "Parameterize, don't duplicate"): the {WebSockets, SSE, LongPolling} x
/// {json, messagepack} matrix, written once as a theory rather than as five near-duplicate copies
/// of what <see cref="ClientRouterEndToEndTests"/>, <see cref="SseClientEndToEndTests"/> and
/// <see cref="LongPollingClientEndToEndTests"/> already cover individually per-transport. This
/// suite exists to catch intersection bugs those per-transport gates cannot see by construction —
/// each of them only ever exercises one cell. SSE+MessagePack is the one impossible cell (SSE is
/// Text-only by design, Slice 5) and is asserted absent from negotiate rather than silently
/// skipped.
/// </summary>
public class TransportProtocolMatrixEndToEndTests : IAsyncLifetime
{
    private RealKestrelServerFixture _factory = null!;
    private const string HubName = "chatHub-matrix-e2e";

    public async Task InitializeAsync()
    {
        _factory = new RealKestrelServerFixture();
        await _factory.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    public static IEnumerable<object[]> Cells()
    {
        yield return [HttpTransportType.WebSockets, false];
        yield return [HttpTransportType.WebSockets, true];
        yield return [HttpTransportType.ServerSentEvents, false];
        // (ServerSentEvents, true) deliberately omitted — see NegotiateAdvertisesServerSentEvents_AsTextOnly.
        yield return [HttpTransportType.LongPolling, false];
        yield return [HttpTransportType.LongPolling, true];
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task DirectSend_AndGroupSend_RoundTrip_ForEveryValidCell(HttpTransportType transport, bool useMessagePack)
    {
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerDouble = await AppServerDouble.ConnectAsync(_factory.ServerAddress, HubName, serverToken);

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        await using var connection = BuildClient(clientToken, transport, useMessagePack);

        var directReceived = new TaskCompletionSource<(string From, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string, string>("ReceiveMessage", (from, text) => directReceived.SetResult((from, text)));

        var groupReceived = new TaskCompletionSource<(string From, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string, string>("ReceiveGroupMessage", (from, text) => groupReceived.SetResult((from, text)));

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var openConnection = await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(10));
        Assert.Equal(useMessagePack ? "messagepack" : "json", openConnection.HubProtocol);
        Assert.Equal("alice", openConnection.UserId);
        var connectionId = openConnection.ConnectionId!;

        await appServerDouble.AddToGroupAsync(connectionId, "room-matrix");

        var protocolName = useMessagePack ? "messagepack" : "json";
        await appServerDouble.SendToConnectionUsingProtocolAsync(connectionId, protocolName, "ReceiveMessage", "System", "direct-hello");
        var (directFrom, directText) = await directReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("System", directFrom);
        Assert.Equal("direct-hello", directText);

        await appServerDouble.SendToGroupAsync(HubName, "room-matrix", "ReceiveGroupMessage", null, "System", "group-hello");
        var (groupFrom, groupText) = await groupReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("System", groupFrom);
        Assert.Equal("group-hello", groupText);

        await connection.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.CloseConnection, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task NegotiateAdvertisesServerSentEvents_AsTextOnly()
    {
        // The impossible matrix cell, pinned as a negative assertion against the real negotiate
        // response rather than left as an implicit gap: SSE must never claim Binary support, which
        // is what would let a MessagePack-over-SSE client attempt (and silently corrupt) a
        // connection instead of failing fast at handshake negotiation (Slice 5's
        // TransportFailedException finding).
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        await using var appServerDouble = await AppServerDouble.ConnectAsync(_factory.ServerAddress, HubName, serverToken);

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));

        using var http = new HttpClient { BaseAddress = _factory.ServerAddress };
        var request = new HttpRequestMessage(HttpMethod.Post, $"/{HubName}/negotiate?negotiateVersion=1");
        request.Headers.Add("Authorization", $"Bearer {clientToken}");
        var response = await http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var transports = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("availableTransports");

        var sse = transports.EnumerateArray().Single(t => t.GetProperty("transport").GetString() == "ServerSentEvents");
        var transferFormats = sse.GetProperty("transferFormats").EnumerateArray().Select(f => f.GetString()).ToList();

        Assert.DoesNotContain("Binary", transferFormats);
        Assert.Contains("Text", transferFormats);
    }

    private HubConnection BuildClient(string clientToken, HttpTransportType transport, bool useMessagePack)
    {
        var url = new Uri(_factory.ServerAddress, $"/{HubName}");
        var builder = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(clientToken);
                options.Transports = transport;
            });

        if (useMessagePack)
        {
            builder.AddMessagePackProtocol();
        }

        return builder.Build();
    }
}
