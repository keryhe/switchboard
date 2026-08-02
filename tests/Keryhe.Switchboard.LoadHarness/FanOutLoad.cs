using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace Keryhe.Switchboard.LoadHarness;

public sealed record FanOutResult(
    int TargetCount,
    int DeliveredCount,
    bool DeliveredToAll,
    TimeSpan TimeToFullOrTimeout,
    double MessagesPerSecond,
    TimeSpan P50Latency,
    TimeSpan P95Latency,
    TimeSpan P99Latency);

/// <summary>
/// Plan decision D33/D35: triggers one broadcast (via the management API — plan §"Management sends
/// bypass app servers and hub code entirely," so this reaches every connection on the hub
/// regardless of SignalR group membership) against every ramped connection, and measures the
/// harness's own observed end-to-end latency. This is deliberately an independent measurement from
/// the service's own <c>signalr.message.outbound_duration</c> histogram (<see cref="OtlpPercentileReader"/>)
/// — a large divergence between the two is itself a finding (plan §4, Slice 5 gate), not
/// something to reconcile by construction.
/// </summary>
public sealed class FanOutLoad(string serviceBaseUrl, string managementToken)
{
    private const string HubName = "chatHub";
    private const string EventName = "LoadHarnessPing";

    public async Task<FanOutResult> RunAsync(IReadOnlyList<HubConnection> connections, TimeSpan timeout)
    {
        var latencies = new ConcurrentBag<TimeSpan>();
        var deliveredCount = 0;
        var allDeliveredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendTimestamp = 0L;
        var subscriptions = new List<IDisposable>(connections.Count);

        foreach (var connection in connections)
        {
            subscriptions.Add(connection.On<string>(EventName, _ =>
            {
                var elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref sendTimestamp));
                latencies.Add(elapsed);
                if (Interlocked.Increment(ref deliveredCount) >= connections.Count)
                {
                    allDeliveredTcs.TrySetResult();
                }
            }));
        }

        using var http = new HttpClient { BaseAddress = new Uri(serviceBaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);

        var stopwatch = Stopwatch.StartNew();
        Interlocked.Exchange(ref sendTimestamp, Stopwatch.GetTimestamp());

        var response = await http.PostAsJsonAsync(
            $"/api/v1/hubs/{HubName}/send",
            new { target = EventName, arguments = new object[] { Guid.NewGuid().ToString("n") } });
        response.EnsureSuccessStatusCode();

        await Task.WhenAny(allDeliveredTcs.Task, Task.Delay(timeout));
        stopwatch.Stop();

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }

        var sorted = latencies.OrderBy(l => l).ToList();
        var delivered = sorted.Count;
        var throughput = stopwatch.Elapsed.TotalSeconds > 0 ? delivered / stopwatch.Elapsed.TotalSeconds : 0;

        return new FanOutResult(
            connections.Count,
            delivered,
            delivered >= connections.Count,
            stopwatch.Elapsed,
            throughput,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99));
    }

    private static TimeSpan Percentile(IReadOnlyList<TimeSpan> sortedLatencies, double percentile)
    {
        if (sortedLatencies.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var index = (int)Math.Ceiling(percentile * sortedLatencies.Count) - 1;
        return sortedLatencies[Math.Clamp(index, 0, sortedLatencies.Count - 1)];
    }
}
