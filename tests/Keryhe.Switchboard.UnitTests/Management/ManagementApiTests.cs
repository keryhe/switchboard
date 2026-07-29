using System.Net;
using System.Net.Http.Headers;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Management;

/// <summary>
/// Phase 4 Slice 1 gate (plans/phase-4-management-and-observability.md §4): the management API's
/// auth (plan decision D21) and its group-membership endpoints, which must delegate to the exact
/// same code the app-server-originated <c>add_to_group</c>/<c>remove_from_group</c> envelope
/// handling uses (plan decision D23) rather than a second implementation that could silently miss
/// the Phase 3 Slice 7 cross-node fix. The cross-node case itself is covered separately in
/// <see cref="EndToEnd.ManagementApiCrossNodeEndToEndTests"/>.
/// </summary>
public class ManagementApiTests
{
    private const string HubName = "chatHub-management-e2e";
    private const string ManagementSigningKey = "dev-only-management-signing-key-change-me-32+";

    [Fact]
    public async Task ManagementApi_DisabledByDefault_Returns404NotMapped()
    {
        await using var service = new RealKestrelServerFixture();
        await service.StartAsync(); // EnableManagementApi defaults to false.

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        var response = await http.PutAsync($"/api/v1/hubs/{HubName}/groups/g/connections/c", content: null);

        // Not mapped at all — 404, not 401 (plan decision D21: fail closed by absence).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EnableManagementApi_WithNoSigningKey_RefusesToStart()
    {
        var app = Keryhe.Switchboard.Server.Program.BuildApp([
            "--urls", "http://127.0.0.1:0",
            "--Switchboard:PublicUrl", "http://127.0.0.1:0",
            "--Switchboard:TokenSigningKey", "dev-only-client-signing-key-change-me-32+",
            "--Switchboard:ServerSigningKey", "dev-only-server-signing-key-change-me-32+",
            "--Switchboard:EnableManagementApi", "true",
        ]);

        await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => app.StartAsync());
    }

    /// <summary>Phase 4 Slice 6 gate: the OpenAPI document exists (and is fetchable without a
    /// token — see D21's "spec describes shapes, not data" rationale) once the management API is
    /// enabled, and covers every /api/v1 route this test suite otherwise exercises.</summary>
    [Fact]
    public async Task ManagementApi_Enabled_ServesOpenApiDocumentCoveringEveryRoute()
    {
        await using var service = await StartWithManagementApiAsync();

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        var response = await http.GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var document = await response.Content.ReadAsStringAsync();
        Assert.Contains("/hubs/{hubName}/send", document);
        Assert.Contains("/hubs/{hubName}/users/{userId}/send", document);
        Assert.Contains("/hubs/{hubName}/groups/{groupName}/send", document);
        Assert.Contains("/hubs/{hubName}/groups/{groupName}/connections/{connectionId}", document);
        Assert.Contains("/hubs/{hubName}/connections", document);
        Assert.Contains("/health", document);
    }

