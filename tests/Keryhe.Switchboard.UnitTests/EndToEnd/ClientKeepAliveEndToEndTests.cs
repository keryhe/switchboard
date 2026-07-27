using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Slice 9 finding: <see cref="Keryhe.Switchboard.Core.Models.SwitchboardOptions.ClientKeepAliveInterval"/>
/// was declared but never actually wired to anything — the service never sent a client a
/// hub-level Ping, so any idle-but-healthy connection (no chat activity for 30s, the real
/// <c>HubConnection</c>'s default <c>ServerTimeout</c>) was torn down by the client as dead and
/// silently reconnected. Every prior end-to-end test in this project completes in well under a
/// second, so this was never exercised until the Angular sample sat idle in a real browser.
/// Fixed in <see cref="Keryhe.Switchboard.Server.ClientConnections.ClientConnectionLifecycle.RunAsync"/>
/// via a periodic keep-alive Ping loop, symmetric to the server connection's own
/// <c>RunPingLoopAsync</c>. This test pins it with a short interval/timeout rather than waiting out
/// the real 15s/30s defaults.
/// </summary>
public class ClientKeepAliveEndToEndTests : IAsyncLifetime
{
    private RealKestrelServerFixture _factory = null!;
    private const string HubName = "chatHub-keepalive-e2e";

    public async Task InitializeAsync()
    {
        _factory = new RealKestrelServerFixture();
        await _factory.StartAsync("--Switchboard:ClientKeepAliveInterval", "00:00:00.200");
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IdleConnection_StaysConnected_PastItsServerTimeout_BecauseOfPeriodicPing()
    {
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));
        await using var appServerDouble = await AppServerDouble.ConnectAsync(_factory.ServerAddress, HubName, serverToken);

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        var url = new Uri(_factory.ServerAddress, $"/{HubName}");
        await using var connection = new HubConnectionBuilder()
            .WithUrl(url, options => options.AccessTokenProvider = () => Task.FromResult<string?>(clientToken))
            .Build();

        // Far shorter than the real 30s default, so the test doesn't need to wait that long —
        // still comfortably longer than the 200ms keep-alive interval configured above.
        connection.ServerTimeout = TimeSpan.FromSeconds(1);

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            disconnected.TrySetResult();
            return Task.CompletedTask;
        };

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Idle for well past ServerTimeout, sending nothing ourselves — only the service's
        // periodic Ping should be keeping this alive.
        var wentIdleWithoutClosing = await Task.WhenAny(disconnected.Task, Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.NotEqual(disconnected.Task, wentIdleWithoutClosing);
        Assert.Equal(HubConnectionState.Connected, connection.State);
    }
}
