using Microsoft.AspNetCore.Routing;

namespace Phase0.Spike.Host.Diagnostics;

/// <summary>
/// Dumps every mapped endpoint's route pattern and metadata type names. Used by A0 to confirm
/// which marker MapHub&lt;T&gt;() actually attaches to the negotiate endpoint, and by A3 to
/// confirm hub-class attributes (e.g. AuthorizeAttribute) are copied onto it. Reflects the
/// *originally mapped* endpoints (EndpointDataSource), not the policy's runtime replacement.
/// </summary>
public static class EndpointDumpEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/__diag/endpoints", (EndpointDataSource dataSource) =>
            Results.Json(dataSource.Endpoints.Select(e => new
            {
                e.DisplayName,
                RoutePattern = (e as RouteEndpoint)?.RoutePattern.RawText,
                Metadata = e.Metadata.Select(m => m.GetType().FullName).ToArray()
            })));
    }
}
