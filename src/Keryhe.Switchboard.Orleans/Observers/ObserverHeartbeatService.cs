using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Orleans.Grains;
using Keryhe.Switchboard.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;

namespace Keryhe.Switchboard.Orleans.Observers;

/// <summary>
/// Subscribes this node's <see cref="HubObserverImpl"/> to every hub it locally knows about
/// (<see cref="IHubRegistry"/> — still node-local through Phase 3 Slice 3; Slice 4 makes it
/// cluster-wide) — once immediately at startup, then again every
/// <see cref="SwitchboardOptions.ObserverHeartbeatInterval"/> for the process's whole lifetime
/// (plan decision D16). The re-subscribe is not an optimization: a hub grain deactivation silently
/// drops every subscription with no error anywhere (verified, finding 5), so periodic
/// re-registration is the only thing that makes cross-node delivery recover without an operator
/// restarting a node.
/// </summary>
public sealed class ObserverHeartbeatService(
    IGrainFactory grainFactory,
    IHubRegistry hubRegistry,
    ILocalTransportRegistry localTransportRegistry,
    IConnectionRegistry connectionRegistry,
    IOptions<SwitchboardOptions> options,
    ILoggerFactory loggerFactory) : BackgroundService
{
    /// <summary>
    /// Holds the <see cref="HubObserverImpl"/> itself alongside the grain reference, and that is
    /// load-bearing rather than incidental: <c>IGrainFactory.CreateObjectReference</c> keeps only a
    /// <b>weak</b> reference to the observer object, so whoever calls it owns keeping the target
    /// alive. An earlier version cached only the reference and let the impl fall out of scope as a
    /// local — once a GC collected it, every cross-node call into this node silently stopped
    /// arriving, permanently: the reference object still exists, so the heartbeat below happily
    /// re-subscribes the same dead reference forever and the grain's targeted-call path logs a
    /// delivery warning at most. Verified: it made cross-node broadcast and OpenConnection delivery
    /// intermittently vanish under exactly the GC pressure a long-running node accumulates.
    /// </summary>
    private readonly Dictionary<string, (HubObserverImpl Observer, IHubObserver Reference)> _observersByHub = new();

    private readonly ILogger<ObserverHeartbeatService> _logger = loggerFactory.CreateLogger<ObserverHeartbeatService>();

    /// <summary>
    /// How long one hub's re-announcement may take before this tick gives up on it and lets the next
    /// tick retry.
    /// </summary>
    /// <remarks>
    /// Without a bound, a single grain call can stall the whole loop for Orleans' own response
    /// timeout (30s by default) — and the case where that happens is exactly the case this loop
    /// exists to recover from. When the silo hosting a hub grain dies, a call already in flight to
    /// that activation does not fail fast; it hangs until Orleans times it out, so this node stops
    /// re-subscribing for far longer than its own heartbeat interval, and cross-node delivery stays
    /// dead the entire time even though a healthy silo is right there ready to host a new
    /// activation. Verified: it left a node unsubscribed for tens of seconds after its peer was
    /// killed. Re-announcement is idempotent, so abandoning a slow attempt costs nothing but a
    /// retry. Never shorter than a second, so a fast heartbeat cadence does not abandon healthy
    /// calls that merely crossed a network.
    /// </remarks>
    private readonly TimeSpan _attemptTimeout =
        TimeSpan.FromSeconds(Math.Max(1, options.Value.ObserverHeartbeatInterval.TotalSeconds));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ObserverHeartbeatInterval);

        await SubscribeToKnownHubsAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SubscribeToKnownHubsAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Graceful shutdown (Phase 3 Slice 7): unsubscribes every hub this node ever subscribed to
    /// before the process exits, so a hub grain's subscriber count reflects reality immediately
    /// rather than waiting out <c>MissedHeartbeatsBeforeEviction</c> heartbeats worth of staleness
    /// after the process is already gone. <c>base.StopAsync</c> runs first — it cancels
    /// <see cref="ExecuteAsync"/>'s loop and awaits it, so there is no concurrent heartbeat tick to
    /// race this cleanup pass. Best-effort and bounded by <paramref name="cancellationToken"/> (the
    /// host's own shutdown-timeout token, per <c>Switchboard:ShutdownTimeout</c>) — a peer already
    /// gone or a slow call here must never block shutdown, the same posture as every other call this
    /// class makes into the cluster.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        foreach (var hubName in _observersByHub.Keys)
        {
            try
            {
                await grainFactory.GetGrain<IHubGrain>(hubName).UnsubscribeAsync(options.Value.NodeId).WaitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Observer heartbeat shutdown: unsubscribing from hub {HubName} failed; the grain's staleness eviction will clean it up instead.", hubName);
            }
        }
    }

    /// <summary>
    /// The set of hub names this node must subscribe an observer to (Phase 3 Slice 7 fix) —
    /// <c>hubRegistry.GetAllHubs()</c> (hubs with a local <em>server</em> connection) alone is not
    /// enough: a node can have live <em>clients</em> for a hub with zero local server connections
    /// (D18's own "zero server connections" gate, and the ordinary window right after a node
    /// restart, before its own app-server pool has reconnected) and still needs to receive
    /// cross-node targeted/group/broadcast/close messages for them. Verified: without this, such a
    /// node's observer never subscribes at all, and <c>HubGrain</c>'s targeted calls (e.g. the
    /// completion for a client-invoked hub method assigned to a remote app server) are silently
    /// dropped — logged as "no active subscription", never delivered, forever.
    /// </summary>
    private IEnumerable<string> KnownHubNames() =>
        hubRegistry.GetAllHubs().Select(h => h.HubName).Union(localTransportRegistry.GetKnownHubNames());

    private async Task SubscribeToKnownHubsAsync(CancellationToken ct)
    {
        foreach (var hubName in KnownHubNames())
        {
            ct.ThrowIfCancellationRequested();

            // A subscribe attempt for one hub failing (e.g. the cluster connection dropping mid-
            // call, a real condition another node dying can trigger) must never take down this
            // BackgroundService — an unhandled exception here stops the whole host by default
            // (BackgroundService's own documented behavior), which would be wildly disproportionate
            // to "one hub's re-subscribe was late." Logged and retried on the very next tick.
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(_attemptTimeout);

            try
            {
                if (!_observersByHub.TryGetValue(hubName, out var entry))
                {
                    var observer = new HubObserverImpl(hubName, localTransportRegistry, hubRegistry, connectionRegistry, loggerFactory.CreateLogger<HubObserverImpl>());
                    entry = (observer, grainFactory.CreateObjectReference<IHubObserver>(observer));
                    _observersByHub[hubName] = entry;
                }

                var hubGrain = grainFactory.GetGrain<IHubGrain>(hubName);
                await AwaitBoundedAsync(hubGrain.SubscribeAsync(entry.Reference, options.Value.NodeId), attemptCts.Token);

                // Re-announce this node's live server connections for the same reason the
                // subscription above is re-announced (finding 5): the grain's cluster-wide
                // server-connection inventory is transient, so a deactivation drops it silently and
                // with it both negotiate's 503 check and least-loaded assignment — the hub would
                // refuse every new connection from then on with no error anywhere. Idempotent by
                // construction: RegisterServerConnectionAsync only adds an entry it doesn't already
                // have, so re-announcing never resets an existing connection's assignment count.
                // Nothing to re-announce for a hub known only via a local client connection
                // (GetHub returns null) — that is exactly the case this fix exists for.
                foreach (var serverConnection in hubRegistry.GetHub(hubName)?.ServerConnections.Values ?? [])
                {
                    if (serverConnection.Status == ServerConnectionStatus.Connected)
                    {
                        await AwaitBoundedAsync(
                            hubGrain.RegisterServerConnectionAsync(options.Value.NodeId, serverConnection.ConnectionId),
                            attemptCts.Token);
                    }
                }
            }
            // The attempt ran out of time rather than the host shutting down — the distinction
            // matters, because only the latter should stop this loop. Checked on the outer token so
            // a genuine shutdown still propagates and ends the service.
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Observer heartbeat: hub {HubName} did not complete within {AttemptTimeout}; abandoning this attempt and retrying on the next tick.",
                    hubName, _attemptTimeout);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Observer heartbeat: subscribing to hub {HubName} failed; will retry on the next tick.", hubName);
            }
        }
    }

    /// <summary>
    /// Awaits <paramref name="call"/> but stops waiting when <paramref name="ct"/> fires, leaving the
    /// abandoned call's eventual outcome observed so it never surfaces as an unobserved task
    /// exception. Orleans has no way to actually cancel an in-flight grain call, so "abandon" is the
    /// most that can be done — which is fine here, since every call this loop makes is idempotent.
    /// </summary>
    private static async Task AwaitBoundedAsync(Task call, CancellationToken ct)
    {
        try
        {
            await call.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            _ = call.ContinueWith(static abandoned => _ = abandoned.Exception, TaskScheduler.Default);
            throw;
        }
    }
}
