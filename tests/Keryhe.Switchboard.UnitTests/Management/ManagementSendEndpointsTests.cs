using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Management;

/// <summary>
/// Phase 4 Slice 2 gate (plans/phase-4-management-and-observability.md §4, plan decision D22):
/// <c>ManagementInvocationWriter</c>'s <see cref="System.Text.Json.JsonElement"/>-to-primitive
/// mapping is exercised against a real MessagePack client, not just JSON — finding 4's failure
/// mode (arguments silently arriving as empty maps) produces no exception and would pass against a
/// JSON-only assertion, since <c>JsonHubProtocol</c> happens to special-case
/// <see cref="System.Text.Json.JsonElement"/> while <c>MessagePackHubProtocol</c> does not.
/// </summary>
public class ManagementSendEndpointsTests
{
    private const string HubName = "chatHub-management-send-e2e";
    private const string ManagementSigningKey = "dev-only-management-signing-key-change-me-32+";

    [Fact]
    public async Task Broadcast_ReachesBothJsonAndMessagePackClients_WithCorrectArgumentValues()
    {
        await using var service = await StartWithManagementApiAsync();
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        await using var appServer = await AppServerDouble.ConnectAsync(service.ServerAddress, HubName, serverToken);

        var jsonTokenValue = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        var mpTokenValue = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "bob", null, TimeSpan.FromMinutes(1));

        await using var jsonClient = BuildClient(service, jsonTokenValue, useMessagePack: false);
        await using var mpClient = BuildClient(service, mpTokenValue, useMessagePack: true);

