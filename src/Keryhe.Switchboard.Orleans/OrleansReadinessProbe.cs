using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Keryhe.Switchboard.Orleans;

/// <summary>
/// Clustered <see cref="IReadinessProbe"/> (Phase 3 Slice 7, deliverable "load balancer
/// integration: <c>/healthz</c> returns 200 only when silo is active and at least one server
/// connection exists per registered hub"). <see cref="IsReady"/> itself is a plain volatile field
/// read — the actual check (silo status plus one cluster-wide <c>IHubGrain.HasActiveServerConnectionAsync</c>
/// grain call per locally-known hub) runs on its own <see cref="SwitchboardOptions.HealthCheckCacheInterval"/>
/// cadence in the background, never inline with a <c>/healthz</c> request: a load balancer probes
/// every node every couple of seconds, and a readiness endpoint that does grain I/O per probe fails
/// exactly when the cluster is unwell — the moment the probe most needs a fast, reliable answer.
///
/// "Registered hub" here means the same thing <see cref="IHubRegistry.GetAllHubs"/> always has —
/// every hub this node has itself seen at least one server connection register for, historically
/// (an entry that dictionary never drops, even once its last connection unregisters). A hub this
/// node has never seen directly is not included, the same scope <c>/healthz</c> already had before
/// this slice; nothing here introduces a cluster-wide hub directory.
///
/// Starts <c>false</c> — "not ready until the silo is fully started and the first check has run"
/// (06-project-plan.md's risk register) — and is set back to <c>false</c> immediately in
/// <see cref="StopAsync"/>, ahead of the rest of graceful shutdown's unsubscribe/deregister work, so
/// a load balancer that respects readiness stops routing new requests here as early in the shutdown
/// sequence as possible.
/// </summary>
public sealed class OrleansReadinessProbe(
    ISiloStatusOracle siloStatusOracle,
    IHubRegistry hubRegistry,
    IOptions<SwitchboardOptions> options,
    ILogger<OrleansReadinessProbe> logger) : BackgroundService, IReadinessProbe
{
    private volatile bool _isReady;

    public bool IsReady => _isReady;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.HealthCheckCacheInterval);

        await RefreshAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(stoppingToken);
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            if (siloStatusOracle.CurrentStatus != SiloStatus.Active)
            {
                _isReady = false;
                return;
            }

            foreach (var hub in hubRegistry.GetAllHubs())
            {
                if (!await hubRegistry.HasActiveServerConnectionAsync(hub.HubName, ct))
                {
                    _isReady = false;
                    return;
                }
            }

            _isReady = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutting down mid-refresh; StopAsync below sets the final value.
        }
        catch (Exception ex)
        {
            // A transient grain-call failure must not crash this loop (BackgroundService stops the
            // whole host on an unhandled exception) and must not leave a stale "ready" answer
            // standing either — treated as not-ready until a later tick succeeds.
            _isReady = false;
            logger.LogWarning(ex, "Readiness check failed; reporting not-ready until the next refresh.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _isReady = false;
        await base.StopAsync(cancellationToken);
    }
}
