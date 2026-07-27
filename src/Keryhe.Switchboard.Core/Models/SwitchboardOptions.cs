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
}
