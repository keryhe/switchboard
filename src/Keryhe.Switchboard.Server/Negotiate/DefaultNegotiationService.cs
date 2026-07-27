using System.Security.Claims;
using System.Security.Cryptography;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.Registry;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Server.Negotiate;

/// <summary>Implements the two-step redirect negotiate flow (03-protocol.md §1.1, 04-design.md §1).</summary>
public sealed class DefaultNegotiationService(
    ITokenService tokenService,
    IHubRegistry hubRegistry,
    IPendingConnectionStore pendingConnections,
    IOptions<SwitchboardOptions> options) : INegotiationService
{
    private readonly SwitchboardOptions _options = options.Value;

    public Task<RedirectResponse> IssueRedirectAsync(string hubName, string? userId, IEnumerable<Claim>? claims, CancellationToken ct)
    {
        var connectionId = Guid.NewGuid().ToString("n");
        var accessToken = tokenService.IssueClientToken(connectionId, hubName, userId, claims, _options.ClientTokenExpiry);

        var url = $"{ToHttpScheme(_options.PublicUrl)}/{hubName}";

        return Task.FromResult(new RedirectResponse { Url = url, AccessToken = accessToken });
    }

    public Task<NegotiateResponse> NegotiateAsync(string hubName, ClaimsPrincipal accessToken, CancellationToken ct)
    {
        // D5: fail fast rather than wait/queue — this is the earliest point the service knows
        // whether any app server can service the hub.
        if (!hubRegistry.HasActiveServerConnection(hubName))
        {
            throw new NoServerConnectionException(hubName);
        }

        var connectionId = accessToken.FindFirst("connectionId")?.Value
            ?? throw new InvalidOperationException("Client token is missing the connectionId claim.");
        var userId = accessToken.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? accessToken.FindFirst("sub")?.Value;

        var connectionToken = GenerateOpaqueToken();

        var claims = accessToken.Claims
            .Where(c => c.Type is not ("connectionId" or "hubName" or "sub" or "exp" or "iat" or "nbf" or "iss" or "aud"))
            .ToDictionary(c => c.Type, c => c.Value);

        pendingConnections.Add(new PendingConnection(
            connectionToken,
            connectionId,
            hubName,
            userId,
            claims.Count > 0 ? claims : null,
            DateTimeOffset.UtcNow.Add(_options.ClientTokenExpiry)));

        // Phase 1 is WebSocket-only (see plan §4 Slice 4) — advertising SSE/LongPolling here would
        // be false, since neither transport is implemented yet.
        var response = new NegotiateResponse
        {
            ConnectionId = connectionId,
            ConnectionToken = connectionToken,
            NegotiateVersion = 1,
            AvailableTransports =
            [
                new AvailableTransport { Transport = "WebSockets", TransferFormats = ["Text", "Binary"] },
            ],
        };

        return Task.FromResult(response);
    }

    private static string GenerateOpaqueToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string ToHttpScheme(string publicUrl) =>
        publicUrl switch
        {
            _ when publicUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) => "https://" + publicUrl["wss://".Length..],
            _ when publicUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) => "http://" + publicUrl["ws://".Length..],
            _ => publicUrl,
        };
}
