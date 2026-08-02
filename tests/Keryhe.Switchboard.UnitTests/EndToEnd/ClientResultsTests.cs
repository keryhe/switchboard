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
/// Phase 5 Slice 3 gate (plan decision D32, finding 2): hub code calling
/// <c>Clients.Client(id).InvokeAsync&lt;T&gt;(...)</c> (SignalR client results, a first-class
/// feature since .NET 8) must surface a Switchboard-specific error naming the limitation and
/// pointing at where it's documented — never the framework's bare
/// <c>NotImplementedException: &lt;T&gt; does not support client return values.</c>, which doesn't
/// even mention Switchboard. See <see cref="Keryhe.Switchboard.SwitchboardHubLifetimeManager{THub}"/>'s
/// <c>InvokeConnectionAsync</c>/<c>SetConnectionResultAsync</c> overrides.
/// </summary>
public class ClientResultsTests : IAsyncLifetime
{
    private RealKestrelServerFixture _service = null!;
    private WebApplication _appServer = null!;
    private Uri _appServerAddress = null!;

    public async Task InitializeAsync()
    {
        _service = new RealKestrelServerFixture();
        await _service.StartAsync();

        var tokenService = _service.Services.GetRequiredService<Keryhe.Switchboard.Core.ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-app-server", ["clientResultsHub"], TimeSpan.FromHours(1));

        var builder = WebApplication.CreateBuilder(["--urls", "http://127.0.0.1:0"]);
        // Detailed errors are what let the client observe the actual NotSupportedException message
        // rather than SignalR's generic "An unexpected error occurred invoking the hub method" —
        // needed here specifically to assert the message names Switchboard; off by default in
        // production, matching every other end-to-end suite in this repo.
        builder.Services.AddSignalR(options => options.EnableDetailedErrors = true);
        builder.Services.AddSwitchboardConnector(options =>
        {
            options.ServiceUrl = _service.ServerAddress.ToString();
            options.ServerAccessToken = serverToken;
            options.ServerConnectionsPerHub = 1;
        });

        _appServer = builder.Build();
        _appServer.MapHub<ClientResultsHub>("/clientResultsHub");

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
    public async Task HubCallingClientResults_SurfacesASwitchboardSpecificError_NotABareNotImplementedException()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/clientResultsHub"))
            .Build();

        // The hub method never actually asks this client anything (client results aren't
        // reachable at all), so nothing needs to answer — the assertion is entirely about what
        // the *invoking* client observes when the hub method it called throws.
        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var ex = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync<int>("TryClientResult").WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("Switchboard", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not support client return values", ex.Message, StringComparison.Ordinal);
    }
}

public class ClientResultsHub : Hub
{
    public async Task<int> TryClientResult()
    {
        // Deliberately targets the caller's own connection — the specific connectionId is
        // irrelevant, since SwitchboardHubLifetimeManager.InvokeConnectionAsync throws before it
        // ever looks at it.
        return await Clients.Client(Context.ConnectionId).InvokeAsync<int>("Ping", CancellationToken.None);
    }
}
