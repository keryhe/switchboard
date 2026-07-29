using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Management;

/// <summary>
/// Guards every <c>/api/v1</c> route (Phase 4 plan decision D21). Hand-rolled against
/// <see cref="ITokenService"/> rather than <c>AddAuthentication</c>/<c>AddJwtBearer</c> —
/// matching every other authenticated endpoint in this service (<c>ServerConnectionEndpoint</c>,
/// <c>NegotiateEndpoint</c>), which validate the same way, so the three-token model (ADR-004)
/// stays in one place rather than half hand-rolled and half middleware-configured.
/// </summary>
/// <remarks>
/// Network allowlist checked before the token (plan decision D29): cheaper, and a peer outside
/// <see cref="SwitchboardOptions.ManagementAllowedNetworks"/> is rejected with <c>403</c> — distinct
/// from a missing/invalid token's <c>401</c>, since the network check runs regardless of whether a
/// token was even presented. An empty allowlist (the default) skips the check entirely: the
/// management token remains the real control (03-protocol.md Part 3), and the network allowlist is
/// defence in depth on top of it, not a substitute for it.
/// </remarks>
public sealed class ManagementAuthFilter(ITokenService tokenService, IOptions<SwitchboardOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var opts = options.Value;

        if (opts.ManagementAllowedNetworks.Length > 0 &&
            !PeerNetworkMatcher.IsTrustedPeer(http.Connection.RemoteIpAddress, opts.ManagementAllowedNetworks))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var authHeader = http.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Unauthorized();
        }

        // A wrong-type token (client or server) fails validation here at the audience/signing-key
        // check before role is ever considered — cryptographically indistinguishable from a garbage
        // token, so this is 401, not 403 (Phase 4 plan decision D21; 03-protocol.md Part 3).
        var principal = tokenService.Validate(authHeader["Bearer ".Length..].Trim(), SwitchboardTokenType.Management);
        if (principal is null)
        {
            return Results.Unauthorized();
        }

        http.User = principal;
        return await next(context);
    }
}
