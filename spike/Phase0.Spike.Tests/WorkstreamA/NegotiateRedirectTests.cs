using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Phase0.Spike.Host.Negotiate;

namespace Phase0.Spike.Tests.WorkstreamA;

/// <summary>
/// A2/A3: the redirect is actually returned over real HTTP, through the real ASP.NET Core
/// routing pipeline, and class-level [Authorize] on SecureHub survives the policy swap.
/// </summary>
public class NegotiateRedirectTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task TestHub_negotiate_returns_the_redirect_body_not_the_frameworks_response()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsync("/testHub/negotiate?negotiateVersion=1", null);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.TryGetProperty("url", out var url));
        Assert.True(body.TryGetProperty("accessToken", out _));

        // The absence check matters more than the presence check: if the framework's own
        // negotiate delegate had run instead of (or in addition to) the policy's replacement,
        // these fields would be present.
        Assert.False(body.TryGetProperty("connectionId", out _), "framework's negotiate response leaked through");
        Assert.False(body.TryGetProperty("availableTransports", out _), "framework's negotiate response leaked through");
        Assert.StartsWith("http", url.GetString());
        Assert.EndsWith("/stub/testHub", url.GetString());
    }

    [Fact]
    public async Task SecureHub_negotiate_without_token_is_401_and_not_redirected()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsync("/secureHub/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("accessToken", body);
    }

    [Fact]
    public async Task SecureHub_negotiate_with_valid_token_is_redirected()
    {
        var client = factory.CreateClient();
        var token = JwtIssuer.IssueClientToken(Guid.NewGuid().ToString(), "secureHub", "alice");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync("/secureHub/negotiate?negotiateVersion=1", null);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("url", out _));
        Assert.True(body.TryGetProperty("accessToken", out _));
    }

    [Fact]
    public async Task Originally_mapped_secureHub_negotiate_endpoint_carries_AuthorizeAttribute()
    {
        // Confirms MapHub<T>() actually copies hub-class attributes onto the negotiate endpoint
        // in this .NET version -- the precondition PolicyMetadataPreservationTests assumes.
        var client = factory.CreateClient();

        var dump = await client.GetFromJsonAsync<JsonElement[]>("/__diag/endpoints");
        var secureNegotiate = dump!.First(e =>
            e.GetProperty("routePattern").GetString() == "/secureHub/negotiate");

        var metadataTypes = secureNegotiate.GetProperty("metadata")
            .EnumerateArray()
            .Select(m => m.GetString())
            .ToArray();

        Assert.Contains("Microsoft.AspNetCore.Http.Connections.NegotiateMetadata", metadataTypes);
        Assert.Contains("Microsoft.AspNetCore.Authorization.AuthorizeAttribute", metadataTypes);
    }
}
