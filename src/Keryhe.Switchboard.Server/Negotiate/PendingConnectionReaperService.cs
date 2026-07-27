using Keryhe.Switchboard.Registry;

namespace Keryhe.Switchboard.Server.Negotiate;

/// <summary>Background reaper for the D4 pending-connection store — evicts expired entries so an
/// unresolved token can never be replayed after its TTL.</summary>
public sealed class PendingConnectionReaperService(IPendingConnectionStore store) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            store.ReapExpired();
        }
    }
}
