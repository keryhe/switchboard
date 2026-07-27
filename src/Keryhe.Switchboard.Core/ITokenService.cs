using System.Security.Claims;

namespace Keryhe.Switchboard.Core;

public enum SwitchboardTokenType { Client, Server, Management }

public interface ITokenService
{
    string IssueClientToken(string connectionId, string hubName, string? userId, IEnumerable<Claim>? extraClaims, TimeSpan expiry);

    string IssueServerToken(string serverId, IEnumerable<string> hubs, TimeSpan expiry);

    string IssueManagementToken(string subject, TimeSpan expiry);

    /// <summary>Validates a bearer token against the signing key(s) for <paramref name="expectedType"/> only.
    /// Never falls back to a different token type's key — an app-server token must never validate as a client token or vice versa (ADR-004).</summary>
    ClaimsPrincipal? Validate(string token, SwitchboardTokenType expectedType);
}
