using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;

namespace Phase0.Spike.Connector.Negotiate;

/// <summary>
/// Intercepts the negotiate endpoint that <c>MapHub&lt;T&gt;()</c> creates and replaces it with a
/// redirect delegate, without disturbing any other mapped endpoint (transport, MVC, minimal APIs).
///
/// The endpoint metadata is copied from the original endpoint verbatim — this is the only way
/// class-level [Authorize] on a Hub survives the swap (see docs/docs/04-design.md §8 and the
/// spike plan §3/A3). Do not construct the replacement from a bare metadata list.
/// </summary>
public sealed class SwitchboardNegotiateMatcherPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
    // Runs after routing's built-in policies (HTTP method, host) resolve the candidate set, but
    // well before anything else would care about negotiate specifically. A4 asserts this holds
    // regardless of what order unrelated policies in the same app register at.
    public override int Order => 100;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            if (endpoint.Metadata.GetMetadata<NegotiateMetadata>() is not null)
            {
                return true;
            }
        }

        return false;
    }

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates.IsValidCandidate(i))
            {
                continue;
            }

            var candidate = candidates[i];
            if (candidate.Endpoint is not RouteEndpoint routeEndpoint)
            {
                continue;
            }

            if (routeEndpoint.Metadata.GetMetadata<NegotiateMetadata>() is null)
            {
                continue;
            }

            var hubName = ResolveHubName(routeEndpoint);

            var replacement = new RouteEndpoint(
                requestDelegate: context =>
                {
                    var handler = context.RequestServices.GetRequiredService<INegotiateRedirectHandler>();
                    return handler.HandleAsync(context, hubName);
                },
                routePattern: routeEndpoint.RoutePattern,
                order: routeEndpoint.Order,
                metadata: routeEndpoint.Metadata,
                displayName: $"{routeEndpoint.DisplayName} (Switchboard redirect)");

            candidates.ReplaceEndpoint(i, replacement, candidate.Values);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Derives the hub name from the route pattern (e.g. "/testHub/negotiate" -&gt; "testHub").
    /// A single policy registration covers every hub mapped with MapHub&lt;T&gt;() with no
    /// per-hub wiring, per docs/docs/04-design.md §8.
    /// </summary>
    private static string ResolveHubName(RouteEndpoint endpoint)
    {
        var raw = endpoint.RoutePattern.RawText?.TrimStart('/') ?? string.Empty;
        var slashIndex = raw.IndexOf('/');
        return slashIndex > 0 ? raw[..slashIndex] : raw;
    }
}
