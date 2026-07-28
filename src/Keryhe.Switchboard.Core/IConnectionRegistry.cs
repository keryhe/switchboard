using Keryhe.Switchboard.Core.Models;

namespace Keryhe.Switchboard.Core;

/// <summary>
/// Async from day one even though the Phase 1 in-memory implementation is synchronous, so that
/// Phase 3's Orleans-backed implementation is a substitution, not an interface change (ADR-002).
/// </summary>
public interface IConnectionRegistry
{
    Task RegisterAsync(ClientConnectionState state, CancellationToken ct);
    Task SetProtocolAsync(string connectionId, string hubProtocol, CancellationToken ct);
    Task UnregisterAsync(string connectionId, CancellationToken ct);
    Task<ClientConnectionState?> GetAsync(string connectionId, CancellationToken ct);
    Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct);
    Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct);

    /// <summary>Not on the fan-out hot path as of Phase 3 (plan decision D14) —
    /// <c>DefaultMessageRouter</c> reads <c>ILocalTransportRegistry</c> instead, so a broadcast
    /// never enumerates every connection in the distributed registry. Remaining callers are
    /// diagnostics and tests.</summary>
    IAsyncEnumerable<ClientConnectionState> GetAllAsync(string hubName, CancellationToken ct);

    /// <summary>Not on the fan-out hot path — see <see cref="GetAllAsync"/>.</summary>
    IAsyncEnumerable<ClientConnectionState> GetGroupMembersAsync(string hubName, string groupName, CancellationToken ct);

    /// <summary>Not on the fan-out hot path — see <see cref="GetAllAsync"/>.</summary>
    IAsyncEnumerable<ClientConnectionState> GetUserConnectionsAsync(string hubName, string userId, CancellationToken ct);
}
