using Phase0.Spike.Connector.Dispatch;
using Phase0.Spike.Host.Hubs;

namespace Phase0.Spike.Tests.WorkstreamB;

/// <summary>
/// B5: OnConnectedAsync/OnDisconnectedAsync fire at the right points, and a hub that rejects the
/// connection produces a close frame with allowReconnect: false rather than hanging. Every await
/// here is bounded -- a deadlock must fail the test, not hang the run.
/// </summary>
public class LifecycleAndRejectionTests
{
    [Fact]
    public async Task OnConnectedAsync_runs_on_start_and_OnDisconnectedAsync_runs_on_input_completion()
    {
        using var harness = new HubDispatchHarness();
        var pipeline = harness.GetPipeline<TestHub>();

        var connectionId = Guid.NewGuid().ToString();
        var user = IdentityReconstruction.Build(userId: null, claims: null);
        var context = new SwitchboardClientConnectionContext(connectionId, user);
        var runTask = pipeline(context);

        await HandshakeWriter.WriteHandshakeRequestAsync(context.ToHubWriter, "json");
        await JsonFrameIO.ReadNextFrameAsync(context.FromHubReader).WaitAsync(TimeSpan.FromSeconds(5)); // handshake ack

        // Round-trip a no-op invocation to be sure OnConnectedAsync (which runs before dispatch
        // loop starts accepting messages) has definitely completed.
        await JsonFrameIO.WriteInvocationAsync(context.ToHubWriter, "1", "Echo", ["sync"]);
        await JsonFrameIO.ReadNextSignificantMessageAsync(context.FromHubReader).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(connectionId, TestHub.ConnectedIds);
        Assert.DoesNotContain(connectionId, TestHub.DisconnectedIds);

        await context.CompleteInboundAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(connectionId, TestHub.DisconnectedIds);
    }

    [Fact]
    public async Task Rejecting_hub_produces_a_close_frame_with_allowReconnect_false_and_completes()
    {
        using var harness = new HubDispatchHarness();
        var pipeline = harness.GetPipeline<RejectingHub>();

        var user = IdentityReconstruction.Build(userId: null, claims: null);
        var context = new SwitchboardClientConnectionContext(Guid.NewGuid().ToString(), user);
        var runTask = pipeline(context);

        await HandshakeWriter.WriteHandshakeRequestAsync(context.ToHubWriter, "json");

        var message = await JsonFrameIO.ReadNextSignificantMessageAsync(context.FromHubReader).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(7, message.GetProperty("type").GetInt32()); // Close
        Assert.True(message.TryGetProperty("error", out _));

        // Finding (see spike findings/inbound-dispatch.md): in .NET 10, HubConnectionHandler's
        // OnConnectedAsync-throws path emits {"type":7,"error":"..."} with NO "allowReconnect"
        // field at all -- not "allowReconnect: false" as docs/docs/04-design.md §11 assumed.
        // A missing field defaults to false in the client libraries, so behavior matches, but
        // the wire shape differs from what the design doc's code sketch showed.
        if (message.TryGetProperty("allowReconnect", out var allowReconnect))
        {
            Assert.False(allowReconnect.GetBoolean());
        }

        // Must complete on its own -- OnConnectedAsync already threw, no need to complete the input side.
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
