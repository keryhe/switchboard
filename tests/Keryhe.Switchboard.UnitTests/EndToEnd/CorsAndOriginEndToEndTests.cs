using System.Net;
using System.Net.WebSockets;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Slice 9 gate: browsers do not preflight a WebSocket upgrade, so <c>app.UseCors</c> never sees
/// it — <see cref="Keryhe.Switchboard.Server.ClientConnections.ClientConnectionValidation.IsOriginAllowed"/>
/// is the only thing standing between a configured <c>AllowedOrigins</c> allowlist and a
/// cross-origin page opening a socket straight to the proxy. Also verifies the assumption that CORS
/// preflight on the ordinary HTTP endpoints (negotiate, and the SSE/Long Polling POST/DELETE
/// routes) is already covered by the global <c>app.UseCors("Switchboard")</c> default policy,
/// rather than assuming it and leaving it unverified.
/// </summary>
public class CorsAndOriginEndToEndTests
{
    private const string HubName = "chatHub-cors-e2e";

    [Fact]
    public async Task WebSocketUpgrade_FromDisallowedOrigin_IsRejected()
    {
        await using var service = new RealKestrelServerFixture();
        await service.StartAsync("--Switchboard:AllowedOrigins:0", "https://allowed.example.com");

        var wsUri = new Uri($"ws://{service.ServerAddress.Authority}/{HubName}?id=irrelevant&access_token=irrelevant");
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://evil.example.com");

        var ex = await Assert.ThrowsAsync<WebSocketException>(
            () => socket.ConnectAsync(wsUri, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task WebSocketUpgrade_FromAllowedOrigin_IsNotRejectedByOriginCheck()
    {
        await using var service = new RealKestrelServerFixture();
        await service.StartAsync("--Switchboard:AllowedOrigins:0", "https://allowed.example.com");

        var wsUri = new Uri($"ws://{service.ServerAddress.Authority}/{HubName}?id=irrelevant&access_token=irrelevant");
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://allowed.example.com");

        // The connection token/access token are garbage, so this still fails — but on the *token*
        // check (401), never the Origin check (403), proving an allowed Origin passes through.
        var ex = await Assert.ThrowsAsync<WebSocketException>(
            () => socket.ConnectAsync(wsUri, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task WebSocketUpgrade_WithNoOriginHeader_IsNotRejected()
    {
        // The .NET SignalR client (and any other non-browser caller) never sends an Origin header
        // at all — AllowedOrigins must not lock those callers out.
        await using var service = new RealKestrelServerFixture();
        await service.StartAsync("--Switchboard:AllowedOrigins:0", "https://allowed.example.com");

        var wsUri = new Uri($"ws://{service.ServerAddress.Authority}/{HubName}?id=irrelevant&access_token=irrelevant");
        using var socket = new ClientWebSocket();

        var ex = await Assert.ThrowsAsync<WebSocketException>(
            () => socket.ConnectAsync(wsUri, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("401", ex.Message);
    }

    [Theory]
    [InlineData("POST", $"/{HubName}/negotiate")]
    [InlineData("POST", $"/{HubName}")]
    [InlineData("DELETE", $"/{HubName}")]
    public async Task CorsPreflight_ForHttpEndpoint_IsAllowed_ForAnAllowedOrigin(string method, string path)
    {
        await using var service = new RealKestrelServerFixture();
        await service.StartAsync("--Switchboard:AllowedOrigins:0", "https://allowed.example.com");

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        var preflight = new HttpRequestMessage(HttpMethod.Options, path);
        preflight.Headers.Add("Origin", "https://allowed.example.com");
        preflight.Headers.Add("Access-Control-Request-Method", method);
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        var response = await http.SendAsync(preflight);

        Assert.True(response.IsSuccessStatusCode, $"preflight for {method} {path} returned {response.StatusCode}");
        Assert.Equal("https://allowed.example.com", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task CorsPreflight_FromDisallowedOrigin_DoesNotEchoTheOrigin()
    {
        await using var service = new RealKestrelServerFixture();
        await service.StartAsync("--Switchboard:AllowedOrigins:0", "https://allowed.example.com");

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        var preflight = new HttpRequestMessage(HttpMethod.Options, $"/{HubName}/negotiate");
        preflight.Headers.Add("Origin", "https://evil.example.com");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await http.SendAsync(preflight);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
