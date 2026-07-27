using System.Collections.Concurrent;

namespace Keryhe.Switchboard.Core.Models;

public enum TransportType { WebSockets, ServerSentEvents, LongPolling }

/// <summary>Represents a connected client. Held in <see cref="IConnectionRegistry"/>.</summary>
public sealed class ClientConnectionState
{
    // Identity
    public required string ConnectionId { get; init; }
    public required string ConnectionToken { get; init; }
    public required string HubName { get; init; }
    public string? UserId { get; init; }

    // Transport & Protocol
    public required TransportType Transport { get; init; }
    public string? HubProtocol { get; set; }
    public required IClientTransport TransportHandle { get; init; }

    // Routing
    public required string ServerConnectionId { get; set; }

    // Membership — ConcurrentDictionary<string, byte> used as a concurrent set (value is always 0)
    public ConcurrentDictionary<string, byte> Groups { get; } = new();

    // Timestamps
    public required DateTimeOffset ConnectedAt { get; init; }
    public DateTimeOffset LastSeen { get; set; }
}
