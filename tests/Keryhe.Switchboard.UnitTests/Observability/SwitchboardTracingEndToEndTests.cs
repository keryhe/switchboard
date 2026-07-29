using System.Diagnostics;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Observability;

/// <summary>
/// Phase 4 Slice 5 gate (plans/phase-4-management-and-observability.md §4, plan decision D26): an
/// in-process <see cref="ActivityListener"/> observes a real negotiate + connect against a real
/// <c>HubConnection</c>, asserting a "negotiate" span and a "client_connect" span each carry
/// <c>hub</c>/<c>connectionId</c>/<c>node.id</c> tags — and asserting the <em>absence</em> of any
/// "message_route" span with the default configuration (<see cref="Keryhe.Switchboard.Core.Models.SwitchboardOptions.TraceMessageRouting"/>
/// defaults to <c>false</c>), even though a real client message is actually routed during the test.
/// A span per routed message at broadcast fan-out rates is the cardinality risk D26 exists to avoid,
/// so proving it stays off by default — not just that it can be turned on — is the point of this
/// test, mirroring how Slice 4's gauge test asserted node-local counts rather than just their shape.
/// </summary>
public class SwitchboardTracingEndToEndTests
{
    private const string HubName = "chatHub-tracing-e2e";

    [Fact]
    public async Task NegotiateAndClientConnect_ProduceTaggedSpans_AndNoMessageRouteSpanByDefault()
    {
        await using var service = new RealKestrelServerFixture();
        await service.StartAsync();
        var tracing = service.Services.GetRequiredService<SwitchboardTracing>();
        var tokenService = service.Services.GetRequiredService<ITokenService>();
        var nodeId = service.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Keryhe.Switchboard.Core.Models.SwitchboardOptions>>().Value.NodeId;
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServer = await AppServerDouble.ConnectAsync(service.ServerAddress, HubName, serverToken);

        using var collector = new ActivityCollector(tracing.ActivitySource);

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        await using var client = BuildClient(service, clientToken);
        await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var open = await appServer.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(5));
        var connectionId = open.ConnectionId!;

        // A real client message is actually routed here — proving the message-route span's
        // absence below isn't just "nothing happened to trace," but "tracing was deliberately
        // skipped for this hot path."
        await client.SendAsync("Ping");
        await appServer.ReceiveEnvelopeAsync(ServerEnvelopeType.ClientMessage, TimeSpan.FromSeconds(5));

        await WaitUntilAsync(
            () => Task.FromResult(collector.Activities.Count(a => a.OperationName is "negotiate" or "client_connect")),
            count => count >= 2,
            TimeSpan.FromSeconds(10));

        var negotiateActivity = Assert.Single(collector.Activities, a => a.OperationName == "negotiate");
        var connectActivity = Assert.Single(collector.Activities, a => a.OperationName == "client_connect");

        AssertTags(negotiateActivity, connectionId, nodeId);
        AssertTags(connectActivity, connectionId, nodeId);

        Assert.DoesNotContain(collector.Activities, a => a.OperationName == "message_route");
    }

    private static void AssertTags(Activity activity, string connectionId, string nodeId)
    {
        Assert.Equal(HubName, activity.GetTagItem("hub"));
        Assert.Equal(connectionId, activity.GetTagItem("connectionId"));
        Assert.Equal(nodeId, activity.GetTagItem("node.id"));
    }

    private static HubConnection BuildClient(RealKestrelServerFixture node, string clientToken)
    {
        var url = new Uri(node.ServerAddress, $"/{HubName}");
        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(clientToken);
                options.Transports = HttpTransportType.WebSockets;
            })
            .Build();
    }

    private static async Task WaitUntilAsync<T>(Func<Task<T>> poll, Func<T, bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!predicate(await poll()))
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    /// <summary>Thin <see cref="ActivityListener"/> wrapper scoped to one specific
    /// <see cref="ActivitySource"/> instance — see <c>SwitchboardMetricsEndToEndTests.MetricsCollector</c>'s
    /// remarks for why instance identity, not name, is required: every <c>SwitchboardTracing</c>
    /// singleton creates a same-named but distinct <see cref="ActivitySource"/>. Uses a
    /// <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/>, not a plain <c>List&lt;T&gt;</c>,
    /// for the same reason the metrics collector does — <c>ActivityStopped</c> fires synchronously
    /// on whichever thread completed the traced operation, concurrently with this test's own
    /// polling thread.</summary>
    private sealed class ActivityCollector : IDisposable
    {
        private readonly ActivityListener _listener = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<Activity> _activities = new();

        public ActivityCollector(ActivitySource source)
        {
            _listener.ShouldListenTo = candidate => ReferenceEquals(candidate, source);
            _listener.Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData;
            _listener.ActivityStopped = activity => _activities.Enqueue(activity);
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Activities => _activities.ToList();

        public void Dispose() => _listener.Dispose();
    }
}