        var jsonReceived = new TaskCompletionSource<object[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mpReceived = new TaskCompletionSource<object[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        jsonClient.On<string, long, bool, string?, object>("ReceiveMessage", (a, b, c, d, e) =>
            jsonReceived.TrySetResult([a, b, c, d!, e]));
        mpClient.On<string, long, bool, string?, object>("ReceiveMessage", (a, b, c, d, e) =>
            mpReceived.TrySetResult([a, b, c, d!, e]));

        await jsonClient.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServer.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));
        await mpClient.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServer.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);

        var body = new
        {
            target = "ReceiveMessage",
            arguments = new object?[]
            {
                "System",
                42,
                true,
                null,
                new { a = 1, b = new[] { 1, 2 } },
            },
        };
        var response = await http.PostAsJsonAsync($"/api/v1/hubs/{HubName}/send", body);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var jsonArgs = await jsonReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var mpArgs = await mpReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        AssertArgumentValues(jsonArgs);
        AssertArgumentValues(mpArgs);

        // Assert absence: a management broadcast bypasses hub code and app servers entirely — the
        // only envelope the app server ever saw is the client's own OpenConnection registration,
        // never a ClientMessage or any dispatch triggered by this send (plan decision D22).
        Assert.DoesNotContain(appServer.ReceivedEnvelopes, e => e.Type == ServerEnvelopeType.ClientMessage);
    }

    [Fact]
    public async Task Broadcast_ToHubWithNoConnections_StillReturns202()
    {
        await using var service = await StartWithManagementApiAsync();
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);

        var response = await http.PostAsJsonAsync(
            "/api/v1/hubs/hub-with-no-connections/send",
            new { target = "ReceiveMessage", arguments = new object[] { "System", "nobody's listening" } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task SendToUser_ReachesOnlyThatUsersConnection()
    {
        await using var service = await StartWithManagementApiAsync();
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        await using var appServer = await AppServerDouble.ConnectAsync(service.ServerAddress, HubName, serverToken);

        var daveToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "dave", null, TimeSpan.FromMinutes(1));
        var eveToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "eve", null, TimeSpan.FromMinutes(1));

        await using var daveClient = BuildClient(service, daveToken, useMessagePack: false);
        await using var eveClient = BuildClient(service, eveToken, useMessagePack: false);

        var daveReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var eveReceived = false;
        daveClient.On<string, string>("ReceiveMessage", (_, text) => daveReceived.TrySetResult(text));
        eveClient.On<string, string>("ReceiveMessage", (_, _) => eveReceived = true);

        await daveClient.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServer.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));
        await eveClient.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServer.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);
        var response = await http.PostAsJsonAsync(
            $"/api/v1/hubs/{HubName}/users/dave/send",
            new { target = "ReceiveMessage", arguments = new object[] { "System", "for-dave-only" } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Assert.Equal("for-dave-only", await daveReceived.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(eveReceived);
    }

    [Fact]
    public async Task SendToGroup_ReachesOnlyGroupMembers()
    {
        await using var service = await StartWithManagementApiAsync();
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        var managementToken = tokenService.IssueManagementToken("ops-dashboard", TimeSpan.FromHours(1));

        await using var appServer = await AppServerDouble.ConnectAsync(service.ServerAddress, HubName, serverToken);

        var memberToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "frank", null, TimeSpan.FromMinutes(1));
        var nonMemberToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "grace", null, TimeSpan.FromMinutes(1));

        await using var memberClient = BuildClient(service, memberToken, useMessagePack: false);
        await using var nonMemberClient = BuildClient(service, nonMemberToken, useMessagePack: false);

        var memberReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nonMemberReceived = false;
        memberClient.On<string, string>("ReceiveMessage", (_, text) => memberReceived.TrySetResult(text));
        nonMemberClient.On<string, string>("ReceiveMessage", (_, _) => nonMemberReceived = true);

        await memberClient.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var openMember = await appServer.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));
        await nonMemberClient.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServer.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));

        using var http = new HttpClient { BaseAddress = service.ServerAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);

        var addResponse = await http.PutAsync($"/api/v1/hubs/{HubName}/groups/send-group/connections/{openMember.ConnectionId}", content: null);
        Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);

        var sendResponse = await http.PostAsJsonAsync(
            $"/api/v1/hubs/{HubName}/groups/send-group/send",
            new { target = "ReceiveMessage", arguments = new object[] { "System", "for-group-only" } });
        Assert.Equal(HttpStatusCode.Accepted, sendResponse.StatusCode);

        Assert.Equal("for-group-only", await memberReceived.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(nonMemberReceived);
    }

    private static void AssertArgumentValues(object[] args)
    {
        Assert.Equal("System", args[0]);
        Assert.Equal(42L, args[1]);
        Assert.Equal(true, args[2]);
        Assert.Null(args[3]);

        var nested = Unwrap(args[4]) as IDictionary<string, object?>;
        Assert.NotNull(nested);
        Assert.Equal(1L, nested!["a"]);
        var b = Unwrap(nested["b"]) as IEnumerable<object?>;
        Assert.NotNull(b);
        Assert.Equal([1L, 2L], b!.ToList());
    }

    /// <summary>
    /// Normalizes a deserialized argument value to plain CLR primitives/collections regardless of
    /// which hub protocol produced it — JSON deserializes an <c>object</c>-typed argument to
    /// <see cref="System.Text.Json.JsonElement"/>, while MessagePack's contractless resolver
    /// produces boxed primitives and <see cref="IDictionary{TKey,TValue}"/>/<see cref="IEnumerable{T}"/>
    /// directly. The test only needs to assert the same logical values arrived through both, not
    /// which concrete runtime type carried them.
    /// </summary>
    private static object? Unwrap(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case System.Text.Json.JsonElement je:
                return je.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => je.GetString(),
                    System.Text.Json.JsonValueKind.Number => je.TryGetInt64(out var l) ? (object)l : je.GetDouble(),
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    System.Text.Json.JsonValueKind.Null => null,
                    System.Text.Json.JsonValueKind.Array => je.EnumerateArray().Select(e => Unwrap(e)).ToList(),
                    System.Text.Json.JsonValueKind.Object => je.EnumerateObject().ToDictionary(p => p.Name, p => Unwrap(p.Value)),
                    _ => null,
                };
            case string s:
                return s;
            case System.Collections.IDictionary dict:
            {
                var result = new Dictionary<string, object?>();
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    result[entry.Key.ToString()!] = Unwrap(entry.Value);
                }

                return result;
            }
            case System.Collections.IEnumerable seq:
            {
                var list = new List<object?>();
                foreach (var item in seq)
                {
                    list.Add(Unwrap(item));
                }

                return list;
            }
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return Convert.ToInt64(value);
            case float or double:
                return Convert.ToDouble(value);
            default:
                return value;
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

    private static HubConnection BuildClient(RealKestrelServerFixture service, string clientToken, bool useMessagePack)
    {
        var url = new Uri(service.ServerAddress, $"/{HubName}");
        var builder = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(clientToken);
                options.Transports = HttpTransportType.WebSockets;
            });

        if (useMessagePack)
        {
            builder.AddMessagePackProtocol();
        }

        return builder.Build();
    }
}
