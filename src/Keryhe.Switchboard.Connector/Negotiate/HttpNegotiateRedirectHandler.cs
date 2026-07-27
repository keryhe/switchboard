using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Connector.Negotiate;

/// <summary>
/// Real implementation of the negotiate redirect: forwards the app server's negotiate request to
/// the Switchboard service and relays its redirect response verbatim (04-design.md §8).
/// </summary>
public sealed class HttpNegotiateRedirectHandler(
    IHttpClientFactory httpClientFactory,
    IOptions<SwitchboardConnectorOptions> options,
    ILogger<HttpNegotiateRedirectHandler> logger) : INegotiateRedirectHandler
{
    public async Task HandleAsync(HttpContext context, string hubName)
    {
        var client = httpClientFactory.CreateClient("switchboard-negotiate");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{hubName}/negotiate?negotiateVersion=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ServerAccessToken);

        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.Identity.Name;
            if (userId is not null)
            {
                request.Headers.Add("X-Switchboard-UserId", userId);
            }

            var claims = user.Claims
                .Where(c => c.Type != ClaimTypes.NameIdentifier)
                .ToDictionary(c => c.Type, c => c.Value);
            if (claims.Count > 0)
            {
                var json = JsonSerializer.Serialize(claims);
                request.Headers.Add("X-Switchboard-Claims", Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, context.RequestAborted);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "Failed to reach Switchboard service for negotiate on hub '{HubName}'.", hubName);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "5";
            return;
        }

        context.Response.StatusCode = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        context.Response.ContentType = "application/json";
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}
