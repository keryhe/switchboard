using System.Diagnostics.Metrics;

namespace Keryhe.Switchboard.Core;

/// <summary>
/// The single <see cref="Meter"/> and every instrument the service records (Phase 4 plan decision
/// D24) — a singleton in Core so instrumentation needs no OpenTelemetry reference (verified: a bare
/// <c>net10.0</c> classlib with zero package references compiles <c>Meter</c>/<c>Counter</c>/
/// <c>Histogram</c>/<c>ObservableGauge</c> — finding 1). Only <c>Keryhe.Switchboard.Server</c> wires
/// an OTLP exporter on top of this; with no exporter configured the instruments still record, so
/// tests observe them via <see cref="System.Diagnostics.Metrics.MeterListener"/> with no collector
/// and no network anywhere.
///
/// The two node-local gauges (<c>client_connections.active</c>, <c>server_connections.active</c>)
/// are registered separately via <see cref="RegisterClientConnectionsGauge"/>/
/// <see cref="RegisterServerConnectionsGauge"/> rather than taken as constructor dependencies:
/// <see cref="ILocalTransportRegistry"/> lives in Core and could be injected directly, but
/// server-connection counts come from <c>Keryhe.Switchboard.Protocol.IHubRegistry</c>, which Core
/// cannot reference without an illegal circular project dependency. Both gauges are wired the same
/// way — a callback resolved once at host startup — for symmetry rather than special-casing one of
/// them. Each measurement is tagged only by <c>hub</c>; the node identity is a resource attribute
/// applied once to every signal by the OTel resource builder in <c>Program.cs</c>, not a per-
/// measurement tag (plan decision D24) — an <see cref="ObservableGauge{T}"/> callback that did grain
/// I/O to sum the cluster would repeat the <c>/healthz</c> mistake at the collection interval, on
/// every node, forever.
/// </summary>
public sealed class SwitchboardMetrics : IDisposable
{
    public const string MeterName = "Keryhe.Switchboard";

    public Meter Meter { get; }

    /// <summary>Counter, by <c>direction</c> (<c>inbound</c>/<c>outbound</c>) and <c>hub</c>.</summary>
    public Counter<long> MessagesRouted { get; }

    /// <summary>Recipient count of a single fan-out send (broadcast/group/user), local to the
    /// issuing node — matches the roadmap's <c>signalr.broadcast.fan_out_size</c> histogram, applied
    /// to all three fan-out kinds rather than only broadcast, since group/user sends share the same
    /// <c>FanOutAsync</c> path and the same "how many recipients did this actually reach" question.</summary>
    public Histogram<long> BroadcastFanOutSize { get; }

    /// <summary>Client frame read → written to the assigned server connection, tagged
    /// <c>cross_node</c> (plan decision D25 — replaces the unmeasurable client↔server round trip).</summary>
    public Histogram<double> MessageInboundDuration { get; }

    /// <summary>Envelope received from an app server → written to the target client transport's
    /// channel (plan decision D25).</summary>
    public Histogram<double> MessageOutboundDuration { get; }

    /// <summary>Counter, by <c>reason</c> — re-scoped from the Phase 1 D3/D4 deferral (plan decision
    /// D28) to the drops that are real today: <c>unknown_connection</c>, <c>server_connection_gone</c>,
    /// <c>malformed_server_connection_ref</c>, <c>no_payload_for_protocol</c>, and
    /// <c>no_node_subscribed</c> (the Phase 3 Slice 7 failure mode).</summary>
    public Counter<long> EnvelopesUnrouted { get; }

    /// <summary>Counter triple replacing the roadmap's <c>signalr.pending_connections.active</c>
    /// gauge (plan decision D28) — a live gauge would need an Orleans-mode "enumerate every
    /// outstanding grain" primitive that D19 deliberately never built. In-flight is
    /// <c>created - consumed - expired</c>, computed in the backend.</summary>
    public Counter<long> PendingConnectionsCreated { get; }
    public Counter<long> PendingConnectionsConsumed { get; }
    public Counter<long> PendingConnectionsExpired { get; }

    public SwitchboardMetrics()
    {
        Meter = new Meter(MeterName);

        MessagesRouted = Meter.CreateCounter<long>("signalr.messages.routed");
        BroadcastFanOutSize = Meter.CreateHistogram<long>("signalr.broadcast.fan_out_size");
        MessageInboundDuration = Meter.CreateHistogram<double>("signalr.message.inbound_duration", unit: "ms");
        MessageOutboundDuration = Meter.CreateHistogram<double>("signalr.message.outbound_duration", unit: "ms");
        EnvelopesUnrouted = Meter.CreateCounter<long>("signalr.envelopes.unrouted");
        PendingConnectionsCreated = Meter.CreateCounter<long>("signalr.pending_connections.created");
        PendingConnectionsConsumed = Meter.CreateCounter<long>("signalr.pending_connections.consumed");
        PendingConnectionsExpired = Meter.CreateCounter<long>("signalr.pending_connections.expired");
    }

    /// <summary>Registers <c>signalr.client_connections.active</c>, backed by a callback the host
    /// supplies once <see cref="ILocalTransportRegistry"/> is resolvable — this node's own counts,
    /// one measurement per hub it locally knows about.</summary>
    public void RegisterClientConnectionsGauge(Func<IEnumerable<Measurement<long>>> observeValues) =>
        Meter.CreateObservableGauge("signalr.client_connections.active", observeValues);

    /// <summary>Registers <c>signalr.server_connections.active</c> — see
    /// <see cref="RegisterClientConnectionsGauge"/>'s remarks; the callback here closes over
    /// <c>IHubRegistry</c> instead, which is why this is a callback rather than a constructor
    /// dependency.</summary>
    public void RegisterServerConnectionsGauge(Func<IEnumerable<Measurement<long>>> observeValues) =>
        Meter.CreateObservableGauge("signalr.server_connections.active", observeValues);

    public void Dispose() => Meter.Dispose();
}
