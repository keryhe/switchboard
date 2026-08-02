using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.IntegrationTests;
using Keryhe.Switchboard.Server.Security;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.CompatibilityTests;

/// <summary>
/// Spins up a real out-of-process <c>Keryhe.Switchboard.Server</c> + <c>SampleChatApp.Api</c> pair
/// — the same shape <c>MilestoneEndToEndTests</c> and <see cref="CompatibilityMatrixTests"/> use —
/// factored out so every probe language's test class (.NET 8, JS, Java) doesn't re-derive it.
/// </summary>
public sealed class SampleAppHost : IAsyncDisposable
{
    private const string ServerSigningKey = "dev-only-compat-server-signing-key-change-me-32+";
    private const string TokenSigningKey = "dev-only-compat-client-signing-key-change-me-32+";
    private const string UserTokenSigningKey = "dev-only-compat-user-signing-key-change-me-32+";

    public ProcessFixture Service { get; }
    public ProcessFixture Api { get; }

    private SampleAppHost(ProcessFixture service, ProcessFixture api)
    {
        Service = service;
        Api = api;
    }

    /// <param name="forContainerizedClient">
    /// When true, the negotiate redirect's <c>PublicUrl</c> is advertised as
    /// <c>http://host.docker.internal:{port}</c> instead of <c>http://127.0.0.1:{port}</c> — a
    /// container (the Java probe, via <see cref="JavaClientContainerFixture"/>) cannot resolve
    /// <c>127.0.0.1</c> in a redirect response as "the host machine" the way Docker Desktop's
    /// magic <c>host.docker.internal</c> hostname does. The bind address stays loopback either
    /// way (verified reachable from a container through Docker Desktop's proxy on this host) —
    /// only the URL embedded in service responses changes.
    /// </param>
    public static async Task<SampleAppHost> StartAsync(bool forContainerizedClient = false)
    {
        var servicePort = GetFreeTcpPort();
        var apiPort = GetFreeTcpPort();
        var serviceUrl = $"http://127.0.0.1:{servicePort}";
        var publicUrl = forContainerizedClient ? $"http://host.docker.internal:{servicePort}" : serviceUrl;

        var serverToken = IssueServerToken();

        var service = new ProcessFixture(FindAssembly("Keryhe.Switchboard.Server"), servicePort, new Dictionary<string, string>
        {
            ["Switchboard__PublicUrl"] = publicUrl,
            ["Switchboard__TokenSigningKey"] = TokenSigningKey,
            ["Switchboard__ServerSigningKey"] = ServerSigningKey,
        });
        await service.StartAsync("/healthz", TimeSpan.FromSeconds(30));

        var api = new ProcessFixture(FindAssembly("SampleChatApp.Api"), apiPort, new Dictionary<string, string>
        {
            ["Switchboard__Url"] = serviceUrl,
            ["Switchboard__ServerToken"] = serverToken,
            ["Auth__UserTokenSigningKey"] = UserTokenSigningKey,
        });
        await api.StartAsync("/api/auth/login", TimeSpan.FromSeconds(30));

        // Mirrors MilestoneEndToEndTests: the Connector needs its own real time to connect to the
        // service before a client can successfully negotiate (D5 fails negotiate fast otherwise).
        using var readyClient = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var health = await readyClient.GetAsync($"{service.BaseUrl}/healthz");
            if (health.IsSuccessStatusCode)
            {
                break;
            }

            await Task.Delay(250);
        }

        return new SampleAppHost(service, api);
    }

    public async ValueTask DisposeAsync()
    {
        await Api.DisposeAsync();
        await Service.DisposeAsync();
    }

    private static string IssueServerToken()
    {
        var options = Options.Create(new SwitchboardOptions
        {
            PublicUrl = "unused",
            TokenSigningKey = TokenSigningKey,
            ServerSigningKey = ServerSigningKey,
        });

        return new JwtTokenService(options).IssueServerToken("compat-sample-chat-app", ["chatHub"], TimeSpan.FromHours(1));
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindAssembly(string projectName)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            projectName.StartsWith("SampleChatApp", StringComparison.Ordinal) ? "samples/SampleChatApp" : "src",
            projectName, "bin", "Debug", "net10.0", $"{projectName}.dll");
        var full = Path.GetFullPath(candidate);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Assembly not found at {full}. Build the solution before running compatibility tests.", full);
        }

        return full;
    }
}
