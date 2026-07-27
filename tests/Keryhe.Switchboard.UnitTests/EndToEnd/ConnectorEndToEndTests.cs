using System.Linq;
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
/// Slice 5 gate: app server + service both running; a hub method invoked by a real client
/// executes on the app server (via the real Connector, not a hand-rolled double) and its return
/// value reaches the client.
///
/// This is the only test that covers the inbound path all the way through: the hand-rolled
/// app-server double in ClientRouterEndToEndTests inspects the client_message envelope but never
/// feeds it to a real hub pipeline, and InboundDispatcherTests builds its own already-framed
/// payload rather than taking one off the wire. A regression in the framing the service puts on
/// client_message payloads is therefore invisible to both of those — it only shows up here.
/// </summary>
public class ConnectorEndToEndTests : IAsyncLifetime
{
    private RealKestrelServerFixture _service = null!;
    private WebApplication _appServer = null!;
    private Uri _appServerAddress = null!;

    public async Task InitializeAsync()
    {
        _service = new RealKestrelServerFixture();
        await _service.StartAsync();

        var tokenService = _service.Services.GetRequiredService<Keryhe.Switchboard.Core.ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-app-server", ["testHub"], TimeSpan.FromHours(1));

        var builder = WebApplication.CreateBuilder(["--urls", "http://127.0.0.1:0"]);
        builder.Services.AddSignalR();
        builder.Services.AddSwitchboardConnector(options =>
        {
            options.ServiceUrl = _service.ServerAddress.ToString();
            options.ServerAccessToken = serverToken;
            options.ServerConnectionsPerHub = 1;
        });

        _appServer = builder.Build();
        _appServer.MapHub<TestHub>("/testHub");

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

    [Fact]
    public async Task HubMethodInvokedByRealClient_ExecutesOnAppServer_AndReturnValueReachesClient()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/testHub"))
            .Build();

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var result = await connection.InvokeAsync<string>("Echo", "hello").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("echo:hello", result);
    }
}

public class TestHub : Hub
{
    public Task<string> Echo(string message) => Task.FromResult($"echo:{message}");
}
