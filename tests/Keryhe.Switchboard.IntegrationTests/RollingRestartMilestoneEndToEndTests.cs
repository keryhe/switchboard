using System.Net.Http.Json;
using System.Text.Json;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Server.Security;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Keryhe.Switchboard.IntegrationTests;

/// <summary>
/// The Phase 3 milestone (plans/phase-3-scale-out-and-resilience.md § Slice 7): "restart node A
/// while a client connected to node A is live; it reconnects ... and continues receiving group
/// messages; a client on node B is unaffected throughout; broadcasts keep flowing across nodes for
/// the duration." Two real out-of-process <c>Keryhe.Switchboard.Server</c> hosts clustering through
/// a real throwaway Postgres container (<see cref="PostgresContainerFixture"/>, Phase 3 Slice 6) —
/// <c>UseLocalhostClustering</c>'s static in-memory membership table lives inside one process's
/// memory and cannot form a cluster across two genuinely separate <c>dotnet</c> processes the way
/// this milestone needs, unlike the in-process two-silo unit tests elsewhere in this repo.
///
/// <para>
/// Two real out-of-process <c>SampleChatApp.Api</c> instances, one per node — a deliberate widening
/// of the plan's "plus SampleChatApp.Api" (singular). A single app-server instance has one fixed
/// <c>Switchboard:Url</c>, so it can only ever give a real, negotiated client a connection to
/// whichever node that URL names; the milestone specifically needs a real client whose whole
/// negotiated flow — not a hand-issued token — goes through node A. Two app-server instances,
/// pointed one at each node, are what makes that possible without a load balancer standing in front
/// of them (this repo ships none — deploying one is the operator's job, see docs/docs/01-overview.md's
/// non-goals). It also happens to be exactly what Phase 3 Slice 4's own gate describes: "a node
/// holding zero server connections for a hub still completes the full flow" — before this test
/// restarts anything, node A's only server connection is api-a's, so every one of clientA's
/// messages already round-trips node A → (cluster-wide assignment) → node B or node A depending on
/// which app server the least-loaded pick lands on, exercising the exact cross-node dispatch path
/// this phase exists to prove.
/// </para>
///
/// <para>
/// Scoping note on "reconnects ... against a surviving node": a client's <c>HubConnectionBuilder.WithUrl</c>
/// is fixed to api-a's address for the lifetime of clientA's <see cref="HubConnection"/> — SignalR's
/// automatic reconnect always re-negotiates against the original target, not some other address, and
/// this repo has no LB to redirect that address elsewhere. What this test actually proves — and what
/// the milestone's underlying claim really is (see the plan's own "what 'the reconnect window' means
/// here" note: there is no server-side session to resume, a reconnect is an ordinary fresh negotiate)
/// — is the server-side half: node A's restart destroys no cluster state that a fresh negotiate
/// needs, node A rejoins the same cluster and resumes serving (via its own app server's reconnected
/// pool, or node B's, whichever cluster-wide assignment picks) with no operator intervention, and
/// node B and every client that never touched node A are completely unaffected for the entire
/// outage.
/// </para>
/// </summary>
public class RollingRestartMilestoneEndToEndTests : IAsyncLifetime
{
    private const string ServerSigningKey = "dev-only-server-signing-key-change-me-32+";
    private const string TokenSigningKey = "dev-only-client-signing-key-change-me-32+";
    private const string UserTokenSigningKey = "dev-only-sample-user-signing-key-change-me-32+";
    private const string HubName = "chatHub";
    private const string RoomId = "rolling-restart-room";

    private readonly ITestOutputHelper _output;
    private readonly PostgresContainerFixture _database = new();
    private ProcessFixture? _nodeA;
    private ProcessFixture? _nodeB;
    private ProcessFixture? _apiA;
    private ProcessFixture? _apiB;

    public RollingRestartMilestoneEndToEndTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        if (!_database.IsAvailable)
        {
            return;
        }

        var nodeAPort = GetFreeTcpPort();
        var nodeBPort = GetFreeTcpPort();
        var apiAPort = GetFreeTcpPort();
        var apiBPort = GetFreeTcpPort();
        var siloPortA = GetFreeTcpPort();
        var gatewayPortA = GetFreeTcpPort();
        var siloPortB = GetFreeTcpPort();
        var gatewayPortB = GetFreeTcpPort();

