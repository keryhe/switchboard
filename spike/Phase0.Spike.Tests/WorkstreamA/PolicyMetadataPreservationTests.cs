using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.Routing.Patterns;
using Phase0.Spike.Connector.Negotiate;

namespace Phase0.Spike.Tests.WorkstreamA;

/// <summary>
/// A3 regression guard: directly exercises SwitchboardNegotiateMatcherPolicy.ApplyAsync against
/// a synthetic CandidateSet (no HTTP, no host) and asserts the replacement endpoint's Metadata
/// still contains AuthorizeAttribute. This is the direct proof that the 401-on-negotiate HTTP
/// test (NegotiateRedirectTests) isn't passing for the wrong reason.
/// </summary>
public class PolicyMetadataPreservationTests
{
    [Fact]
    public void AppliesToEndpoints_is_true_only_for_endpoints_carrying_NegotiateMetadata()
    {
        var policy = new SwitchboardNegotiateMatcherPolicy();

        var negotiateEndpoint = BuildEndpoint("/x/negotiate", withNegotiateMetadata: true);
        var otherEndpoint = BuildEndpoint("/x", withNegotiateMetadata: false);

        Assert.True(policy.AppliesToEndpoints([negotiateEndpoint]));
        Assert.False(policy.AppliesToEndpoints([otherEndpoint]));
    }

    [Fact]
    public async Task ApplyAsync_replacement_endpoint_preserves_original_metadata_including_Authorize()
    {
        var original = BuildEndpoint("/secureHub/negotiate", withNegotiateMetadata: true, withAuthorize: true);
        var candidates = new CandidateSet(
            [original],
            [new RouteValueDictionary()],
            [0]);

        var policy = new SwitchboardNegotiateMatcherPolicy();
        await policy.ApplyAsync(new DefaultHttpContext(), candidates);

        var replaced = candidates[0].Endpoint;

        Assert.NotSame(original, replaced);
        Assert.Contains(replaced.Metadata, m => m is AuthorizeAttribute);
        Assert.Contains(replaced.Metadata, m => m is NegotiateMetadata);
    }

    [Fact]
    public async Task ApplyAsync_ignores_candidates_without_NegotiateMetadata()
    {
        var original = BuildEndpoint("/api/ping", withNegotiateMetadata: false);
        var candidates = new CandidateSet(
            [original],
            [new RouteValueDictionary()],
            [0]);

        var policy = new SwitchboardNegotiateMatcherPolicy();
        await policy.ApplyAsync(new DefaultHttpContext(), candidates);

        Assert.Same(original, candidates[0].Endpoint);
    }

    private static Endpoint BuildEndpoint(string pattern, bool withNegotiateMetadata, bool withAuthorize = false)
    {
        var metadata = new List<object>();
        if (withNegotiateMetadata)
        {
            metadata.Add(new NegotiateMetadata());
        }

        if (withAuthorize)
        {
            metadata.Add(new AuthorizeAttribute());
        }

        return new RouteEndpoint(
            requestDelegate: _ => Task.CompletedTask,
            routePattern: RoutePatternFactory.Parse(pattern),
            order: 0,
            metadata: new EndpointMetadataCollection(metadata),
            displayName: pattern);
    }
}
