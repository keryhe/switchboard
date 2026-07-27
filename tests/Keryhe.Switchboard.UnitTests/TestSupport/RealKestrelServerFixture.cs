using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Keryhe.Switchboard.UnitTests.TestSupport;

/// <summary>
/// Real .NET SignalR clients (<c>HubConnection</c>) open actual OS sockets for the WebSocket
/// transport, so they cannot be driven against <c>WebApplicationFactory</c>'s default in-memory
/// <c>TestServer</c> (which WebApplicationFactory wires in regardless of a <c>CreateHost</c>
/// override). This fixture instead boots the real <see cref="Keryhe.Switchboard.Server.Program"/>
/// composition directly via a real Kestrel server on an OS-assigned loopback port.
/// </summary>
public sealed class RealKestrelServerFixture : IAsyncDisposable
{
    private WebApplication? _app;

    public Uri ServerAddress { get; private set; } = null!;
    public IServiceProvider Services => _app!.Services;

    public async Task StartAsync(params string[] extraArgs)
    {
        // PublicUrl must be known up front (it's baked into every negotiate redirect response),
        // so a real port is picked before Kestrel binds rather than discovered afterwards.
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var args = new[]
        {
            "--urls", url,
            "--Switchboard:PublicUrl", url,
            "--Switchboard:TokenSigningKey", "dev-only-client-signing-key-change-me-32+",
            "--Switchboard:ServerSigningKey", "dev-only-server-signing-key-change-me-32+",
        }.Concat(extraArgs).ToArray();

        _app = Keryhe.Switchboard.Server.Program.BuildApp(args);
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        ServerAddress = new Uri(addresses.First());
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
