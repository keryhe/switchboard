using Phase0.Spike.Connector.Dispatch;
using Phase0.Spike.Host.Hubs;

namespace Phase0.Spike.Tests.WorkstreamB;

/// <summary>
/// B3: identity flows into Context.User / Context.UserIdentifier only through
/// IConnectionUserFeature, and per-method [Authorize] respects it. The non-null
/// authenticationType on the synthesized ClaimsIdentity is load-bearing -- without it
/// IsAuthenticated is false and every [Authorize] check fails.
/// </summary>
public class IdentityFlowTests
{
    [Fact]
    public async Task With_userId_Context_User_and_UserIdentifier_are_populated_and_authorized_method_succeeds()
    {
        using var harness = new HubDispatchHarness();
        var pipeline = harness.GetPipeline<TestHub>();

        var user = IdentityReconstruction.Build(userId: "alice", claims: null);
        var context = new SwitchboardClientConnectionContext(Guid.NewGuid().ToString(), user);
        var runTask = pipeline(context);

        await HandshakeWriter.WriteHandshakeRequestAsync(context.ToHubWriter, "json");
        await JsonFrameIO.WriteInvocationAsync(context.ToHubWriter, "1", "UserIdentifierValue", []);
        var idResult = await JsonFrameIO.ReadNextSignificantMessageAsync(context.FromHubReader).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("alice", idResult.GetProperty("result").GetString());

        await JsonFrameIO.WriteInvocationAsync(context.ToHubWriter, "2", "IsAuthenticated", []);
        var authResult = await JsonFrameIO.ReadNextSignificantMessageAsync(context.FromHubReader).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(authResult.GetProperty("result").GetBoolean());

        await JsonFrameIO.WriteInvocationAsync(context.ToHubWriter, "3", "SecretEcho", ["secret"]);
        var secretResult = await JsonFrameIO.ReadNextSignificantMessageAsync(context.FromHubReader).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secretResult.TryGetProperty("error", out _));
        Assert.Equal("secret", secretResult.GetProperty("result").GetString());

        await context.CompleteInboundAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Without_identity_authorized_method_is_denied()
    {
        using var harness = new HubDispatchHarness();
        var pipeline = harness.GetPipeline<TestHub>();

        var user = IdentityReconstruction.Build(userId: null, claims: null);
        var context = new SwitchboardClientConnectionContext(Guid.NewGuid().ToString(), user);
        var runTask = pipeline(context);

        await HandshakeWriter.WriteHandshakeRequestAsync(context.ToHubWriter, "json");

        await JsonFrameIO.WriteInvocationAsync(context.ToHubWriter, "1", "IsAuthenticated", []);
        var authResult = await JsonFrameIO.ReadNextSignificantMessageAsync(context.FromHubReader).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(authResult.GetProperty("result").GetBoolean());

        await JsonFrameIO.WriteInvocationAsync(context.ToHubWriter, "2", "SecretEcho", ["secret"]);
        var secretResult = await JsonFrameIO.ReadNextSignificantMessageAsync(context.FromHubReader).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(secretResult.TryGetProperty("error", out _));

        await context.CompleteInboundAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
