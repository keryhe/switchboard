using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Orleans.Grains;
using Keryhe.Switchboard.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;

namespace Keryhe.Switchboard.Orleans;

/// <summary>
/// Publishes this node's <c>InternalUrl</c> (plan decision D19, Phase 3 Slice 5 — the SSE/Long
/// Polling forward hop resolves a remote connection owner's <c>connectionToken</c> request to a
/// physical address through this registry) and, since Phase 4, its known hub names (plan decision
/// D27, finding 5 — the cluster-wide hub directory neither <c>IHubRegistry.GetAllHubs()</c> nor
/// <c>ILocalTransportRegistry.GetKnownHubNames()</c> can answer alone) into
/// <see cref="INodeRegistryGrain"/> — once immediately at startup, then again every
/// <see cref="SwitchboardOptions.ObserverHeartbeatInterval"/> for the process's whole lifetime,
/// mirroring <see cref="Observers.ObserverHeartbeatService"/>'s own re-subscribe cadence: this
/// grain's state is real data (not a live reference like an observer subscription), but it can
/// still go stale if a node's set of known hubs grows after the first publish, so periodic
/// republishing rather than a one-shot startup call is what keeps the directory current. Registers
/// unconditionally, even with no <c>InternalUrl</c> configured — a WebSocket-only or single-node-
/// style deployment still needs its hub names visible to the cluster-wide directory.
/// </summary>
public sealed class NodeRegistryPublisherService(
    IGrainFactory grainFactory,
    IHubRegistry hubRegistry,
    ILocalTransportRegistry localTransportRegistry,
    IOptions<SwitchboardOptions> options,
    ILogger<NodeRegistryPublisherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ObserverHeartbeatInterval);

        await PublishAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PublishAsync(stoppingToken);
        }
    }

    /// <summary>Graceful shutdown (Phase 3 Slice 7's posture, extended to hub names): removes this
    /// node's entry before the process exits so a stale directory entry doesn't outlive it. Best-
    /// effort — the cluster this node was part of may already be unreachable during shutdown (its
    /// only peer having gone down too), exactly the case <c>ObserverHeartbeatService.StopAsync</c>
    /// already documents for the same reason.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        try
        {
            await grainFactory.GetGrain<INodeRegistryGrain>(NodeRegistryGrainKey.Value)
                .UnregisterAsync(options.Value.NodeId)
                .WaitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to unregister this node from INodeRegistryGrain during shutdown; leaving a stale entry.");
        }
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        try
        {
            var hubNames = hubRegistry.GetAllHubs().Select(h => h.HubName)
                .Union(localTransportRegistry.GetKnownHubNames())
                .ToList();

            await grainFactory.GetGrain<INodeRegistryGrain>(NodeRegistryGrainKey.Value)
                .RegisterAsync(options.Value.NodeId, options.Value.InternalUrl, hubNames)
                .WaitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to publish this node's registration (InternalUrl/known hub names) to INodeRegistryGrain; will retry on the next tick.");
        }
    }
}