        var nodeAUrl = $"http://127.0.0.1:{nodeAPort}";
        var nodeBUrl = $"http://127.0.0.1:{nodeBPort}";
        var clusterId = $"switchboard-slice7-{Guid.NewGuid():n}";

        var commonArgs = new Dictionary<string, string>
        {
            ["Switchboard__TokenSigningKey"] = TokenSigningKey,
            ["Switchboard__ServerSigningKey"] = ServerSigningKey,
            ["Switchboard__UseOrleansCluster"] = "true",
            ["Switchboard__OrleansClusterId"] = clusterId,
            ["Switchboard__OrleansServiceId"] = clusterId,
            ["Switchboard__OrleansAdoNetConnectionString"] = _database.ConnectionString,
            ["Switchboard__OrleansAdoNetInvariant"] = "Npgsql",
            ["Switchboard__ObserverHeartbeatInterval"] = "00:00:00.300",
            ["Switchboard__HealthCheckCacheInterval"] = "00:00:00.300",
            ["Switchboard__ClientKeepAliveInterval"] = "00:00:10",
            ["Switchboard__ShutdownTimeout"] = "00:00:05",
        };

        _nodeA = new ProcessFixture(FindServerAssembly(), nodeAPort, new Dictionary<string, string>(commonArgs)
        {
            ["Switchboard__PublicUrl"] = nodeAUrl,
            ["Switchboard__OrleansSiloPort"] = siloPortA.ToString(),
            ["Switchboard__OrleansGatewayPort"] = gatewayPortA.ToString(),
        });
        await _nodeA.StartAsync("/healthz", TimeSpan.FromSeconds(30));

        _nodeB = new ProcessFixture(FindServerAssembly(), nodeBPort, new Dictionary<string, string>(commonArgs)
        {
            ["Switchboard__PublicUrl"] = nodeBUrl,
            ["Switchboard__OrleansSiloPort"] = siloPortB.ToString(),
            ["Switchboard__OrleansGatewayPort"] = gatewayPortB.ToString(),
        });
        await _nodeB.StartAsync("/healthz", TimeSpan.FromSeconds(30));

        // api-b only, for now — deliberately *not* api-a yet. Cluster-wide least-loaded
        // server-connection assignment (plan decision D18) is decided once, at negotiate time, and
        // is sticky for a connection's whole life; the milestone needs clientB's assignment
        // permanently pinned to node B so it can genuinely prove "unaffected throughout" rather than
        // getting assigned api-a's connection by chance and being just as exposed to node A's outage
        // as clientA. api-a — and clientA — don't get created until the test method, after clientB
        // is fully connected and joined, so there is nothing for that first assignment to pick but
        // api-b's own connection.
        _apiB = new ProcessFixture(FindAssembly("SampleChatApp.Api", "samples/SampleChatApp"), apiBPort, new Dictionary<string, string>
        {
            ["Switchboard__Url"] = nodeBUrl,
            ["Switchboard__ServerToken"] = IssueServerToken("sample-chat-app-api-b"),
            ["Auth__UserTokenSigningKey"] = UserTokenSigningKey,
        });
        await _apiB.StartAsync("/api/auth/login", TimeSpan.FromSeconds(30));

        // api-b's Connector needs its own real time to connect its pool before any negotiate against
        // node B can succeed (D5: negotiate fails fast with 503 otherwise) — node B's own /healthz
        // becomes healthy once it has registered that connection locally.
        await WaitUntilHealthyAsync(_nodeB.BaseUrl, TimeSpan.FromSeconds(20));

