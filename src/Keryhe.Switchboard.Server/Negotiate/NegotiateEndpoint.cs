using System.Security.Claims;
using System.Text.Json;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Server.Negotiate;

/// <summary>
/// POST /{hub}/negotiate. Dispatches purely on the *validated* token type (plan decision D1) —
/// never on a caller-controlled query parameter or header — extended by plan decision D11 for
/// Pattern A (service-direct negotiate, 04-design.md §1): a request with no valid token at all
/// falls through to the network allowlist only when <c>EnableDirectNegotiate</c> is on, and even
/// then the allowlist governs whether asserted identity is <em>believed</em>, never whether the
/// endpoint answers — a request from outside <c>TrustedProxyNetworks</c> still gets a connection,
/// just an anonymous one, with the identity headers stripped rather than merely ignored.
/// Evaluation order, matching D11 exactly:
/// 1. Valid server token → Pattern B step 1, identity headers trusted unconditionally (the trust
///    boundary is the token, not the network).
/// 2. Valid client token → step 2.
/// 3. No valid token, direct negotiate enabled, peer inside the allowlist → Pattern A step 1,
///    identity headers trusted.
/// 4. No valid token, direct negotiate enabled, peer outside → Pattern A step 1, anonymous.
/// 5. Otherwise → 401.
/// </summary>
public static class NegotiateEndpoint
{
    public static async Task HandleAsync(
        HttpContext context,
        string hub,
        INegotiationService negotiationService,
        ITokenService tokenService,
        IOptions<SwitchboardOptions> options)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : null;

        if (!string.IsNullOrEmpty(token))
        {
            var serverPrincipal = tokenService.Validate(token, SwitchboardTokenType.Server);
            if (serverPrincipal is not null)
            {
                var requiredHubs = serverPrincipal.FindAll("hubs").Select(c => c.Value).ToHashSet();
                await HandleStep1Async(context, hub, requiredHubs, negotiationService, options.Value, trusted: true);
                return;
            }

            var clientPrincipal = tokenService.Validate(token, SwitchboardTokenType.Client);
            if (clientPrincipal is not null)
            {
                await HandleStep2Async(context, hub, clientPrincipal, negotiationService);
                return;
            }
        }

        if (!options.Value.EnableDirectNegotiate)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var trustedPeer = DirectNegotiateIdentity.IsTrustedPeer(context.Connection.RemoteIpAddress, options.Value.TrustedProxyNetworks);
        await HandleStep1Async(context, hub, requiredHubs: null, negotiationService, options.Value, trusted: trustedPeer);
    }

    private static async Task HandleStep1Async(
        HttpContext context,
        string hub,
        IReadOnlySet<string>? requiredHubs,
        INegotiationService negotiationService,
        SwitchboardOptions options,
        bool trusted)
    {
        // Only Pattern B (a server token) carries a hub restriction to check — Pattern A has no
        // token to carry one, matching 04-design.md §1's "identical to Pattern B, with the
        // service playing both roles."
        if (requiredHubs is not null && !requiredHubs.Contains(hub))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var (userId, claims) = DirectNegotiateIdentity.ExtractIdentity(context, options, trusted);

        var redirect = await negotiationService.IssueRedirectAsync(hub, userId, claims, context.RequestAborted);

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, redirect, JsonOptions, context.RequestAborted);
    }

    private static async Task HandleStep2Async(
        HttpContext context,
        string hub,
        ClaimsPrincipal clientPrincipal,
        INegotiationService negotiationService)
    {
        var tokenHubName = clientPrincipal.FindFirst("hubName")?.Value;
        if (!string.Equals(tokenHubName, hub, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        NegotiateResponse response;
        try
        {
            response = await negotiationService.NegotiateAsync(hub, clientPrincipal, context.RequestAborted);
        }
        catch (NoServerConnectionException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var negotiationResponse = new NegotiationResponse
        {
            ConnectionId = response.ConnectionId,
            ConnectionToken = response.ConnectionToken,
            Version = response.NegotiateVersion,
            AvailableTransports = response.AvailableTransports
                .Select(t => new Microsoft.AspNetCore.Http.Connections.AvailableTransport
                {
                    Transport = t.Transport,
                    TransferFormats = t.TransferFormats.ToList(),
                })
                .ToList(),
        };

        context.Response.ContentType = "application/json";
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        NegotiateProtocol.WriteResponse(negotiationResponse, writer);
        await context.Response.Body.WriteAsync(writer.WrittenMemory, context.RequestAborted);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
