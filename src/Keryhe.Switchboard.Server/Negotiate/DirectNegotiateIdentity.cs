using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;

namespace Keryhe.Switchboard.Server.Negotiate;

/// <summary>
/// Pattern A's trust boundary (04-design.md §1, plan decision D11): CIDR matching against the
/// immediate peer, and stripping — not merely ignoring — the identity headers for anyone outside
/// the allowlist, so a spoofed header can never survive into claims.
/// </summary>
public static class DirectNegotiateIdentity
{
    /// <summary>
    /// Matches the directly connected peer address (04-design.md §1 rule 4 — never
    /// <c>X-Forwarded-For</c>, which is spoofable by the very thing this check guards against,
    /// unless a separate <c>ForwardedHeadersMiddleware</c> allowlist has already normalized
    /// <see cref="HttpContext.Connection"/> itself) against the configured CIDR ranges. Delegates
    /// to <see cref="PeerNetworkMatcher"/>, shared with the management API's own network allowlist
    /// (Phase 4 plan decision D29).
    /// </summary>
    public static bool IsTrustedPeer(IPAddress? remoteIpAddress, IReadOnlyList<string> trustedProxyNetworks) =>
        PeerNetworkMatcher.IsTrustedPeer(remoteIpAddress, trustedProxyNetworks);

    /// <summary>
    /// Reads the identity headers only when <paramref name="trusted"/> — otherwise they are
    /// removed from the request before anything else can observe them (04-design.md §1 rule 3:
    /// "stripped before processing", not merely ignored) and the connection is anonymous.
    /// </summary>
    public static (string? UserId, List<Claim>? Claims) ExtractIdentity(HttpContext context, SwitchboardOptions options, bool trusted)
    {
        if (!trusted)
        {
            context.Request.Headers.Remove(options.TrustedIdentityHeader);
            context.Request.Headers.Remove(options.TrustedClaimsHeader);
            return (null, null);
        }

        var userId = context.Request.Headers.TryGetValue(options.TrustedIdentityHeader, out var userIdValues)
            ? userIdValues.ToString()
            : null;

        List<Claim>? claims = null;
        if (context.Request.Headers.TryGetValue(options.TrustedClaimsHeader, out var claimsHeader) && claimsHeader.Count > 0)
        {
            claims = DecodeClaims(claimsHeader.ToString());
        }

        return (userId, claims);
    }

    private static List<Claim> DecodeClaims(string base64)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        return dict.Select(kv => new Claim(kv.Key, kv.Value)).ToList();
    }
}