        _apiAPort = apiAPort;
        _nodeAUrl = nodeAUrl;
    }

    private int _apiAPort;
    private string _nodeAUrl = string.Empty;

    /// <summary>Starts api-a on demand, from inside the test method — see the ordering rationale on
    /// api-b's construction in <see cref="InitializeAsync"/> above.</summary>
    private async Task StartApiAAsync()
    {
        _apiA = new ProcessFixture(FindAssembly("SampleChatApp.Api", "samples/SampleChatApp"), _apiAPort, new Dictionary<string, string>
        {
            ["Switchboard__Url"] = _nodeAUrl,
            ["Switchboard__ServerToken"] = IssueServerToken("sample-chat-app-api-a"),
            ["Auth__UserTokenSigningKey"] = UserTokenSigningKey,
        });
        await _apiA.StartAsync("/api/auth/login", TimeSpan.FromSeconds(30));
        await WaitUntilHealthyAsync(_nodeA!.BaseUrl, TimeSpan.FromSeconds(20));
    }

    public async Task DisposeAsync()
    {
        if (_apiA is not null)
        {
            await _apiA.DisposeAsync();
        }

        if (_apiB is not null)
        {
            await _apiB.DisposeAsync();
        }

        if (_nodeA is not null)
        {
            await _nodeA.DisposeAsync();
        }

        if (_nodeB is not null)
        {
            await _nodeB.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    [Fact]
    public async Task RestartingNodeA_DoesNotAffectNodeB_AndClientA_ReconnectsAndResumesGroupMessages()
    {
        if (!_database.IsAvailable)
        {
            _output.WriteLine($"Skipping Phase 3 milestone test — no real database available ({_database.UnavailableReason}). Untested in this environment, per plan §Slice 7's own database caveat (inherited from Slice 6).");
            return;
        }

        // --- clientB: real, full-stack app-server flow through api-b, pinned to node B for its
        // whole life — node B and api-b never go down in this test. ---
        await using var clientB = await ConnectRealClientAsync(_apiB!, "bob");

        var clientBSawDisconnect = false;
        clientB.Closed += _ => { clientBSawDisconnect = true; return Task.CompletedTask; };
        clientB.Reconnecting += _ => { clientBSawDisconnect = true; return Task.CompletedTask; };

        var clientBReceived = new List<TaskCompletionSource<string>>();
        clientB.On<JsonElement>("ReceiveMessage", msg => clientBReceived[^1].TrySetResult(msg.GetProperty("text").GetString()!));

        await clientB.InvokeAsync("JoinRoom", RoomId).WaitAsync(TimeSpan.FromSeconds(10));

        // Only now does api-a — and the second candidate for cluster-wide assignment — come into
        // existence, so clientB's own assignment above had nothing else to pick and is permanently
        // pinned to api-b/node B (see the ordering rationale in InitializeAsync/StartApiAAsync).
        await StartApiAAsync();

        // Node A registering a server connection locally (what /healthz inside StartApiAAsync
        // already waited for) is not the same as node A's ObserverHeartbeatService having actually
        // subscribed to the hub grain yet — that only happens on its own periodic cadence
        // (ObserverHeartbeatInterval, 300ms here). Cluster-wide assignment (plan decision D18) can
        // legally pick a connection whose owner node hasn't subscribed yet, in which case delivery
        // is silently dropped (HubGrain logs "no active subscription" rather than throwing) and the
        // invocation just hangs — the exact race the in-process two-node unit tests guard against by
        // polling GetSubscriberCountAsync before connecting a client, which isn't available across
        // real process boundaries. A few heartbeat intervals' worth of margin here is the
        // out-of-process equivalent.
        await Task.Delay(TimeSpan.FromSeconds(2));

        // --- clientA: real, full-stack app-server flow through api-a, pinned to node A — the node
        // this test restarts. ---
        var clientAToken = await LoginAsync(_apiA!, "alice");
        await using var clientA = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(_apiA!.BaseUrl), $"/{HubName}"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(clientAToken);
            })
            // The default automatic-reconnect policy gives up after ~42s across four attempts —
            // comfortably shorter than a fresh silo's boot-and-rejoin-the-cluster time, which would
            // make this test flaky on nothing but timing. Retries every second, indefinitely, for
            // the life of this test instead.
            .WithAutomaticReconnect(new UnlimitedRetryPolicy())
            .Build();

        var clientADisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.Closed += _ => { clientADisconnected.TrySetResult(); return Task.CompletedTask; };
        clientA.Reconnecting += _ => { clientADisconnected.TrySetResult(); return Task.CompletedTask; };

        var clientAReconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.Reconnected += async _ =>
        {
            _output.WriteLine($"clientA.Reconnected fired, state={clientA.State}");
            try
            {
                // A reconnect is a fresh SignalR connection with a new ConnectionId — group
                // membership does not survive it and must be re-established explicitly, same as any
                // real client.
                //
                // Retried rather than invoked once: a hub-method completion delivered cross-node
                // goes through the observer backplane's plain subscribe-and-call path (ADR-003's own
                // documented "fire-and-forget, may be lost" semantics — there is no delivery retry
                // underneath it), and a node that just restarted has not necessarily run its first
                // heartbeat re-subscribe yet by the time this fires. A real client built against this
                // system's documented delivery guarantees should retry an RPC that appears to hang
                // for exactly this reason; this mirrors that rather than depending on a single
                // attempt winning a race with the target's own subscribe cadence.
                await RetryInvokeAsync(clientA, "JoinRoom", RoomId, _output);
                _output.WriteLine("clientA.Reconnected: JoinRoom succeeded");
                clientAReconnected.TrySetResult();
            }
            catch (Exception ex)
            {
                _output.WriteLine($"clientA.Reconnected handler failed: {ex}");
            }
        };

        var clientAReceived = new List<TaskCompletionSource<string>>();
        clientA.On<JsonElement>("ReceiveMessage", msg => clientAReceived[^1].TrySetResult(msg.GetProperty("text").GetString()!));

        await clientA.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        _output.WriteLine($"clientA connected, state={clientA.State}, connectionId={clientA.ConnectionId}");
        try
        {
            await clientA.InvokeAsync("JoinRoom", RoomId).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            _output.WriteLine($"clientA JoinRoom failed: {ex}");
            _output.WriteLine($"=== node A output ===\n{_nodeA!.RecentOutput}");
            _output.WriteLine($"=== node B output ===\n{_nodeB!.RecentOutput}");
            _output.WriteLine($"=== api-a output ===\n{_apiA!.RecentOutput}");
            _output.WriteLine($"=== api-b output ===\n{_apiB!.RecentOutput}");
            throw;
        }

        // --- Baseline: room fan-out works, cross-node, before anything is restarted. ---
        clientAReceived.Add(new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        clientBReceived.Add(new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        await clientB.InvokeAsync("SendMessage", RoomId, "before-restart").WaitAsync(TimeSpan.FromSeconds(10));
        _output.WriteLine($"clientA state before wait: {clientA.State}");
        Assert.Equal("before-restart", await clientAReceived[^1].Task.WaitAsync(TimeSpan.FromSeconds(15)));
        _output.WriteLine($"clientB state before wait: {clientB.State}");
        Assert.Equal("before-restart", await clientBReceived[^1].Task.WaitAsync(TimeSpan.FromSeconds(15)));

        // --- Kill node A gracefully (real SIGTERM -> real IHostedService.StopAsync). ---
        await _nodeA!.StopGracefullyAsync(TimeSpan.FromSeconds(15));
        await clientADisconnected.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // --- While node A is down: node B and everything behind it must be completely unaffected. ---
        clientBReceived.Add(new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        await clientB.InvokeAsync("SendMessage", RoomId, "during-outage").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("during-outage", await clientBReceived[^1].Task.WaitAsync(TimeSpan.FromSeconds(15)));

        // --- Bring node A back up on the same address; the milestone's client-side reconnect. ---
        await _nodeA.RestartAsync("/healthz", TimeSpan.FromSeconds(30));
        try
        {
            await clientAReconnected.Task.WaitAsync(TimeSpan.FromSeconds(120));
        }
        catch (Exception ex)
        {
            _output.WriteLine($"clientA reconnect failed: {ex}; clientA.State={clientA.State}");
            _output.WriteLine($"=== node A output ===\n{_nodeA.RecentOutput}");
            _output.WriteLine($"=== node B output ===\n{_nodeB!.RecentOutput}");
            _output.WriteLine($"=== api-a output ===\n{_apiA!.RecentOutput}");
            _output.WriteLine($"=== api-b output ===\n{_apiB!.RecentOutput}");
            throw;
        }

        // --- After restart: cross-node group fan-out resumes for the client that went through the
        // outage, and node B's client never saw more than the one expected disconnect signal. ---
        clientAReceived.Add(new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        clientBReceived.Add(new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        await clientB.InvokeAsync("SendMessage", RoomId, "after-restart").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("after-restart", await clientAReceived[^1].Task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("after-restart", await clientBReceived[^1].Task.WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.False(clientBSawDisconnect, "clientB (connected only to node B) must never see a disconnect/reconnect signal while only node A is restarted.");

        await clientA.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await clientB.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The milestone's other half (plans/phase-3-scale-out-and-resilience.md § Slice 7 gate): "grep
    /// -ri redis over the solution returns nothing." Scoped to code and project files, not
    /// documentation — docs/README.md, the ADRs, and the plan itself all legitimately discuss and
    /// name the "no Redis" decision by design, so a literal solution-wide grep would fail on its own
    /// rationale. What actually matters operationally is that no source file, csproj, or the sln
    /// references Redis. This file itself is excluded for the same reason the docs are — it names
    /// "Redis" only in this very doc comment, discussing the gate rather than referencing Redis.
    /// </summary>
    [Fact]
    public void NoRedisReferenceAnywhereInCodeOrProjectFiles()
    {
        var repoRoot = FindRepoRoot();
        var thisFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "RollingRestartMilestoneEndToEndTests.cs"));
        var offenders = new List<string>();

        foreach (var extension in new[] { "*.cs", "*.csproj", "*.sln" })
        {
            foreach (var file in Directory.EnumerateFiles(repoRoot, extension, SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    string.Equals(Path.GetFullPath(file), thisFile, StringComparison.Ordinal))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                if (text.Contains("redis", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add(file);
                }
            }
        }

        Assert.True(offenders.Count == 0, $"Redis reference found in: {string.Join(", ", offenders)}");
    }

    /// <summary>Bounded retry for an RPC whose completion may have been dropped by the observer
    /// backplane's fire-and-forget delivery (ADR-003) rather than genuinely failed — see the caller.</summary>
    private static async Task RetryInvokeAsync(HubConnection connection, string methodName, string arg, ITestOutputHelper output)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            output.WriteLine($"RetryInvokeAsync attempt {attempt}, state={connection.State}");
            try
            {
                await connection.InvokeAsync(methodName, arg).WaitAsync(TimeSpan.FromSeconds(5));
                output.WriteLine($"RetryInvokeAsync attempt {attempt} succeeded");
                return;
            }
            catch (Exception ex)
            {
                output.WriteLine($"RetryInvokeAsync attempt {attempt} failed: {ex.GetType().Name}: {ex.Message}");
                lastError = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }

        throw new InvalidOperationException($"'{methodName}' did not complete after retrying.", lastError);
    }

    private static async Task<HubConnection> ConnectRealClientAsync(ProcessFixture api, string username)
    {
        var token = await LoginAsync(api, username);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(api.BaseUrl), $"/{HubName}"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return connection;
    }

    private static async Task<string> LoginAsync(ProcessFixture api, string username)
    {
        using var http = new HttpClient { BaseAddress = new Uri(api.BaseUrl) };
        var loginResponse = await http.PostAsJsonAsync("api/auth/login", new { Username = username });
        loginResponse.EnsureSuccessStatusCode();
        var login = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
        return login.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task WaitUntilHealthyAsync(string baseUrl, TimeSpan timeout)
    {
        using var client = new HttpClient();
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var health = await client.GetAsync($"{baseUrl}/healthz");
            if (health.IsSuccessStatusCode)
            {
                return;
            }

            await Task.Delay(250);
        }
    }

    private static string IssueServerToken(string serverId)
    {
        var options = Options.Create(new SwitchboardOptions
        {
            PublicUrl = "unused",
            TokenSigningKey = TokenSigningKey,
            ServerSigningKey = ServerSigningKey,
        });

        return new JwtTokenService(options).IssueServerToken(serverId, [HubName], TimeSpan.FromHours(1));
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindServerAssembly() => FindAssembly("Keryhe.Switchboard.Server", "src");

    private static string FindAssembly(string projectName, string parentDirectory)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", parentDirectory, projectName, "bin", "Debug", "net10.0", $"{projectName}.dll");
        var full = Path.GetFullPath(candidate);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Assembly not found at {full}. Build the solution before running integration tests.", full);
        }

        return full;
    }

    private sealed class UnlimitedRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => TimeSpan.FromSeconds(1);
    }

    private static string FindRepoRoot()
    {
        var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        if (!File.Exists(Path.Combine(candidate, "Switchboard.sln")))
        {
            throw new InvalidOperationException($"Could not locate repo root (Switchboard.sln) from {candidate}.");
        }

        return candidate;
    }
}
