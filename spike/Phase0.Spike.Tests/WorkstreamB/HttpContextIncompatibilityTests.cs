using Phase0.Spike.Connector.Dispatch;
using Phase0.Spike.Host.Hubs;

namespace Phase0.Spike.Tests.WorkstreamB;

/// <summary>
/// B6: Context.GetHttpContext() fails predictably (returns null) on a synthetic connection,
/// rather than crashing inside the framework. Documented, permanent incompatibility -- see
/// docs/docs/04-design.md §11 and the Phase 5 compatibility matrix.
/// </summary>
public class HttpContextIncompatibilityTests
{
    [Fact]
    public async Task GetHttpContext_returns_null_predictably()
    {
        using var harness = new HubDispatchHarness();
        var pipeline = harness.GetPipeline<TestHub>();

        var user = IdentityReconstruction.Build(userId: null, claims: null);
        var context = new SwitchboardClientConnectionContext(Guid.NewGuid().ToString(), user);
        var runTask = pipeline(context);

        await HandshakeWriter.WriteHandshakeRequestAsync(context.ToHubWriter, "json");
        await JsonFrameIO.WriteInvocationAsync(context.ToHubWriter, "1", "HttpContextStatus", []);

        var result = await JsonFrameIO.ReadNextSignificantMessageAsync(context.FromHubReader).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.TryGetProperty("error", out _));
        Assert.Equal("null", result.GetProperty("result").GetString());

        await context.CompleteInboundAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
