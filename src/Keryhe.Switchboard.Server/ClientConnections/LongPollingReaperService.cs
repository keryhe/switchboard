using Keryhe.Switchboard.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// Detects a Long Polling client that has abandoned the connection — stopped issuing poll
/// requests without ever sending DELETE (03-protocol.md §1.6, plan Slice 5's
/// <c>SwitchboardOptions.DisconnectTimeout</c> exists for exactly this) — and force-closes it, the
/// same way a WebSocket's own socket-close event would. Every other transport notices
/// disconnection for free (a closed socket, a cancelled SSE request); Long Polling has no
/// persistent connection to watch, so this is the only way the service ever finds out.
/// </summary>
public sealed class LongPollingReaperService(
    LongPollingConnectionTracker tracker,
    IOptions<SwitchboardOptions> options,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var disconnectTimeout = options.Value.DisconnectTimeout;
        var scanInterval = TimeSpan.FromTicks(Math.Max(disconnectTimeout.Ticks / 2, TimeSpan.FromSeconds(1).Ticks));

        using var timer = new PeriodicTimer(scanInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = timeProvider.GetUtcNow();
            foreach (var (_, transport) in tracker.All)
            {
                if (now - transport.LastPollAt > disconnectTimeout)
                {
                    await transport.CloseAsync();
                }
            }
        }
    }
}
