using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;

namespace Phase0.Spike.Host;

/// <summary>
/// Harmless policies registered alongside SwitchboardNegotiateMatcherPolicy (Order 100) at
/// orders below and above it, purely so A4 can assert the redirect still wins regardless of
/// registration/execution order relative to unrelated IEndpointSelectorPolicy instances.
/// </summary>
public sealed class LowOrderNoOpPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
    public override int Order => -100;
    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints) => false;
    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates) => Task.CompletedTask;
}

public sealed class HighOrderNoOpPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
    public override int Order => 1000;
    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints) => false;
    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates) => Task.CompletedTask;
}
