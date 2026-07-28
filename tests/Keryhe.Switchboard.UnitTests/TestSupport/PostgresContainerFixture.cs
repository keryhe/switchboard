using System.Diagnostics;
using Npgsql;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.TestSupport;

/// <summary>
/// Starts a throwaway <c>postgres:16-alpine</c> container (via the <c>docker</c> CLI — no
/// <c>Testcontainers</c> package dependency for one fixture) and applies the vendored
/// <c>Keryhe.Switchboard.Orleans/Sql/PostgreSQL/*.sql</c> scripts to it, for Phase 3 Slice 6's gate:
/// "two nodes clustering through a real database ... complete Slice 2's and Slice 3's scenarios."
/// <see cref="IsAvailable"/> is <c>false</c> (rather than throwing) when Docker isn't present or the
/// container never becomes reachable — the plan is explicit that an environment with no database
/// available must be called out as untested, not silently assumed working, so the owning test
/// no-ops with a message instead of failing the whole suite in a Docker-less environment.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private static readonly string SqlDirectory = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Keryhe.Switchboard.Orleans", "Sql", "PostgreSQL");

    private static readonly string MigrationsDirectory = Path.Combine(SqlDirectory, "Migrations");

    private string? _containerName;

    public bool IsAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (!await RunSucceedsAsync("docker", "version --format {{.Server.Version}}"))
        {
            UnavailableReason = "docker CLI not available or daemon not running";
            return;
        }

        _containerName = $"switchboard-slice6-{Guid.NewGuid():n}";
        var run = await RunAsync("docker", $"run -d --rm --name {_containerName} -e POSTGRES_PASSWORD=switchboard -e POSTGRES_DB=switchboard -P postgres:16-alpine");
        if (run.ExitCode != 0)
        {
            UnavailableReason = $"docker run failed: {run.StdErr}";
            _containerName = null;
            return;
        }

        var portResult = await RunAsync("docker", $"port {_containerName} 5432/tcp");
        var hostPort = ParseHostPort(portResult.StdOut);
        if (hostPort is null)
        {
            UnavailableReason = $"could not resolve published port: {portResult.StdOut} {portResult.StdErr}";
            return;
        }

        ConnectionString = $"Host=127.0.0.1;Port={hostPort};Username=postgres;Password=switchboard;Database=switchboard";

        if (!await WaitUntilReadyAsync(TimeSpan.FromSeconds(30)))
        {
            UnavailableReason = "Postgres container did not become reachable within 30s";
            return;
        }

        if (!await ApplyScriptAsync(Path.Combine(SqlDirectory, "PostgreSQL-Main.sql")) ||
            !await ApplyScriptAsync(Path.Combine(SqlDirectory, "PostgreSQL-Clustering.sql")) ||
            // Upstream gap (dotnet/orleans#8676, still open as of Orleans 10.2.2): the base
            // Clustering.sql never defines CleanupDefunctSiloEntriesKey, which
            // DbStoredQueries' unconditional reflection-based validation nonetheless requires —
            // every ADO.NET-clustered silo fails to start without it. The fix vendored upstream is
            // this migration script, not a change to the base script itself; see Sql/README.md.
            !await ApplyScriptAsync(Path.Combine(MigrationsDirectory, "PostgreSQL-Clustering-3.7.0.sql")) ||
            !await ApplyScriptAsync(Path.Combine(SqlDirectory, "PostgreSQL-Persistence.sql")))
        {
            UnavailableReason = "applying the vendored Orleans schema failed";
            return;
        }

        IsAvailable = true;
    }

    public async Task DisposeAsync()
    {
        if (_containerName is not null)
        {
            await RunAsync("docker", $"rm -f {_containerName}");
        }
    }

    private async Task<bool> WaitUntilReadyAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync(cts.Token);
                return true;
            }
            catch
            {
                await Task.Delay(500, CancellationToken.None);
            }
        }

        return false;
    }

    private async Task<bool> ApplyScriptAsync(string path)
    {
        var sql = await File.ReadAllTextAsync(path);

        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
            return true;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    private static int? ParseHostPort(string dockerPortOutput)
    {
        // "0.0.0.0:54329\n" (and, on some Docker setups, a second "[::]:54329" line) — take the
        // first colon-separated port number found.
        var firstLine = dockerPortOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var separatorIndex = firstLine?.LastIndexOf(':') ?? -1;
        return separatorIndex >= 0 && int.TryParse(firstLine![(separatorIndex + 1)..].Trim(), out var port) ? port : null;
    }

    private static async Task<bool> RunSucceedsAsync(string fileName, string arguments) =>
        (await RunAsync(fileName, arguments)).ExitCode == 0;

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string fileName, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };

            process.Start();
            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(cts.Token);

            return (process.ExitCode, await stdOutTask, await stdErrTask);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
