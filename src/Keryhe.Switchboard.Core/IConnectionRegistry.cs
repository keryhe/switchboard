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
    IAsyncEnumerable<ClientConnectionState> GetAllAsync(string hubName, CancellationToken ct);
    Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct);
    Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct);
    IAsyncEnumerable<ClientConnectionState> GetGroupMembersAsync(string hubName, string groupName, CancellationToken ct);
    IAsyncEnumerable<ClientConnectionState> GetUserConnectionsAsync(string hubName, string userId, CancellationToken ct);
}
