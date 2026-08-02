using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace Keryhe.Switchboard.LoadHarness;

public enum RampStopReason
{
    ReachedTarget,
    HostLimit,
    ServiceLimit,
}

public sealed record RampResult(
    int Requested,
    int Connected,
    RampStopReason StopReason,
    string? StopDetail,
    TimeSpan Duration,
    double NegotiateThroughputPerSecond,
    IReadOnlyDictionary<FailureCategory, int> FailuresByCategory);

/// <summary>
/// Plan decision D33/D35: ramps real <see cref="HubConnection"/>s (through <c>SampleChatApp.Api</c>'s
/// negotiate redirect, exactly like a real client) up to a target count, measuring negotiate
/// throughput and classifying every failure. Stops early — reporting precisely why — rather than
/// grinding through thousands of failures once a run of them looks like a host or service limit
/// rather than transient noise (plan decision D35's core requirement: distinguish the harness's
/// own limits from the service's).
/// </summary>
public sealed class ConnectionRamp(string apiBaseUrl)
{
    /// <summary>A run of this many consecutive host-limit-category failures (ephemeral port or
    /// file descriptor exhaustion) is treated as "the host stopped us," not noise — one or two
    /// stray failures under concurrent load are not unusual and don't warrant aborting the ramp.</summary>
    private const int ConsecutiveHostLimitFailuresToStop = 20;

    private const int MaxConcurrentConnectAttempts = 50;

    public async Task<(RampResult Result, IReadOnlyList<HubConnection> Connections)> RunAsync(int targetCount, IProgress<int>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var connections = new ConcurrentBag<HubConnection>();
        var failuresByCategory = new ConcurrentDictionary<FailureCategory, int>();
        var connectedCount = 0;
        var consecutiveHostLimitFailures = 0;
        var stopReason = RampStopReason.ReachedTarget;
        string? stopDetail = null;
        using var cts = new CancellationTokenSource();
        using var semaphore = new SemaphoreSlim(MaxConcurrentConnectAttempts);
        using var http = new HttpClient { BaseAddress = new Uri(apiBaseUrl) };

        var tasks = new List<Task>();
        for (var i = 0; i < targetCount; i++)
        {
            if (cts.IsCancellationRequested)
            {
                break;
            }

            await semaphore.WaitAsync(cts.Token).ConfigureAwait(false);
            var clientIndex = i;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var connection = await ConnectOneAsync(http, clientIndex, cts.Token);
                    connections.Add(connection);
                    Interlocked.Increment(ref connectedCount);
                    Interlocked.Exchange(ref consecutiveHostLimitFailures, 0);
                    progress?.Report(connectedCount);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cts.IsCancellationRequested)
                {
                    var category = FailureClassifier.Classify(ex);
                    failuresByCategory.AddOrUpdate(category, 1, (_, count) => count + 1);

                    if (category is FailureCategory.EphemeralPortExhaustion or FailureCategory.FileDescriptorExhaustion)
                    {
                        var consecutive = Interlocked.Increment(ref consecutiveHostLimitFailures);
                        if (consecutive >= ConsecutiveHostLimitFailuresToStop && !cts.IsCancellationRequested)
                        {
                            stopReason = RampStopReason.HostLimit;
                            stopDetail = $"{consecutive} consecutive {category} failures — this is the harness's own host running out of a resource, not the service ({ex.Message})";
                            cts.Cancel();
                        }
                    }
                    else if (category == FailureCategory.NegotiateBackpressure503)
                    {
                        // D5's documented, correct backpressure — not a failure worth aborting the
                        // ramp over, but worth noting if it's what ultimately stops us from
                        // reaching target (checked after the loop, from failuresByCategory).
                        stopReason = RampStopReason.ServiceLimit;
                        stopDetail = "the service returned 503 (negotiate backpressure, D5) — correct behavior under overload, not a defect";
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }, cts.Token));
        }

        await Task.WhenAll(tasks.Select(async t =>
        {
            try
            {
                await t;
            }
            catch (OperationCanceledException)
            {
                // Expected once cts.Cancel() fires — in-flight attempts unwind here.
            }
        }));

        stopwatch.Stop();

        var finalConnectedCount = connections.Count;
        if (finalConnectedCount >= targetCount)
        {
            stopReason = RampStopReason.ReachedTarget;
            stopDetail = null;
        }

        var throughput = stopwatch.Elapsed.TotalSeconds > 0 ? finalConnectedCount / stopwatch.Elapsed.TotalSeconds : 0;

        var result = new RampResult(
            targetCount,
            finalConnectedCount,
            stopReason,
            stopDetail,
            stopwatch.Elapsed,
            throughput,
            failuresByCategory.ToDictionary(kv => kv.Key, kv => kv.Value));

        return (result, connections.ToList());
    }

    private async Task<HubConnection> ConnectOneAsync(HttpClient http, int clientIndex, CancellationToken ct)
    {
        var loginResponse = await http.PostAsJsonAsync("api/auth/login", new { Username = $"load-{clientIndex}" }, ct);
        loginResponse.EnsureSuccessStatusCode();
        var login = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync(ct));
        var userToken = login.RootElement.GetProperty("accessToken").GetString()!;

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(apiBaseUrl), "/chatHub"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(userToken);
            })
            .Build();

        try
        {
            await connection.StartAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return connection;
    }
}
