using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Negotiate;

public class NegotiateEndpointTests : IClassFixture<WebApplicationFactory<Keryhe.Switchboard.Server.Program>>
{
    private readonly WebApplicationFactory<Keryhe.Switchboard.Server.Program> _factory;

    public NegotiateEndpointTests(WebApplicationFactory<Keryhe.Switchboard.Server.Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Negotiate_WithNoToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/chatHub/negotiate", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_WithInvalidToken_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "garbage");

        var response = await client.PostAsync("/chatHub/negotiate", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_Step1_WithServerToken_ReturnsRedirectShapeOnly()
    {
        var client = _factory.CreateClient();
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", ["chatHub"], TimeSpan.FromHours(1));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serverToken);

        var response = await client.PostAsync("/chatHub/negotiate", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("url", out _));
        Assert.True(json.RootElement.TryGetProperty("accessToken", out _));

        // Assert absence: the redirect must not carry connectionId/availableTransports (03-protocol.md §1.1).
        Assert.False(json.RootElement.TryGetProperty("connectionId", out _));
        Assert.False(json.RootElement.TryGetProperty("availableTransports", out _));
    }

    [Fact]
    public async Task Negotiate_Step1_ForHubNotInServerTokenClaim_Returns403()
    {
        var client = _factory.CreateClient();
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", ["otherHub"], TimeSpan.FromHours(1));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serverToken);

        var response = await client.PostAsync("/chatHub/negotiate", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_Step2_WithNoServerConnections_Returns503()
    {
        var client = _factory.CreateClient();
        var (_, accessToken) = await IssueClientAccessTokenAsync(client, "hubWithNoServers");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.PostAsync("/hubWithNoServers/negotiate", content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_Step2_WithActiveServerConnection_ReturnsConnectShape()
    {
        const string hubName = "chatHubWithServers";
        RegisterFakeServerConnection(hubName);

        var client = _factory.CreateClient();
        var (_, accessToken) = await IssueClientAccessTokenAsync(client, hubName);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.PostAsync($"/{hubName}/negotiate", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("connectionId", out _));
        Assert.True(json.RootElement.TryGetProperty("connectionToken", out _));
        Assert.True(json.RootElement.TryGetProperty("availableTransports", out var transports));
        Assert.True(transports.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Negotiate_Step2_WithClientTokenForDifferentHub_Returns403()
    {
        const string hubName = "chatHubMismatch";
        RegisterFakeServerConnection(hubName);

        var client = _factory.CreateClient();
        var (_, accessToken) = await IssueClientAccessTokenAsync(client, hubName);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.PostAsync("/someOtherHub/negotiate", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<(string ConnectionId, string AccessToken)> IssueClientAccessTokenAsync(HttpClient client, string hubName)
    {
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [hubName], TimeSpan.FromHours(1));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{hubName}/negotiate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serverToken);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var accessToken = json.RootElement.GetProperty("accessToken").GetString()!;
        return (string.Empty, accessToken);
    }

    private void RegisterFakeServerConnection(string hubName)
    {
        var hubRegistry = _factory.Services.GetRequiredService<IHubRegistry>();
        hubRegistry.RegisterServerConnectionAsync(new ServerConnectionState
        {
            ConnectionId = Guid.NewGuid().ToString("n"),
            HubName = hubName,
            AppServerId = "test-server",
            Connection = new FakeServerConnection(hubName),
            ConnectedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private sealed class FakeServerConnection(string hubName) : IServerConnection
    {
        public string ConnectionId { get; } = Guid.NewGuid().ToString("n");
        public string HubName { get; } = hubName;
        public int LogicalConnectionCount => 0;
        public ValueTask SendAsync(ServerEnvelope envelope, CancellationToken ct) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ServerEnvelope> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
