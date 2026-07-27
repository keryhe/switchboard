using System.Security.Claims;
using Keryhe.Switchboard.Core.Models;

namespace Keryhe.Switchboard.Core;

public interface INegotiationService
{
    /// <summary>Step 1 — issue the redirect: mint the access-token JWT and return this service's https URL.</summary>
    Task<RedirectResponse> IssueRedirectAsync(string hubName, string? userId, IEnumerable<Claim>? claims, CancellationToken ct);

    /// <summary>Step 2 — the client re-negotiates here presenting the step-1 token; mint the opaque connectionToken.</summary>
    Task<NegotiateResponse> NegotiateAsync(string hubName, ClaimsPrincipal accessToken, CancellationToken ct);
}
