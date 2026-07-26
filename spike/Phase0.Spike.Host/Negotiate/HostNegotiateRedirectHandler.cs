using System.Security.Claims;
using Phase0.Spike.Connector.Negotiate;

namespace Phase0.Spike.Host.Negotiate;

/// <summary>
/// Stands in for the real call to the Switchboard proxy's negotiate endpoint
/// (docs/docs/04-design.md §8 step 2). Reads the authenticated caller's identity, mints a
/// short-lived client JWT, and returns the redirect body pointing at the spike's stub target.
/// </summary>
public sealed class HostNegotiateRedirectHandler : INegotiateRedirectHandler
{
    public async Task HandleAsync(HttpContext context, string hubName)
    {
        var connectionId = Guid.NewGuid().ToString();
        var userId = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User.Identity?.Name
            : null;

        var accessToken = JwtIssuer.IssueClientToken(connectionId, hubName, userId);
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            url = $"{baseUrl}/stub/{hubName}",
            accessToken
        });
    }
}
