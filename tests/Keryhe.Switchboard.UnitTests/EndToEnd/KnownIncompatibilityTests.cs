using System.Security.Claims;
using Keryhe.Switchboard.Connector;
using Keryhe.Switchboard.Connector.ServerConnections;
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
/// Phase 5 Slice 3 gate (plan decision D31): three incompatibilities that were previously prose
/// only — in a risk register, a design-doc note, or nowhere at all — become executable assertions.
/// Prose does not fail a build when the described behavior silently changes; these tests do.
/// Lives beside <see cref="ConnectorEndToEndTests"/> (both need the real Connector wired against a
/// real in-process Kestrel service, via <see cref="RealKestrelServerFixture"/>), a deliberate
/// deviation from the plan's original sketch of putting D31/D32 tests under
/// tests/Keryhe.Switchboard.CompatibilityTests — that project has no Connector reference or
/// in-process host fixture, and duplicating both here would just be churn.
/// </summary>
public class KnownIncompatibilityTests : IAsyncLifetime
{
    private RealKestrelServerFixture _service = null!;
    private WebApplication _appServer = null!;
    private Uri _appServerAddress = null!;

    public async Task InitializeAsync()
    {
        _service = new RealKestrelServerFixture();
        await _service.StartAsync();

        var tokenService = _service.Services.GetRequiredService<Keryhe.Switchboard.Core.ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-app-server", ["incompatHub"], TimeSpan.FromHours(1));

        var builder = WebApplication.CreateBuilder(["--urls", "http://127.0.0.1:0"]);
        builder.Services.AddSignalR();
        // D31's IUserIdProvider assertion needs a custom provider that computes a *different* id
        // than the one the service's user index uses (04-design.md §11 "Identity reconstruction")
        // — registered after AddSignalR() so it overrides the framework's TryAddSingleton default.
        builder.Services.AddSingleton<IUserIdProvider, PrefixedUserIdProvider>();
        builder.Services.AddSwitchboardConnector(options =>
        {
            options.ServiceUrl = _service.ServerAddress.ToString();
            options.ServerAccessToken = serverToken;
            options.ServerConnectionsPerHub = 1;
        });

        _appServer = builder.Build();

        // A minimal stand-in for real JWT bearer auth (which SampleChatApp.Api uses) — negotiate
        // identity forwarding (HttpNegotiateRedirectHandler) reads context.User off the app
        // server's own negotiate request, so every request needs an authenticated principal for
        // the IUserIdProvider-divergence test to have anything to diverge from.
        _appServer.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, RealUserId)], "TestAuth"));
            await next();
        });

        _appServer.MapHub<KnownIncompatibilityHub>("/incompatHub");

        await _appServer.StartAsync();
        var addresses = _appServer.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        _appServerAddress = new Uri(addresses.First());

        var poolRegistry = _appServer.Services.GetRequiredService<ConnectorConnectionPoolRegistry>();
        using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!poolRegistry.All.Any())
        {
            readyCts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20);
        }

        await poolRegistry.All.Single().WaitForFirstConnectionAsync(readyCts.Token);
    }

    public async Task DisposeAsync()
    {
        await _appServer.StopAsync();
        await _appServer.DisposeAsync();
        await _service.DisposeAsync();
    }

    private const string RealUserId = "alice";

    /// <summary>
    /// 04-design.md §11: "There is no IHttpContextFeature — no HTTP request exists on the app
    /// server for this connection." If a future change to <c>SwitchboardClientConnectionContext</c>
    /// accidentally adds one, this test starts failing loudly instead of the doc and the code
    /// silently disagreeing.
    /// </summary>
    [Fact]
    public async Task GetHttpContext_IsAlwaysNull_OnASyntheticConnection()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/incompatHub"))
            .Build();

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var isNull = await connection.InvokeAsync<bool>("GetHttpContextIsNull").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(isNull);
    }

    /// <summary>
    /// 04-design.md §11 "Identity reconstruction": a custom <c>IUserIdProvider</c> that derives
    /// the id from something other than the raw <c>userId</c> the service captured at negotiate
    /// makes <c>Context.UserIdentifier</c> diverge from the service's own user index — silently.
    /// <see cref="PrefixedUserIdProvider"/> computes <c>"prefixed-alice"</c> while the service's
    /// index still has plain <c>"alice"</c> (the id forwarded via X-Switchboard-UserId in
    /// InitializeAsync's auth stand-in). A send to the real id reaches the client; a send to the
    /// app-server-computed id — exactly what naive hub code using <c>Context.UserIdentifier</c>
    /// would target — silently goes nowhere.
    /// </summary>
    [Fact]
    public async Task CustomUserIdProvider_DivergesFromServicesUserIndex_SilentlyDropsSends()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/incompatHub"))
            .Build();

        var toReal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("ToRealId", text => toReal.SetResult(text));

        var toComputed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("ToComputedId", text => toComputed.SetResult(text));

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        await connection.InvokeAsync("SendToUserByRealId", RealUserId).WaitAsync(TimeSpan.FromSeconds(10));

        var realText = await toReal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("sent-to-real-id", realText);

        var computedGotIt = await Task.WhenAny(toComputed.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.False(ReferenceEquals(computedGotIt, toComputed.Task),
            "a send to the app-server-computed user id should not reach the client — that's the divergence this test pins");
    }

    /// <summary>
    /// ADR-005 § What Is Not In Scope: stateful reconnect is a non-goal, but "a client that
    /// requests it against this service simply falls back to standard reconnect ... the connection
    /// is not broken." That promise had never actually been exercised against a real client.
    /// </summary>
    [Fact]
    public async Task StatefulReconnectRequest_FallsBackGracefully_ConnectionStillWorks()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/incompatHub"))
            .WithStatefulReconnect()
            .Build();

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(HubConnectionState.Connected, connection.State);

        var isNull = await connection.InvokeAsync<bool>("GetHttpContextIsNull").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(isNull);
    }
}

/// <summary>Computes a deliberately different id than the raw <c>NameIdentifier</c> claim, so
/// <see cref="KnownIncompatibilityTests.CustomUserIdProvider_DivergesFromServicesUserIndex_SilentlyDropsSends"/>
/// can demonstrate the documented divergence (04-design.md §11).</summary>
public sealed class PrefixedUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        var raw = connection.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return raw is null ? null : $"prefixed-{raw}";
    }
}

public class KnownIncompatibilityHub : Hub
{
    public Task<bool> GetHttpContextIsNull() => Task.FromResult(Context.GetHttpContext() is null);

    public async Task SendToUserByRealId(string realUserId)
    {
        await Clients.User(realUserId).SendAsync("ToRealId", "sent-to-real-id");
        await Clients.User(Context.UserIdentifier!).SendAsync("ToComputedId", "sent-to-computed-id");
    }
}
