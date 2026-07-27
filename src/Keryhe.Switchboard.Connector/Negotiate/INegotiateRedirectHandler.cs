using Microsoft.AspNetCore.Http;

namespace Keryhe.Switchboard.Connector.Negotiate;

/// <summary>
/// Produces the redirect response for a negotiate endpoint that
/// <see cref="SwitchboardNegotiateMatcherPolicy"/> has taken over. Registered by the host
/// (in Phase 1, this becomes the real call to the Switchboard proxy's negotiate endpoint).
/// </summary>
public interface INegotiateRedirectHandler
{
    Task HandleAsync(HttpContext context, string hubName);
}
