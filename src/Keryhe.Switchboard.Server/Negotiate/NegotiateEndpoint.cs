using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Server.Negotiate;

/// <summary>
/// POST /{hub}/negotiate. Dispatches between the two negotiate steps purely on the *validated*
/// token type (plan decision D1) — never on a caller-controlled query parameter or header, since
/// getting this wrong lets a client token mint redirects or an app-server token claim a client
/// connection.
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
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();

        var serverPrincipal = tokenService.Validate(token, SwitchboardTokenType.Server);
        if (serverPrincipal is not null)
        {
            await HandleStep1Async(context, hub, serverPrincipal, negotiationService, options.Value);
            return;
        }

        var clientPrincipal = tokenService.Validate(token, SwitchboardTokenType.Client);
        if (clientPrincipal is not null)
        {
            await HandleStep2Async(context, hub, clientPrincipal, negotiationService);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }

    private static async Task HandleStep1Async(
        HttpContext context,
        string hub,
        ClaimsPrincipal serverPrincipal,
        INegotiationService negotiationService,
        SwitchboardOptions options)
    {
        var hubs = serverPrincipal.FindAll("hubs").Select(c => c.Value).ToHashSet();
        if (!hubs.Contains(hub))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        string? userId = context.Request.Headers.TryGetValue(options.TrustedIdentityHeader, out var userIdValues)
            ? userIdValues.ToString()
            : null;

        List<Claim>? claims = null;
        if (context.Request.Headers.TryGetValue(options.TrustedClaimsHeader, out var claimsHeader) && claimsHeader.Count > 0)
        {
            claims = DecodeClaims(claimsHeader.ToString());
        }

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

    private static List<Claim> DecodeClaims(string base64)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        return dict.Select(kv => new Claim(kv.Key, kv.Value)).ToList();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
