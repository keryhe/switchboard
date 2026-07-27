using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Keryhe.Switchboard.Server.Security;

/// <summary>
/// Issues and validates all three independent token types (ADR-004). Never falls back to a
/// different token type's signing key — an app-server token must never validate as a client
/// token or vice versa. Each type additionally supports a "…Fallback" key for zero-downtime
/// rotation: validation tries the primary key first, then the fallback if configured.
/// </summary>
public sealed class JwtTokenService(IOptions<SwitchboardOptions> options) : ITokenService
{
    private readonly SwitchboardOptions _options = options.Value;

    // Disable the legacy short-to-long inbound claim type mapping (e.g. "role" -> the
    // http://schemas.microsoft.com/ws/2008/06/identity/claims/role URI) so validated principals
    // carry exactly the claim types this service issued ("sub", "role", "hubs", ...), matching
    // the claim names documented in 04-design.md §1 and 03-protocol.md §2.1 verbatim.
    private readonly JwtSecurityTokenHandler _handler = new() { MapInboundClaims = false };

    public string IssueClientToken(string connectionId, string hubName, string? userId, IEnumerable<Claim>? extraClaims, TimeSpan expiry)
    {
        var claims = new List<Claim>
        {
            new("connectionId", connectionId),
            new("hubName", hubName),
        };

        if (userId is not null)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, userId));
        }

        if (extraClaims is not null)
        {
            claims.AddRange(extraClaims);
        }

        return WriteToken(claims, _options.TokenSigningKey, _options.TokenIssuer, _options.TokenAudience, expiry);
    }

    public string IssueServerToken(string serverId, IEnumerable<string> hubs, TimeSpan expiry)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, serverId),
            new("role", "appserver"),
        };

        claims.AddRange(hubs.Select(h => new Claim("hubs", h)));

        return WriteToken(claims, _options.ServerSigningKey, _options.TokenIssuer, _options.ServerAudience, expiry);
    }

    public string IssueManagementToken(string subject, TimeSpan expiry)
    {
        if (_options.ManagementSigningKey is null)
        {
            throw new InvalidOperationException($"{nameof(SwitchboardOptions.ManagementSigningKey)} is not configured.");
        }

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new("role", "management"),
        };

        return WriteToken(claims, _options.ManagementSigningKey, _options.TokenIssuer, _options.ManagementAudience, expiry);
    }

    public ClaimsPrincipal? Validate(string token, SwitchboardTokenType expectedType)
    {
        var (audience, keys, requiredRole) = expectedType switch
        {
            SwitchboardTokenType.Client => (_options.TokenAudience, Keys(_options.TokenSigningKey, null), (string?)null),
            SwitchboardTokenType.Server => (_options.ServerAudience, Keys(_options.ServerSigningKey, _options.ServerSigningKeyFallback), "appserver"),
            SwitchboardTokenType.Management => (_options.ManagementAudience, Keys(_options.ManagementSigningKey, _options.ManagementSigningKeyFallback), "management"),
            _ => throw new ArgumentOutOfRangeException(nameof(expectedType)),
        };

        foreach (var key in keys)
        {
            var parameters = new TokenValidationParameters
            {
                ValidIssuer = _options.TokenIssuer,
                ValidAudience = audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(5),
            };

            try
            {
                var principal = _handler.ValidateToken(token, parameters, out _);

                if (requiredRole is not null && principal.FindFirst("role")?.Value != requiredRole)
                {
                    return null;
                }

                return principal;
            }
            catch (Exception ex) when (ex is SecurityTokenException or ArgumentException or FormatException)
            {
                // Malformed / wrong-signature / wrong-audience token: try the next key (fallback),
                // or fall through to null if none validate. Anything else is a real bug, not a
                // "this token isn't this type" signal, so it is allowed to propagate.
            }
        }

        return null;
    }

    private static IEnumerable<string> Keys(string? primary, string? fallback)
    {
        if (primary is not null)
        {
            yield return primary;
        }

        if (fallback is not null)
        {
            yield return fallback;
        }
    }

    private string WriteToken(IReadOnlyList<Claim> claims, string signingKey, string issuer, string audience, TimeSpan expiry)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: now,
            expires: now.Add(expiry),
            signingCredentials: credentials);

        return _handler.WriteToken(token);
    }
}
