using Phase0.Spike.Connector.Dispatch;
using Phase0.Spike.Host.Hubs;

namespace Phase0.Spike.Tests.WorkstreamB;

/// <summary>
/// B4: Completion/StreamItem and Clients.All are two distinct outbound paths that must never be
/// conflated (docs/docs/04-design.md §11). Also confirms the synthetic handshake response is
/// identifiable and droppable, matching the Connector's outbound-reader contract.
/// </summary>
public class ReturnPathSplitTests
{
    [Fact]
    public async Task Completion_arrives_on_the_pipe_while_ClientsAll_reaches_the_lifetime_manager_only()
    {
        using var harness = new HubDispatchHarness();
        var pipeline = harness.GetPipeline<TestHub>();
        var recorder = harness.GetRecorder<TestHub>();

        var user = IdentityReconstruction.Build(userId: null, claims: null);
        var context = new SwitchboardClientConnectionContext(Guid.NewGuid().ToString(), user);
        var runTask = pipeline(context);

        await HandshakeWriter.WriteHandshakeRequestAsync(context.ToHubWriter, "json");
        await JsonFrameIO.WriteInvocationAsync(context.ToHubWriter, "1", "EchoAndBroadcast", ["ping"]);

        var completion = await JsonFrameIO.ReadNextSignificantMessageAsync(context.FromHubReader).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, completion.GetProperty("type").GetInt32());
        Assert.Equal("ping", completion.GetProperty("result").GetString());

        // Give the recorder a moment: Clients.All.SendAsync is awaited before the hub method
        // returns, so this should already be populated, but poll briefly to avoid flakiness.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (recorder.AllSends.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Single(recorder.AllSends);
        var (method, args) = recorder.AllSends.Single();
        Assert.Equal("Broadcast", method);
        Assert.Equal("ping", args[0]);

        await context.CompleteInboundAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Handshake_response_is_the_first_frame_and_is_identifiable_as_droppable()
    {
        using var harness = new HubDispatchHarness();
        var pipeline = harness.GetPipeline<TestHub>();

        var user = IdentityReconstruction.Build(userId: null, claims: null);
        var context = new SwitchboardClientConnectionContext(Guid.NewGuid().ToString(), user);
        var runTask = pipeline(context);

        await HandshakeWriter.WriteHandshakeRequestAsync(context.ToHubWriter, "json");

        var firstFrame = await JsonFrameIO.ReadNextFrameAsync(context.FromHubReader).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("{}", firstFrame);

        await context.CompleteInboundAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
