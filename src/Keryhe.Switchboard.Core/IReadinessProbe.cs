namespace Keryhe.Switchboard.Core;

/// <summary>
/// Backs <c>/healthz</c>'s readiness half (Phase 3 Slice 7, plan decision — the "silo active and a
/// server connection exists per registered hub" load-balancer gate). Deliberately a plain
/// synchronous property, not a <c>Task&lt;bool&gt;</c>: a readiness probe that does I/O per request
/// fails exactly when the cluster is unwell — the moment a load-balancer probe most needs an
/// answer — so the underlying check (which, in clustered mode, is real grain I/O) always happens
/// out of band on its own cadence, never inline with a request.
/// </summary>
public interface IReadinessProbe
{
    bool IsReady { get; }
}
