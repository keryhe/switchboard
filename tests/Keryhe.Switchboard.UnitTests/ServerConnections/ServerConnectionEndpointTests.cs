using System.Net.WebSockets;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.ServerConnections;

public class ServerConnectionEndpointTests : IClassFixture<ServerConnectionEndpointTests.Fixture>
{
    private readonly Fixture _fixture;

    public ServerConnectionEndpointTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    public sealed class Fixture : WebApplicationFactory<Keryhe.Switchboard.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Switchboard:ServerPingInterval"] = "00:00:00.200",
                    ["Switchboard:ServerPingTimeout"] = "00:00:00.200",
                });
            });
        }
    }

    private async Task<(WebSocket Socket, string HubName)> ConnectAndHandshakeAsync(string hubName, IEnumerable<string> tokenHubs, int handshakeVersion = 1)
    {
        var tokenService = _fixture.Services.GetRequiredService<ITokenService>();
        var token = tokenService.IssueServerToken("test-server", tokenHubs, TimeSpan.FromHours(1));

        var wsClient = _fixture.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req => req.Headers["Authorization"] = $"Bearer {token}";

        var uri = new Uri(_fixture.Server.BaseAddress, $"/server/{hubName}");
        var socket = await wsClient.ConnectAsync(uri, CancellationToken.None);

        await SendEnvelopeAsync(socket, new ServerEnvelope { Type = ServerEnvelopeType.Handshake, HubName = hubName, Version = handshakeVersion });
        return (socket, hubName);
    }

    private static async Task SendEnvelopeAsync(WebSocket socket, ServerEnvelope envelope)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        ServerEnvelopeSerializer.Write(buffer, envelope);
        await socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
    }

    private static async Task<ServerEnvelope> ReceiveEnvelopeAsync(WebSocket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[8192];
        var totalRead = 0;

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(totalRead), cts.Token);
            totalRead += result.Count;

            if (ServerEnvelopeSerializer.TryParseEnvelope(
                    new System.Buffers.ReadOnlySequence<byte>(buffer.AsMemory(0, totalRead)),
                    out var envelope, out _, out _))
            {
                return envelope!;
            }

            if (result.EndOfMessage && totalRead == 0)
            {
                throw new InvalidOperationException("Socket closed before a complete envelope arrived.");
            }
        }
    }

    [Fact]
    public async Task Connect_WithNoToken_Returns401()
    {
        var wsClient = _fixture.Server.CreateWebSocketClient();
        var uri = new Uri(_fixture.Server.BaseAddress, "/server/chatHub");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wsClient.ConnectAsync(uri, CancellationToken.None));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task Connect_WithHubNotInServerTokenClaim_Returns403()
    {
        var tokenService = _fixture.Services.GetRequiredService<ITokenService>();
        var token = tokenService.IssueServerToken("test-server", ["otherHub"], TimeSpan.FromHours(1));

        var wsClient = _fixture.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req => req.Headers["Authorization"] = $"Bearer {token}";
        var uri = new Uri(_fixture.Server.BaseAddress, "/server/chatHub");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wsClient.ConnectAsync(uri, CancellationToken.None));
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task Connect_HandshakesAndAppearsInHubRegistry_ThenSurvivesPingPong()
    {
        const string hubName = "chatHub-handshake";
        var (socket, _) = await ConnectAndHandshakeAsync(hubName, [hubName]);

        var ack = await ReceiveEnvelopeAsync(socket, TimeSpan.FromSeconds(5));
        Assert.Equal(ServerEnvelopeType.HandshakeAck, ack.Type);
        Assert.NotNull(ack.ConnectionId);

        var hubRegistry = _fixture.Services.GetRequiredService<IHubRegistry>();
        Assert.True(hubRegistry.HasActiveServerConnection(hubName));

        // The endpoint's ping loop (interval overridden to 200ms) should send at least one Ping.
        var ping = await ReceiveEnvelopeAsync(socket, TimeSpan.FromSeconds(5));
        Assert.Equal(ServerEnvelopeType.Ping, ping.Type);

        await SendEnvelopeAsync(socket, new ServerEnvelope { Type = ServerEnvelopeType.Pong });

        // Give the server a moment to process the pong, then the connection should still be Connected.
        await Task.Delay(300);
        var descriptor = hubRegistry.GetHub(hubName);
        Assert.NotNull(descriptor);
        Assert.Equal(1, descriptor!.ActiveServerConnectionCount);

        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token);

        await WaitUntilAsync(() => !hubRegistry.HasActiveServerConnection(hubName), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Connect_WithUnsupportedHandshakeVersion_ReceivesHandshakeErrorAndCloses()
    {
        const string hubName = "chatHub-badversion";
        var (socket, _) = await ConnectAndHandshakeAsync(hubName, [hubName], handshakeVersion: 2);

        var response = await ReceiveEnvelopeAsync(socket, TimeSpan.FromSeconds(5));
        Assert.Equal(ServerEnvelopeType.HandshakeError, response.Type);
        Assert.NotNull(response.Error);

        var hubRegistry = _fixture.Services.GetRequiredService<IHubRegistry>();
        Assert.False(hubRegistry.HasActiveServerConnection(hubName));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(50);
        }
    }
}
