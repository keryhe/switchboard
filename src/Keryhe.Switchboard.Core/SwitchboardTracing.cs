using System.Diagnostics;

namespace Keryhe.Switchboard.Core;

/// <summary>
/// The single <see cref="ActivitySource"/> the service records spans against (Phase 4 plan decision
/// D26) — BCL-only, same posture as <see cref="SwitchboardMetrics"/>: no OpenTelemetry reference is
/// needed to create or record activities, only to export them, so this lives in Core and only
/// <c>Keryhe.Switchboard.Server</c> wires an OTLP trace exporter on top of it.
///
/// Negotiate and client-connect spans are always-on — one per connection, cheap, and genuinely
/// useful for correlating a slow or failing connect. A span per <em>routed message</em> is the
/// cardinality equivalent of doing grain I/O in <c>/healthz</c> at broadcast fan-out rates, so
/// <see cref="StartMessageRouteActivity"/> is only ever called from a site that has already checked
/// <see cref="Keryhe.Switchboard.Core.Models.SwitchboardOptions.TraceMessageRouting"/> (default
/// <c>false</c>) — the gate lives at the call site, not here, since <see cref="ActivitySource"/>
/// itself has no notion of "this kind of span is opt-in."
///
/// No trace-context propagation into <c>ServerEnvelope</c> in Phase 4 (plan decision D26) — spans
/// tag <c>connectionId</c> instead, and parent/child linkage into app-server spans is left to
/// backend-side correlation by that shared attribute rather than a new wire field.
/// </summary>
public sealed class SwitchboardTracing : IDisposable
{
    public const string ActivitySourceName = "Keryhe.Switchboard";

    public ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    public Activity? StartNegotiateActivity(string hubName, string connectionId, string nodeId) =>
        StartActivity("negotiate", hubName, connectionId, nodeId);

    public Activity? StartClientConnectActivity(string hubName, string connectionId, string nodeId) =>
        StartActivity("client_connect", hubName, connectionId, nodeId);

    /// <summary>Callers must check <c>SwitchboardOptions.TraceMessageRouting</c> themselves before
    /// calling this — see the type-level remarks.</summary>
    public Activity? StartMessageRouteActivity(string hubName, string connectionId, string nodeId) =>
        StartActivity("message_route", hubName, connectionId, nodeId);

    private Activity? StartActivity(string name, string hubName, string connectionId, string nodeId)
    {
        var activity = ActivitySource.StartActivity(name);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("hub", hubName);
        activity.SetTag("connectionId", connectionId);
        activity.SetTag("node.id", nodeId);
        return activity;
    }

    public void Dispose() => ActivitySource.Dispose();
}
