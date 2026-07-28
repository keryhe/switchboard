using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Orleans.Grains;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;

namespace Keryhe.Switchboard.Orleans;

/// <summary>
/// Publishes this node's <c>InternalUrl</c> into <see cref="INodeRegistryGrain"/> at startup and
/// removes it at shutdown (plan decision D19, Phase 3 Slice 5) — the SSE/Long Polling forward hop
/// resolves a remote connection owner's <c>connectionToken</c> request to a physical address through
/// this registry. A no-op when <c>InternalUrl</c> isn't configured: WebSocket-only or single-node
/// deployments never need it (<see cref="SwitchboardOptions.InternalUrl"/>'s own remarks).
/// </summary>
public sealed class NodeRegistryPublisherService(
    IGrainFactory grainFactory,
    IOptions<SwitchboardOptions> options,
    ILogger<NodeRegistryPublisherService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.InternalUrl))
        {
            logger.LogInformation(
                "SwitchboardOptions.InternalUrl is not configured; this node will not be reachable for the SSE/Long Polling forward hop (plan decision D19).");
            return Task.CompletedTask;
        }

        return grainFactory.GetGrain<INodeRegistryGrain>(NodeRegistryGrainKey.Value)
            .RegisterAsync(options.Value.NodeId, options.Value.InternalUrl);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.InternalUrl))
        {
            return;
        }

        try
        {
            await grainFactory.GetGrain<INodeRegistryGrain>(NodeRegistryGrainKey.Value)
                .UnregisterAsync(options.Value.NodeId);
        }
        catch (Exception ex)
        {
            // Best-effort: this runs during host shutdown, where the cluster this node was part of
            // may already be unreachable (its only peer having gone down too, or this node's own
            // silo already tearing down) — verified during Phase 3 Slice 2 testing to throw
            // Orleans.Runtime.SiloUnavailableException in exactly that case. A failed unregister
            // leaves one stale INodeRegistryGrain entry pointing at an address nothing is listening
            // on any more; ClientConnectionForwarder already treats an unreachable cached address as
            // evictable on the next forward attempt, so this is never worse than a transient 502,
            // and must never turn into a graceful-shutdown failure over it.
            logger.LogWarning(ex, "Failed to unregister this node's InternalUrl from INodeRegistryGrain during shutdown; leaving a stale entry.");
        }
    }
}
