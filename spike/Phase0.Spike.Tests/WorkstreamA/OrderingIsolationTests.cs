using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Phase0.Spike.Connector.Negotiate;
using Phase0.Spike.Host;

namespace Phase0.Spike.Tests.WorkstreamA;

/// <summary>
/// A4: unrelated IEndpointSelectorPolicy registrations at orders below and above the
/// Switchboard policy (see Program.cs — LowOrderNoOpPolicy at -100, HighOrderNoOpPolicy at 1000)
/// don't disturb the redirect, and unrelated routes (minimal API, MVC, the transport endpoint)
/// are untouched by the policy being registered at all.
/// </summary>
public class OrderingIsolationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void All_three_policies_are_registered_with_the_expected_relative_order()
    {
        using var scope = factory.Services.CreateScope();
        var policies = scope.ServiceProvider.GetServices<MatcherPolicy>().ToList();

        var low = policies.OfType<LowOrderNoOpPolicy>().Single();
        var switchboard = policies.OfType<SwitchboardNegotiateMatcherPolicy>().Single();
        var high = policies.OfType<HighOrderNoOpPolicy>().Single();

        Assert.True(low.Order < switchboard.Order);
        Assert.True(switchboard.Order < high.Order);
    }

    [Fact]
    public async Task Negotiate_redirect_still_wins_with_unrelated_policies_registered_at_both_ends()
    {
        // The dummy policies are always present in this host (Program.cs); this test exists so
        // the assertion is explicit rather than an accidental side effect of every other test.
        var client = factory.CreateClient();

        var response = await client.PostAsync("/testHub/negotiate?negotiateVersion=1", null);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("accessToken", body);
    }

    [Fact]
    public async Task Minimal_api_route_is_unaffected()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/ping");
        response.EnsureSuccessStatusCode();
        Assert.Contains("minimal-api", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Mvc_controller_route_is_unaffected()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/mvc-ping");
        response.EnsureSuccessStatusCode();
        Assert.Contains("\"via\":\"mvc\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Transport_endpoint_is_untouched_by_the_policy()
    {
        // A plain GET with no WebSocket upgrade headers hits the framework's own
        // HttpConnectionDispatcher for the transport endpoint, which requires an established
        // connection (400) -- not the policy's JSON redirect body. Confirms /testHub (transport)
        // is a distinct endpoint from /testHub/negotiate and was never touched by ApplyAsync.
        var client = factory.CreateClient();
        var response = await client.GetAsync("/testHub");

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("accessToken", body);
    }
}
