using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Management.Models;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Management;

/// <summary>
/// Phase 4 Slice 3 gate (plans/phase-4-management-and-observability.md §4, plan decision D27):
/// <c>GET /api/v1/hubs/{hub}/connections</c> (mandatorily paginated) and <c>GET /api/v1/health</c>
/// (per-hub cluster-wide counts), plus the standing requirement that <c>/healthz</c> itself stays
/// byte-identical to its Phase 3 shape — no new topology detail leaking onto the public,
/// unauthenticated probe. The cross-node case (a connection/hub known only to a different node)
/// is covered separately in <see cref="EndToEnd.ManagementApiCrossNodeEndToEndTests"/>.
/// </summary>
public class ManagementClusterInventoryTests
{
    private const string HubName = "chatHub-management-inventory-e2e";
    private const string ManagementSigningKey = "dev-only-management-signing-key-change-me-32+";

    [Fact]
    public async Task Healthz_StaysByteIdentical_WithManagementApiEnabled()
    {
        await using var service = await StartWithManagementApiAsync();

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        var response = await http.GetAsync("/healthz");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Byte-identical to the Phase 3 shape (03-protocol.md § Health Check): no counts, no hub
        // names, nothing this phase added — asserted, not assumed (plan decision D27).
        Assert.Equal("""{"status":"healthy"}""", body);
    }

    [Fact]
    public async Task Health_ReportsServerAndClientConnectionCountsForKnownHub()
    {
        await using var service = await StartWithManagementApiAsync();
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        await using var appServer = await AppServerDouble.ConnectAsync(service.ServerAddress, HubName, serverToken);

        var token1 = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        var token2 = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "bob", null, TimeSpan.FromMinutes(1));
        await using var client1 = BuildClient(service, token1);
        await using var client2 = BuildClient(service, token2);
        await client1.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServer.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));
        await client2.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServer.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);
        var response = await http.GetFromJsonAsync<ManagementHealthResponse>("/api/v1/health");

        Assert.NotNull(response);
        Assert.Equal("healthy", response!.Status);
        Assert.Equal(1, response.ServerConnections[HubName]);
        Assert.Equal(2, response.ClientConnections);
    }

    [Fact]
    public async Task Health_WithNoToken_Returns401()
    {
        await using var service = await StartWithManagementApiAsync();

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        var response = await http.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListConnections_ReturnsConnectionWithGroupsAndUserId()
    {
        await using var service = await StartWithManagementApiAsync();
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        await using var appServer = await AppServerDouble.ConnectAsync(service.ServerAddress, HubName, serverToken);

        var token = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "carol", null, TimeSpan.FromMinutes(1));
        await using var client = BuildClient(service, token);
        await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var open = await appServer.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);

        var addResponse = await http.PutAsync($"/api/v1/hubs/{HubName}/groups/list-group/connections/{open.ConnectionId}", content: null);
        Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);

        var page = await http.GetFromJsonAsync<ConnectionsPage>($"/api/v1/hubs/{HubName}/connections");
        Assert.NotNull(page);
        Assert.Equal(1, page!.TotalCount);
        Assert.Null(page.NextContinuationToken);

        var summary = Assert.Single(page.Connections);
        Assert.Equal(open.ConnectionId, summary.ConnectionId);
        Assert.Equal("carol", summary.UserId);
        Assert.Equal("WebSockets", summary.Transport);
        Assert.Equal(["list-group"], summary.Groups);
    }

    [Fact]
    public async Task ListConnections_ToHubWithNoConnections_ReturnsEmptyPage()
    {
        await using var service = await StartWithManagementApiAsync();
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);

        var page = await http.GetFromJsonAsync<ConnectionsPage>("/api/v1/hubs/hub-with-no-connections/connections");
        Assert.NotNull(page);
        Assert.Empty(page!.Connections);
        Assert.Equal(0, page.TotalCount);
        Assert.Null(page.NextContinuationToken);
    }

    [Fact]
    public async Task ListConnections_Paginates_NoDuplicatesAcrossPages_AllConnectionsCovered()
    {
        await using var service = await StartWithManagementApiAsync();
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        await using var appServer = await AppServerDouble.ConnectAsync(service.ServerAddress, HubName, serverToken);

        const int connectionCount = 5;
        var clients = new List<HubConnection>();
        var expectedConnectionIds = new HashSet<string>();

        try
        {
            for (var i = 0; i < connectionCount; i++)
            {
                var userId = $"page-user-{i}";
                var token = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, userId, null, TimeSpan.FromMinutes(1));
                var client = BuildClient(service, token);
                clients.Add(client);
                await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var open = await AppServerDoubleWaits.WaitForOpenConnectionAsync(userId, appServer);
                expectedConnectionIds.Add(open.ConnectionId!);
            }

            using var http = new HttpClient { BaseAddress = service.ServerAddress };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);

            const int pageSize = 2;
            var seenConnectionIds = new List<string>();
            string? token2 = null;
            var reportedTotalCount = -1;
            var pageCount = 0;

            do
            {
                var url = $"/api/v1/hubs/{HubName}/connections?limit={pageSize}"
                    + (token2 is null ? "" : $"&continuationToken={Uri.EscapeDataString(token2)}");
                var page = await http.GetFromJsonAsync<ConnectionsPage>(url);
                Assert.NotNull(page);

                reportedTotalCount = page!.TotalCount;
                Assert.True(page.Connections.Count <= pageSize);
                seenConnectionIds.AddRange(page.Connections.Select(c => c.ConnectionId));

                token2 = page.NextContinuationToken;
                pageCount++;
                Assert.True(pageCount <= connectionCount, "Pagination did not terminate within the expected number of pages.");
            }
            while (token2 is not null);

            Assert.Equal(connectionCount, reportedTotalCount);
            // Disjoint and complete: no duplicates across pages, and the full set matches exactly.
            Assert.Equal(seenConnectionIds.Count, seenConnectionIds.Distinct().Count());
            Assert.Equal(expectedConnectionIds, seenConnectionIds.ToHashSet());
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
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
                options.Transports = HttpTransportType.WebSockets;
            })
            .Build();
    }

    /// <summary>Mirrors <c>Keryhe.Switchboard.Core.ConnectionsPage</c>'s wire shape for
    /// deserialization on the test side — a plain DTO, not the production type, so this test
    /// doesn't need a reference to <c>Keryhe.Switchboard.Core</c>'s internals beyond what
    /// System.Text.Json needs to bind.</summary>
    private sealed class ConnectionsPage
    {
        public List<ConnectionSummary> Connections { get; set; } = [];
        public int TotalCount { get; set; }
        public string? NextContinuationToken { get; set; }
    }

    private sealed class ConnectionSummary
    {
        public string ConnectionId { get; set; } = "";
        public string? UserId { get; set; }
        public string Transport { get; set; } = "";
        public DateTimeOffset ConnectedAt { get; set; }
        public List<string> Groups { get; set; } = [];
    }
}
