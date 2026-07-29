using System.Security.Claims;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.Registry;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// Token/pending-connection validation shared by every transport's request handler (WebSocket GET
/// upgrade, SSE GET/POST/DELETE, Long Polling GET/POST/DELETE) — factored out once a second
/// transport needed the exact same checks the WebSocket endpoint already did.
/// </summary>
public static class ClientConnectionValidation
{
    /// <summary>
    /// The .NET SignalR client sets the Authorization header directly on the WebSocket upgrade
    /// (it isn't restricted like a browser); browser clients cannot, so they fall back to the
    /// access_token query parameter (03-protocol.md §1.2) — accept either, on every request type.
    /// </summary>
    public static string ExtractAccessToken(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        return authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : context.Request.Query["access_token"].ToString();
    }

    /// <summary>Validates the client access token for a request against an already-established
    /// connection (SSE/Long Polling POST send and DELETE close) — no pending-connection lookup,
    /// since that one-shot entry was already consumed by the request that established the
    /// connection.</summary>
    public static ClaimsPrincipal? ValidateAccessToken(HttpContext context, string hub, ITokenService tokenService)
    {
        var accessToken = ExtractAccessToken(context);
        var principal = tokenService.Validate(accessToken, SwitchboardTokenType.Client);
        if (principal is null)
        {
            return null;
        }

        return principal.FindFirst("hubName")?.Value == hub ? principal : null;
    }

    /// <summary>
    /// Browsers do not preflight a WebSocket upgrade, so <c>app.UseCors</c> never sees it — the
    /// upgrade handler is the only place left to enforce <c>AllowedOrigins</c> for that transport
    /// (Slice 9, plan §4). Absent when the request has no <c>Origin</c> header at all (the .NET
    /// client and other non-browser callers never send one) and whenever <paramref name="allowedOrigins"/>
    /// is empty (matching CORS's own "empty policy" being permissive, not a lockout).
    /// </summary>
    public static bool IsOriginAllowed(HttpContext context, IReadOnlyList<string> allowedOrigins)
    {
        if (allowedOrigins.Count == 0)
        {
            return true;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        return allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
    }

    public readonly record struct EstablishResult(int? ErrorStatusCode, PendingConnection? Pending, string? ServerConnectionRef)
    {
        public bool Success => ErrorStatusCode is null;
    }

    /// <summary>Validates the token, consumes the one-shot pending-connection entry, and assigns
    /// an app server connection — the full set of checks needed to establish a brand-new client
    /// connection, regardless of which transport is upgrading it. The assignment (plan decision
    /// D18, Phase 3 Slice 4) is a <see cref="Core.ServerConnectionRef"/>-formatted string, not a
    /// live object — it may name a connection on another node.</summary>
    public static async Task<EstablishResult> EstablishAsync(
        HttpContext context,
        string hub,
        string connectionToken,
        ITokenService tokenService,
        IPendingConnectionStore pendingConnections,
        IServerConnectionSelector serverConnectionSelector,
        string localNodeId,
        SwitchboardMetrics metrics)
    {
        var accessToken = ExtractAccessToken(context);
        var principal = tokenService.Validate(accessToken, SwitchboardTokenType.Client);
        if (principal is null)
        {
            return new EstablishResult(StatusCodes.Status401Unauthorized, null, null);
        }

        if (principal.FindFirst("hubName")?.Value != hub)
        {
            return new EstablishResult(StatusCodes.Status403Forbidden, null, null);
        }

        var pending = await pendingConnections.TryConsumeAsync(connectionToken, context.RequestAborted);
        if (pending is not null)
        {
            // Removed from the store by TryConsumeAsync above regardless of what happens next, so
            // this is "consumed" (plan decision D28) even if the hub-mismatch check below still
            // rejects the request.
            metrics.PendingConnectionsConsumed.Add(1);
        }

        if (pending is null || pending.HubName != hub)
        {
            return new EstablishResult(StatusCodes.Status401Unauthorized, null, null);
        }

        var serverConnectionRef = await serverConnectionSelector.AssignConnectionAsync(hub, localNodeId, context.RequestAborted);
        if (serverConnectionRef is null)
        {
            return new EstablishResult(StatusCodes.Status503ServiceUnavailable, null, null);
        }

        return new EstablishResult(null, pending, serverConnectionRef);
    }
}
