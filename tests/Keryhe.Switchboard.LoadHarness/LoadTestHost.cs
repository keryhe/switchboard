using System.Net.Sockets;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Server.Security;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.LoadHarness;

/// <summary>
/// Spins up a real out-of-process <c>Keryhe.Switchboard.Server</c> + <c>SampleChatApp.Api</c> pair
/// for a load run, with the management API enabled (needed for <see cref="FanOutLoad"/>'s
/// broadcast trigger) — the same shape
/// <c>tests/Keryhe.Switchboard.CompatibilityTests/SampleAppHost.cs</c> uses for compatibility
/// testing, standalone here since this project has no xunit dependency.
/// </summary>
public sealed class LoadTestHost : IAsyncDisposable
{
    private const string ServerSigningKey = "dev-only-load-harness-server-signing-key-change-me-32+";
    private const string TokenSigningKey = "dev-only-load-harness-client-signing-key-change-me-32+";
    private const string UserTokenSigningKey = "dev-only-load-harness-user-signing-key-change-me-32+";
    private const string ManagementSigningKey = "dev-only-load-harness-management-signing-key-change-me-32+";

    public ManagedProcess Service { get; }
    public ManagedProcess Api { get; }
    public string ManagementToken { get; }

    private LoadTestHost(ManagedProcess service, ManagedProcess api, string managementToken)
    {
        Service = service;
        Api = api;
        ManagementToken = managementToken;
    }

    public static async Task<LoadTestHost> StartAsync(string? otlpEndpoint = null)
    {
        var servicePort = GetFreeTcpPort();
        var apiPort = GetFreeTcpPort();
        var serviceUrl = $"http://127.0.0.1:{servicePort}";
        var apiUrl = $"http://127.0.0.1:{apiPort}";

        var serverToken = IssueServerToken();
        var managementToken = IssueManagementToken();

        var serviceEnv = new Dictionary<string, string>
        {
            ["Switchboard__PublicUrl"] = serviceUrl,
            ["Switchboard__TokenSigningKey"] = TokenSigningKey,
            ["Switchboard__ServerSigningKey"] = ServerSigningKey,
            ["Switchboard__EnableManagementApi"] = "true",
            ["Switchboard__ManagementSigningKey"] = ManagementSigningKey,
        };

        if (otlpEndpoint is not null)
        {
            serviceEnv["Switchboard__OtlpEndpoint"] = otlpEndpoint;
            // 1s rather than the OTEL SDK's 60s default export cadence — matches
            // Phase4MilestoneEndToEndTests's own precedent for the identical reason: a scrape
            // taken moments after the fan-out load finishes must not race a minute-long export
            // interval, or the cross-check reads stale (empty) histogram data.
            serviceEnv["OTEL_METRIC_EXPORT_INTERVAL"] = "1000";
        }

        var service = new ManagedProcess(FindAssembly("Keryhe.Switchboard.Server"), serviceUrl, serviceEnv);
        await service.StartAsync("/healthz", TimeSpan.FromSeconds(30));

        var api = new ManagedProcess(FindAssembly("SampleChatApp.Api"), apiUrl, new Dictionary<string, string>
        {
            ["Switchboard__Url"] = serviceUrl,
            ["Switchboard__ServerToken"] = serverToken,
            ["Auth__UserTokenSigningKey"] = UserTokenSigningKey,
        });
        await api.StartAsync("/api/auth/login", TimeSpan.FromSeconds(30));

        // The API's Connector needs its own real time to connect to the service before any
        // client can successfully negotiate (D5 fails negotiate fast with 503 otherwise).
        using var readyClient = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var health = await readyClient.GetAsync($"{serviceUrl}/healthz");
            if (health.IsSuccessStatusCode)
            {
                break;
            }

            await Task.Delay(250);
        }

        return new LoadTestHost(service, api, managementToken);
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

        return new JwtTokenService(options).IssueServerToken("load-harness-sample-chat-app", ["chatHub"], TimeSpan.FromHours(2));
    }

    private static string IssueManagementToken()
    {
        var options = Options.Create(new SwitchboardOptions
        {
            PublicUrl = "unused",
            TokenSigningKey = TokenSigningKey,
            ServerSigningKey = ServerSigningKey,
            ManagementSigningKey = ManagementSigningKey,
        });

        return new JwtTokenService(options).IssueManagementToken("load-harness", TimeSpan.FromHours(2));
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Mirrors the running configuration (Debug/Release) of this harness itself onto the
    /// sibling projects it spawns — a load run is meant to be executed via
    /// <c>dotnet run -c Release</c>, so the assemblies it spawns must be the Release builds too.</summary>
    private static string FindAssembly(string projectName)
    {
        var configuration = AppContext.BaseDirectory.Contains("/Release/", StringComparison.Ordinal) ? "Release" : "Debug";
        var candidate = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            projectName.StartsWith("SampleChatApp", StringComparison.Ordinal) ? "samples/SampleChatApp" : "src",
            projectName, "bin", configuration, "net10.0", $"{projectName}.dll");
        var full = Path.GetFullPath(candidate);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(
                $"Assembly not found at {full}. Build the solution in {configuration} configuration before running the load harness.", full);
        }

        return full;
    }
}
