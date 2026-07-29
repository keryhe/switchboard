using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Registry;

namespace Keryhe.Switchboard.Server.Negotiate;

/// <summary>Background reaper for the D4 pending-connection store — evicts expired entries so an
/// unresolved token can never be replayed after its TTL. Records
/// <c>signalr.pending_connections.expired</c> (Phase 4 plan decision D28) from the count
/// <see cref="IPendingConnectionStore.ReapExpiredAsync"/> returns; the Orleans store always returns
/// 0 (its own remarks explain why an activation-lifetime-bounded grain needs no sweep).</summary>
public sealed class PendingConnectionReaperService(IPendingConnectionStore store, SwitchboardMetrics metrics) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var reaped = await store.ReapExpiredAsync(stoppingToken);
            if (reaped > 0)
            {
                metrics.PendingConnectionsExpired.Add(reaped);
            }
        }
    }
}
