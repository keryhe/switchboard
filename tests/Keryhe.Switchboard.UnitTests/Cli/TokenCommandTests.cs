using System.IdentityModel.Tokens.Jwt;
using Keryhe.Switchboard.Server.Cli;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Cli;

public class TokenCommandTests
{
    [Fact]
    public void Generate_AppServerRole_ProducesValidServerToken()
    {
        var (exitCode, output) = RunCapturingStdout([
            "token", "generate",
            "--role", "appserver",
            "--server-id", "chat-api-1",
            "--hubs", "chatHub,notificationHub",
            "--ttl", "24h",
            "--key", "dev-only-server-signing-key-change-me-32+",
        ]);

        Assert.Equal(0, exitCode);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(output.Trim());
        Assert.Equal("appserver", jwt.Claims.First(c => c.Type == "role").Value);
        Assert.Equal(["chatHub", "notificationHub"], jwt.Claims.Where(c => c.Type == "hubs").Select(c => c.Value));
        Assert.Equal("chat-api-1", jwt.Claims.First(c => c.Type == "sub").Value);
    }

    [Fact]
    public void Generate_ManagementRole_ProducesValidManagementToken()
    {
        var (exitCode, output) = RunCapturingStdout([
            "token", "generate",
            "--role", "management",
            "--subject", "ops-dashboard",
            "--ttl", "1h",
            "--key", "dev-only-management-signing-key-change-me-32+",
        ]);

        Assert.Equal(0, exitCode);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(output.Trim());
        Assert.Equal("management", jwt.Claims.First(c => c.Type == "role").Value);
        Assert.Equal("ops-dashboard", jwt.Claims.First(c => c.Type == "sub").Value);
    }

    [Fact]
    public void Generate_MissingRequiredFlag_ReturnsNonZeroExitCode()
    {
        var (exitCode, _) = RunCapturingStdout([
            "token", "generate",
            "--role", "appserver",
            "--key", "dev-only-server-signing-key-change-me-32+",
        ]);

        Assert.NotEqual(0, exitCode);
    }

    private static (int ExitCode, string Output) RunCapturingStdout(string[] args)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            var exitCode = TokenCommand.Run(args);
            return (exitCode, writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
