using System.Security.Claims;

namespace Phase0.Spike.Connector.Dispatch;

/// <summary>
/// Rebuilds the ClaimsPrincipal the app server trusts from envelope fields, matching
/// docs/docs/04-design.md §11. ClaimTypes.NameIdentifier is synthesized from userId
/// deliberately — it keeps Context.UserIdentifier (via the default IUserIdProvider) aligned
/// with the service's own user index, so Clients.User(...) resolves consistently on both sides.
/// </summary>
public static class IdentityReconstruction
{
    public static ClaimsPrincipal Build(string? userId, IReadOnlyDictionary<string, string>? claims)
    {
        var identityClaims = new List<Claim>();

        if (userId is not null)
        {
            identityClaims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        }

        if (claims is not null)
        {
            foreach (var (type, value) in claims)
            {
                identityClaims.Add(new Claim(type, value));
            }
        }

        // ClaimsIdentity.IsAuthenticated is governed solely by authenticationType being
        // non-null/non-empty -- NOT by whether there are any claims (confirmed empirically in
        // the Phase 0 spike, Workstream B/B3). The design doc's original sketch used a
        // constant "Switchboard" authenticationType unconditionally, which would make every
        // anonymous connection (no userId, no claims) silently pass [Authorize] checks. Only
        // mark the identity authenticated when there is an actual identity to assert.
        var authenticationType = userId is not null ? "Switchboard" : null;
        return new ClaimsPrincipal(new ClaimsIdentity(identityClaims, authenticationType));
    }
}
