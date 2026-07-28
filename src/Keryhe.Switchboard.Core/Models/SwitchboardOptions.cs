using System.Threading.Channels;

namespace Keryhe.Switchboard.Core.Models;

public sealed class SwitchboardOptions
{
    // --- Network ---
    public required string PublicUrl { get; set; }

    public string[] AllowedOrigins { get; set; } = [];

    // --- Pattern A: service-direct negotiate (Phase 2; kept here for shape parity, disabled by default) ---
    public bool EnableDirectNegotiate { get; set; } = false;
    public string TrustedIdentityHeader { get; set; } = "X-Switchboard-UserId";
    public string TrustedClaimsHeader { get; set; } = "X-Switchboard-Claims";
    public string[] TrustedProxyNetworks { get; set; } = [];

    // --- Client JWT ---
    public required string TokenSigningKey { get; set; }
    public string TokenIssuer { get; set; } = "switchboard";
    public string TokenAudience { get; set; } = "switchboard-client";
    public TimeSpan ClientTokenExpiry { get; set; } = TimeSpan.FromSeconds(60);

    // --- Server JWT ---
    public required string ServerSigningKey { get; set; }
    public string? ServerSigningKeyFallback { get; set; }
    public string ServerAudience { get; set; } = "switchboard-server";

    // --- Management JWT (Phase 4) ---
    public string? ManagementSigningKey { get; set; }
    public string? ManagementSigningKeyFallback { get; set; }
    public string ManagementAudience { get; set; } = "switchboard-management";

    // --- Server connections ---
    public int MinServerConnectionsPerHub { get; set; } = 5;
    public TimeSpan ServerPingInterval { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ServerPingTimeout { get; set; } = TimeSpan.FromSeconds(5);

    // --- Client connections ---
    public TimeSpan ClientKeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ClientHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxClientConnectionsPerHub { get; set; } = 0;

    // --- SSE / Long Polling (Phase 2 Slices 5/6) ---
    // WebSocket has a socket-close event and needs neither: a connection with no in-flight poll
    // for this long is considered gone, and a long-poll GET waits this long for a message before
    // returning 204. Defaults match ASP.NET Core's own HttpConnectionDispatcherOptions.
    public TimeSpan DisconnectTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan LongPollTimeout { get; set; } = TimeSpan.FromSeconds(90);

    // --- Write channel ---
    public int WriteChannelCapacity { get; set; } = 256;
    public BoundedChannelFullMode WriteChannelFullMode { get; set; } = BoundedChannelFullMode.DropWrite;

    // --- Orleans / Clustering (Phase 3) ---
    public bool UseOrleansCluster { get; set; } = false;
    public string? OrleansAdoNetConnectionString { get; set; }
    public string? OrleansAdoNetInvariant { get; set; }
    public string OrleansClusterId { get; set; } = "switchboard";
    public string OrleansServiceId { get; set; } = "switchboard";

    /// <summary>GUID per process by default (plan decision D14); override for a stable id across
    /// restarts if a deployment wants one. Used as <c>originNodeId</c> on every backplane publish
    /// and, once server-connection assignment is cluster-wide (plan decision D18), as half of a
    /// node-qualified <c>ServerConnectionId</c> (see <see cref="Keryhe.Switchboard.Core.ServerConnectionRef"/>).</summary>
    public string NodeId { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>This node's address on the internal cluster network — used for the SSE/Long
    /// Polling owner-forward hop (plan decision D19, Phase 3 Slice 5). Not required for single-node
    /// or WebSocket-only deployments.</summary>
    public string? InternalUrl { get; set; }

    /// <summary>Every node re-subscribes to every hub grain it locally knows about on this cadence
    /// (plan decision D16) — this, not grain-side persistence, is what survives a hub grain
    /// deactivation (verified: an idle grain silently drops every observer subscription — finding
    /// 5) and what an evicted-after-failure node needs to recover cross-node delivery without a
    /// restart.</summary>
    public TimeSpan ObserverHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Test-only: <c>UseLocalhostClustering()</c>'s dev/single-node clustering provider
    /// binds fixed default ports, so running two silos in one process (Phase 3 Slice 2's two-node
    /// tests) needs each given its own. Null in every real deployment — a real multi-node cluster
    /// uses the ADO.NET providers (Phase 3 Slice 6), never localhost clustering at all.</summary>
    public int? OrleansSiloPort { get; set; }

    /// <summary>Paired with <see cref="OrleansSiloPort"/> — see its remarks.</summary>
    public int? OrleansGatewayPort { get; set; }

    /// <summary>Test-only, paired with <see cref="OrleansSiloPort"/>: the primary silo's
    /// "ip:port" endpoint a secondary test silo joins. Null for the primary silo itself.</summary>
    public string? OrleansPrimarySiloEndpoint { get; set; }

    /// <summary>How often <c>OrleansReadinessProbe</c> (Phase 3 Slice 7) recomputes the cached
    /// value <c>/healthz</c> answers from — silo status plus a cluster-wide server-connection check
    /// per locally-known hub. Deliberately not answered per-request: a load balancer probes every
    /// node every couple of seconds, and doing grain I/O inline would make the probe itself fail
    /// exactly when the cluster is unwell. 1-2s per the plan; kept short enough that a real outage
    /// is reflected within roughly one probe interval, long enough that a load-balancer's probing
    /// cadence never turns into a grain-call storm.</summary>
    public TimeSpan HealthCheckCacheInterval { get; set; } = TimeSpan.FromSeconds(1);
}
