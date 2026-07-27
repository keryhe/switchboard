using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Server.Security;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Security;

public class JwtTokenServiceTests
{
    private static JwtTokenService CreateService()
    {
        var options = new SwitchboardOptions
        {
            PublicUrl = "http://localhost:5000",
            TokenSigningKey = "dev-only-client-signing-key-change-me-32+",
            ServerSigningKey = "dev-only-server-signing-key-change-me-32+",
        };
        return new JwtTokenService(Options.Create(options));
    }

    [Fact]
    public void ServerToken_RoundTrips()
    {
        var service = CreateService();
        var token = service.IssueServerToken("test-server", ["chatHub"], TimeSpan.FromHours(1));

        var principal = service.Validate(token, SwitchboardTokenType.Server);

        Assert.NotNull(principal);
        Assert.Equal("appserver", principal!.FindFirst("role")?.Value);
        Assert.Contains("chatHub", principal.FindAll("hubs").Select(c => c.Value));
    }

    [Fact]
    public void ClientToken_RoundTrips()
    {
        var service = CreateService();
        var token = service.IssueClientToken("conn-1", "chatHub", "alice", null, TimeSpan.FromSeconds(60));

        var principal = service.Validate(token, SwitchboardTokenType.Client);

        Assert.NotNull(principal);
        Assert.Equal("conn-1", principal!.FindFirst("connectionId")?.Value);
        Assert.Equal("chatHub", principal.FindFirst("hubName")?.Value);
    }

    [Fact]
    public void ServerToken_DoesNotValidateAsClientToken()
    {
        var service = CreateService();
        var token = service.IssueServerToken("test-server", ["chatHub"], TimeSpan.FromHours(1));

        Assert.Null(service.Validate(token, SwitchboardTokenType.Client));
    }
}
