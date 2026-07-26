using Phase0.Spike.Connector.Dispatch;
using Phase0.Spike.Host.Hubs;

namespace Phase0.Spike.Tests.WorkstreamB;

/// <summary>B2: a hub method executes from raw bytes written into a synthetic connection's pipe, with no client attached.</summary>
public class HubMethodDispatchTests
{
    [Fact]
    public async Task Echo_executes_with_correctly_bound_arguments()
    {
        using var harness = new HubDispatchHarness();
        var pipeline = harness.GetPipeline<TestHub>();

        var connectionId = Guid.NewGuid().ToString();
        var user = IdentityReconstruction.Build(userId: null, claims: null);
        var context = new SwitchboardClientConnectionContext(connectionId, user);

        var runTask = pipeline(context);

        await HandshakeWriter.WriteHandshakeRequestAsync(context.ToHubWriter, "json");
        await JsonFrameIO.WriteInvocationAsync(context.ToHubWriter, invocationId: "1", target: "Echo", arguments: ["hello"]);

        var completion = await JsonFrameIO.ReadNextSignificantMessageAsync(context.FromHubReader)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, completion.GetProperty("type").GetInt32());
        Assert.Equal("1", completion.GetProperty("invocationId").GetString());
        Assert.Equal("hello", completion.GetProperty("result").GetString());

        await context.CompleteInboundAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
