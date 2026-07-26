using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Phase0.Spike.Host.Negotiate;

/// <summary>
/// Mints the step-1 client JWT shape described in docs/docs/04-design.md §1 (connectionId,
/// hubName, sub, iss, aud, short exp). The stub target doesn't validate this token — it's
/// generated for wire-shape fidelity, not because the spike's stub enforces it.
/// </summary>
public static class JwtIssuer
{
    public static string IssueClientToken(string connectionId, string hubName, string? userId)
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

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestConstants.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: TestConstants.Issuer,
            audience: TestConstants.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddSeconds(60),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
