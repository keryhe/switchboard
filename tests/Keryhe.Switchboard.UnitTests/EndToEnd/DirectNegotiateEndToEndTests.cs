using System.Linq;
using System.Net;
using System.Text.Json;
using Keryhe.Switchboard.Connector;
using Keryhe.Switchboard.Connector.ServerConnections;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Slice 8 gate (plan decision D11): Pattern A's network allowlist governs whether asserted
/// identity is believed, never whether the negotiate endpoint answers — an allowlisted peer's
/// identity flows all the way to a real Hub's <c>Context.User</c>/<c>Context.UserIdentifier</c>,
/// while a non-allowlisted peer asserting <c>X-Switchboard-UserId: admin</c> gets a real,
/// anonymous connection (not a rejection). Also confirms Pattern B is untouched by the allowlist
/// (its trust boundary is the server token, not the network) and that direct negotiate stays
/// disabled by default.
/// </summary>
public class DirectNegotiateEndToEndTests
{
    private const string HubName = "testHub-directnegotiate";

    [Fact]
    public async Task AllowlistedPeer_NegotiatesWithAssertedIdentity_AndHubContextReflectsIt()
    {
        await using var env = await Environment.StartAsync(
            "--Switchboard:EnableDirectNegotiate", "true",
            "--Switchboard:TrustedProxyNetworks:0", "127.0.0.1/32");

        await using var connection = env.BuildDirectClient(headers => headers["X-Switchboard-UserId"] = "alice");
        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var isAuthenticated = await connection.InvokeAsync<bool>("IsAuthenticated").WaitAsync(TimeSpan.FromSeconds(10));
        var userId = await connection.InvokeAsync<string?>("GetUserIdentifier").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(isAuthenticated);
        Assert.Equal("alice", userId);
    }

    [Fact]
    public async Task NonAllowlistedPeer_AssertingAdminIdentity_GetsAnonymousConnection_AndHubContextIsUnauthenticated()
    {
        // 10.0.0.0/8 deliberately excludes the loopback address every test in this process
        // connects from — the peer really is outside the allowlist, not merely configured oddly.
        await using var env = await Environment.StartAsync(
            "--Switchboard:EnableDirectNegotiate", "true",
            "--Switchboard:TrustedProxyNetworks:0", "10.0.0.0/8");

        await using var connection = env.BuildDirectClient(headers => headers["X-Switchboard-UserId"] = "admin");
        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var isAuthenticated = await connection.InvokeAsync<bool>("IsAuthenticated").WaitAsync(TimeSpan.FromSeconds(10));
        var userId = await connection.InvokeAsync<string?>("GetUserIdentifier").WaitAsync(TimeSpan.FromSeconds(10));

        // This is the Phase 0 authenticationType fix earning its keep (00-review-findings.md):
        // an anonymous ClaimsIdentity must report IsAuthenticated == false, not true-because-it-
        // has-no-claims-to-check.
        Assert.False(isAuthenticated);
        Assert.Null(userId);
    }

    [Fact]
    public async Task PatternB_StillWorks_FromPeerOutsideTheAllowlist()
    {
        await using var service = new RealKestrelServerFixture();
        await service.StartAsync(
            "--Switchboard:EnableDirectNegotiate", "true",
            "--Switchboard:TrustedProxyNetworks:0", "10.0.0.0/8");

        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-app-server-b", [HubName], TimeSpan.FromHours(1));

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        var request = new HttpRequestMessage(HttpMethod.Post, $"/{HubName}/negotiate");
        request.Headers.Add("Authorization", $"Bearer {serverToken}");
        request.Headers.Add("X-Switchboard-UserId", "bob");

        var response = await http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var accessToken = JsonDocument.Parse(body).RootElement.GetProperty("accessToken").GetString()!;

        // A server token's identity headers are trusted unconditionally (the trust boundary is
        // the token, not the peer address) — the redirect's client token must carry "bob" even
        // though this peer sits outside TrustedProxyNetworks.
        var principal = tokenService.Validate(accessToken, SwitchboardTokenType.Client);
        Assert.NotNull(principal);
        Assert.Equal("bob", principal!.FindFirst("sub")?.Value);
    }

