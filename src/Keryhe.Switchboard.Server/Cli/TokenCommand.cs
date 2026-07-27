using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Server.Security;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Server.Cli;

/// <summary>
/// <c>dotnet switchboard token generate --role appserver|management ...</c> (03-protocol.md §2.1).
/// Server and management tokens are pre-generated offline, not minted at runtime.
/// </summary>
public static class TokenCommand
{
    public static int Run(string[] args)
    {
        // args[0] == "token", args[1] == "generate"
        if (args.Length < 2 || args[0] != "token" || args[1] != "generate")
        {
            PrintUsage();
            return 1;
        }

        var flags = ParseFlags(args[2..]);

        if (!flags.TryGetValue("role", out var role) || !flags.TryGetValue("key", out var key))
        {
            Console.Error.WriteLine("Error: --role and --key are required.");
            PrintUsage();
            return 1;
        }

        var ttl = flags.TryGetValue("ttl", out var ttlText) ? ParseTtl(ttlText) : TimeSpan.FromHours(24);

        var options = Options.Create(new SwitchboardOptions
        {
            PublicUrl = "unused",
            TokenSigningKey = "unused-unused-unused-unused-unused",
            ServerSigningKey = role == "appserver" ? key : "unused-unused-unused-unused-unused",
            ManagementSigningKey = role == "management" ? key : null,
        });

        ITokenService tokenService = new JwtTokenService(options);

        string token;
        switch (role)
        {
            case "appserver":
                if (!flags.TryGetValue("server-id", out var serverId))
                {
                    Console.Error.WriteLine("Error: --server-id is required for --role appserver.");
                    return 1;
                }

                var hubs = flags.TryGetValue("hubs", out var hubsText)
                    ? hubsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [];

                if (hubs.Length == 0)
                {
                    Console.Error.WriteLine("Error: --hubs is required for --role appserver (comma-separated).");
                    return 1;
                }

                token = tokenService.IssueServerToken(serverId, hubs, ttl);
                break;

            case "management":
                if (!flags.TryGetValue("subject", out var subject))
                {
                    Console.Error.WriteLine("Error: --subject is required for --role management.");
                    return 1;
                }

                token = tokenService.IssueManagementToken(subject, ttl);
                break;

            default:
                Console.Error.WriteLine($"Error: unknown --role '{role}'. Expected 'appserver' or 'management'.");
                return 1;
        }

        Console.WriteLine(token);
        return 0;
    }

    private static Dictionary<string, string> ParseFlags(string[] args)
    {
        var flags = new Dictionary<string, string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                flags[args[i][2..]] = args[i + 1];
                i++;
            }
        }

        return flags;
    }

    private static TimeSpan ParseTtl(string text)
    {
        var unit = text[^1];
        var value = double.Parse(text[..^1]);
        return unit switch
        {
            's' => TimeSpan.FromSeconds(value),
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            _ => throw new FormatException($"Unrecognized TTL '{text}'. Expected a number followed by s/m/h/d, e.g. '24h'."),
        };
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            Usage:
              dotnet switchboard token generate --role appserver --server-id <id> --hubs <hub1,hub2> --ttl 24h --key <ServerSigningKey>
              dotnet switchboard token generate --role management --subject <name> --ttl 24h --key <ManagementSigningKey>
            """);
    }
}
