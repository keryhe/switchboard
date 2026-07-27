using System.Net.Http.Json;
using System.Text.Json;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Server.Security;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Switchboard.IntegrationTests;

/// <summary>
/// Phase 1 milestone (06-project-plan.md § Definition of Done): SampleChatApp.Api connects to the
/// proxy; a .NET HubConnection negotiates through the API, connects to the proxy, and exchanges
/// hub method calls (including a server-initiated broadcast) end to end, as real out-of-process
/// hosts.
/// </summary>
public class MilestoneEndToEndTests : IAsyncLifetime
{
    private const string ServerSigningKey = "dev-only-server-signing-key-change-me-32+";
    private const string TokenSigningKey = "dev-only-client-signing-key-change-me-32+";
    private const string UserTokenSigningKey = "dev-only-sample-user-signing-key-change-me-32+";

    private ProcessFixture _service = null!;
    private ProcessFixture _api = null!;

    public async Task InitializeAsync()
    {
        var servicePort = GetFreeTcpPort();
        var apiPort = GetFreeTcpPort();
        var serviceUrl = $"http://127.0.0.1:{servicePort}";
        var apiUrl = $"http://127.0.0.1:{apiPort}";

        var serverToken = IssueServerToken();

        _service = new ProcessFixture(FindAssembly("Keryhe.Switchboard.Server"), servicePort, new Dictionary<string, string>
        {
            ["Switchboard__PublicUrl"] = serviceUrl,
            ["Switchboard__TokenSigningKey"] = TokenSigningKey,
            ["Switchboard__ServerSigningKey"] = ServerSigningKey,
        });
        await _service.StartAsync("/healthz", TimeSpan.FromSeconds(30));

        _api = new ProcessFixture(FindAssembly("SampleChatApp.Api"), apiPort, new Dictionary<string, string>
        {
            ["Switchboard__Url"] = serviceUrl,
            ["Switchboard__ServerToken"] = serverToken,
            ["Auth__UserTokenSigningKey"] = UserTokenSigningKey,
        });
        await _api.StartAsync("/api/auth/login", TimeSpan.FromSeconds(30));

        // The API's Connector needs its own real time to connect to the service before a client
        // can successfully negotiate (D5: the service fails negotiate fast with 503 otherwise).
        using var readyClient = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var health = await readyClient.GetAsync($"{_service.BaseUrl}/healthz");
            if (health.IsSuccessStatusCode)
            {
                break;
            }

            await Task.Delay(250);
        }
    }

    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        await _service.DisposeAsync();
    }

    [Fact]
    public async Task SampleChatApp_ConnectsThroughProxy_AndExchangesMessagesEndToEnd()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_api.BaseUrl) };
        var loginResponse = await http.PostAsJsonAsync("api/auth/login", new { Username = "alice" });
        loginResponse.EnsureSuccessStatusCode();
        var login = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
        var userToken = login.RootElement.GetProperty("accessToken").GetString()!;

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(_api.BaseUrl), "/chatHub"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(userToken);
            })
            .Build();

        var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("Connected", id => connected.SetResult(id));

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("ReceiveMessage", msg => received.SetResult(msg));

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        // Server-initiated push: ChatHub.OnConnectedAsync does Clients.Caller.SendAsync("Connected").
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Client-invoked hub methods. Awaiting InvokeAsync (rather than SendAsync) is the real
        // assertion here: it only returns once the hub method has actually executed on the app
        // server and its completion message has travelled back
        // app server -> service -> client as a send_to_connection. That is the full inbound
        // dispatch round trip through the synthetic connection.
        await connection.InvokeAsync("JoinRoom", "room1").WaitAsync(TimeSpan.FromSeconds(10));
        await connection.InvokeAsync("SendMessage", "room1", "hello").WaitAsync(TimeSpan.FromSeconds(10));

        // ChatHub.SendMessage fans out via Clients.Group(roomId). Phase 1 routed group sends
        // nowhere (plan decision D3, a deliberate log-and-no-op); Phase 2 Slice 1 implements real
        // group fan-out, so the message the room's own member sent must now come back to it.
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("hello", message.GetProperty("text").GetString());

        await connection.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static string IssueServerToken()
    {
        var options = Options.Create(new SwitchboardOptions
        {
            PublicUrl = "unused",
            TokenSigningKey = TokenSigningKey,
            ServerSigningKey = ServerSigningKey,
        });

        return new JwtTokenService(options).IssueServerToken("sample-chat-app-api", ["chatHub"], TimeSpan.FromHours(1));
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
            throw new FileNotFoundException($"Assembly not found at {full}. Build the solution before running integration tests.", full);
        }

        return full;
    }
}