    [Fact]
    public async Task DirectNegotiateDisabled_StillReturnsUnauthorized_ForAnUntokenedRequest()
    {
        await using var service = new RealKestrelServerFixture();
        await service.StartAsync(); // EnableDirectNegotiate defaults to false.

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        var response = await http.PostAsync($"/{HubName}/negotiate", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Pins the existing fail-fast startup validation (04-design.md §1 rule 2,
    /// Program.cs) — an identity header trusted from anywhere is a trivial impersonation vector,
    /// so the service must refuse to start rather than silently run with an empty allowlist.</summary>
    [Fact]
    public async Task EnableDirectNegotiate_WithNoTrustedProxyNetworks_RefusesToStart()
    {
        var app = Keryhe.Switchboard.Server.Program.BuildApp([
            "--urls", "http://127.0.0.1:0",
            "--Switchboard:PublicUrl", "http://127.0.0.1:0",
            "--Switchboard:TokenSigningKey", "dev-only-client-signing-key-change-me-32+",
            "--Switchboard:ServerSigningKey", "dev-only-server-signing-key-change-me-32+",
            "--Switchboard:EnableDirectNegotiate", "true",
        ]);

        await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => app.StartAsync());
    }

    /// <summary>Wires a real service + a real app server (via the real Connector, not a hand-
    /// rolled double) so a hub method can actually inspect <c>Context.User</c> — the only way to
    /// prove identity flowed all the way through, not just that the redirect token looked right.</summary>
    private sealed class Environment(RealKestrelServerFixture service, WebApplication appServer) : IAsyncDisposable
    {
        public static async Task<Environment> StartAsync(params string[] serviceArgs)
        {
            var service = new RealKestrelServerFixture();
            await service.StartAsync(serviceArgs);

            var tokenService = service.Services.GetRequiredService<ITokenService>();
            var serverToken = tokenService.IssueServerToken("test-app-server", [HubName], TimeSpan.FromHours(1));

            var builder = WebApplication.CreateBuilder(["--urls", "http://127.0.0.1:0"]);
            builder.Services.AddSignalR();
            builder.Services.AddSwitchboardConnector(options =>
            {
                options.ServiceUrl = service.ServerAddress.ToString();
                options.ServerAccessToken = serverToken;
                options.ServerConnectionsPerHub = 1;
            });

            var appServer = builder.Build();
            appServer.MapHub<IdentityHub>($"/{HubName}");
            await appServer.StartAsync();

            var poolRegistry = appServer.Services.GetRequiredService<ConnectorConnectionPoolRegistry>();
            using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!poolRegistry.All.Any())
            {
                readyCts.Token.ThrowIfCancellationRequested();
                await Task.Delay(20);
            }

            await poolRegistry.All.Single().WaitForFirstConnectionAsync(readyCts.Token);

            return new Environment(service, appServer);
        }

        /// <summary>A client that negotiates directly against the service (Pattern A) — no app
        /// server negotiate forwarding involved at all, unlike every other end-to-end test in this
        /// project. <c>HttpConnectionOptions.Headers</c> attaches to every request the client
        /// makes, including the very first (unauthenticated) negotiate POST.</summary>
        public HubConnection BuildDirectClient(Action<IDictionary<string, string>> configureHeaders)
        {
            return new HubConnectionBuilder()
                .WithUrl(new Uri(service.ServerAddress, $"/{HubName}"), options => configureHeaders(options.Headers))
                .Build();
        }

        public async ValueTask DisposeAsync()
        {
            await appServer.StopAsync();
            await appServer.DisposeAsync();
            await service.DisposeAsync();
        }
    }
}

public class IdentityHub : Hub
{
    public bool IsAuthenticated() => Context.User?.Identity?.IsAuthenticated ?? false;

    public string? GetUserIdentifier() => Context.UserIdentifier;
}