    [Fact]
    public async Task ManagementApi_DisabledByDefault_OpenApiDocumentAlsoNotMapped()
    {
        await using var service = new RealKestrelServerFixture();
        await service.StartAsync();

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        var response = await http.GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ManagementApi_WithNoToken_Returns401()
    {
        await using var service = await StartWithManagementApiAsync();

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        var response = await http.PutAsync($"/api/v1/hubs/{HubName}/groups/g/connections/c", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ManagementApi_WithServerToken_Returns401()
    {
        await using var service = await StartWithManagementApiAsync();
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serverToken);
        var response = await http.PutAsync($"/api/v1/hubs/{HubName}/groups/g/connections/c", content: null);

        // A server access token must never drive the management API (ADR-004) — fails validation
        // at the audience/signing-key check, indistinguishable from a garbage token, hence 401 not
        // 403 (plan decision D21).
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ManagementApi_WithManagementTokenAgainstServerConnectionEndpoint_Returns401()
    {
        await using var service = await StartWithManagementApiAsync();
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        // The reverse direction of the previous test (ADR-004's whole point): a management token
        // must not drive the app-server WebSocket endpoint either.
        var wsClient = new System.Net.WebSockets.ClientWebSocket();
        wsClient.Options.SetRequestHeader("Authorization", $"Bearer {managementToken}");
        var wsUri = new UriBuilder(service.ServerAddress) { Scheme = "ws", Path = $"/server/{HubName}" }.Uri;

        // Real socket (RealKestrelServerFixture), unlike WebApplicationFactory's in-memory
        // TestServer client — a real ClientWebSocket reports a non-101 handshake response as
        // WebSocketException, not InvalidOperationException.
        var ex = await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => wsClient.ConnectAsync(wsUri, CancellationToken.None));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task ManagementApi_OutsideAllowedNetwork_Returns403_EvenWithValidToken()
    {
        // 10.0.0.0/8 deliberately excludes the loopback address this test connects from — the peer
        // really is outside the allowlist (mirrors DirectNegotiateEndToEndTests' own pattern).
        await using var service = await StartWithManagementApiAsync("--Switchboard:ManagementAllowedNetworks:0", "10.0.0.0/8");
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);
        var response = await http.PutAsync($"/api/v1/hubs/{HubName}/groups/g/connections/c", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ManagementApi_WithinAllowedNetwork_Succeeds()
    {
        await using var service = await StartWithManagementApiAsync("--Switchboard:ManagementAllowedNetworks:0", "127.0.0.1/32");
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        await using var appServer = await AppServerDouble.ConnectAsync(service.ServerAddress, HubName, serverToken);
        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        await using var client = BuildClient(service, clientToken);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<string, string>("ReceiveMessage", (_, text) => received.TrySetResult(text));
        await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var open = await appServer.ReceiveEnvelopeAsync(Keryhe.Switchboard.Protocol.ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);
        var response = await http.PutAsync($"/api/v1/hubs/{HubName}/groups/allowed-group/connections/{open.ConnectionId}", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await appServer.SendToGroupAsync(HubName, "allowed-group", "ReceiveMessage", null, "System", "group-hello");
        Assert.Equal("group-hello", await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ManagementApi_AddToGroup_ThenGroupSend_ReachesConnection()
    {
        await using var service = await StartWithManagementApiAsync();
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        await using var appServer = await AppServerDouble.ConnectAsync(service.ServerAddress, HubName, serverToken);
        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "bob", null, TimeSpan.FromMinutes(1));
        await using var client = BuildClient(service, clientToken);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.On<string, string>("ReceiveMessage", (_, text) => received.TrySetResult(text));
        await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var open = await appServer.ReceiveEnvelopeAsync(Keryhe.Switchboard.Protocol.ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);
        var addResponse = await http.PutAsync($"/api/v1/hubs/{HubName}/groups/mgmt-group/connections/{open.ConnectionId}", content: null);
        Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);

        await appServer.SendToGroupAsync(HubName, "mgmt-group", "ReceiveMessage", null, "System", "mgmt-hello");
        Assert.Equal("mgmt-hello", await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ManagementApi_RemoveFromGroup_StopsFutureGroupMessages()
    {
        await using var service = await StartWithManagementApiAsync();
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        await using var appServer = await AppServerDouble.ConnectAsync(service.ServerAddress, HubName, serverToken);
        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "carol", null, TimeSpan.FromMinutes(1));
        await using var client = BuildClient(service, clientToken);
        var receivedCount = 0;
        client.On<string, string>("ReceiveMessage", (_, _) => receivedCount++);
        await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var open = await appServer.ReceiveEnvelopeAsync(Keryhe.Switchboard.Protocol.ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);

        var addResponse = await http.PutAsync($"/api/v1/hubs/{HubName}/groups/remove-group/connections/{open.ConnectionId}", content: null);
        Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);

        var removeResponse = await http.DeleteAsync($"/api/v1/hubs/{HubName}/groups/remove-group/connections/{open.ConnectionId}");
        Assert.Equal(HttpStatusCode.OK, removeResponse.StatusCode);

        await appServer.SendToGroupAsync(HubName, "remove-group", "ReceiveMessage", null, "System", "should-not-arrive");

        // Assert absence: give the (non-)delivery a beat, then confirm nothing arrived.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(0, receivedCount);
    }

    private static async Task<RealKestrelServerFixture> StartWithManagementApiAsync(params string[] extraArgs)
    {
        var service = new RealKestrelServerFixture();
        await service.StartAsync(
        [
            "--Switchboard:EnableManagementApi", "true",
            "--Switchboard:ManagementSigningKey", ManagementSigningKey,
            .. extraArgs,
        ]);
        return service;
    }

    private static HubConnection BuildClient(RealKestrelServerFixture service, string clientToken)
    {
        var url = new Uri(service.ServerAddress, $"/{HubName}");
        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(clientToken);
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
            })
            .Build();
    }
}
