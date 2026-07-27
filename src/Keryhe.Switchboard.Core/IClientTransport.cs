using System.Threading.Channels;

namespace Keryhe.Switchboard.Core;

/// <summary>Abstraction over the three client-facing transports (WebSockets, SSE, Long Polling).</summary>
public interface IClientTransport
{
    string ConnectionId { get; }
    string HubName { get; }
    string? UserId { get; }

    /// <summary>Raw hub-protocol frame bytes (already framed: \x1e-delimited or length-prefixed) to write to the client.</summary>
    Channel<ReadOnlyMemory<byte>> Output { get; }

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken ct);

    ValueTask CloseAsync(string? error = null);
}
