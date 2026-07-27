namespace Keryhe.Switchboard.Core;

/// <summary>Central dispatch engine. Operates purely on resolved connection/server-connection state.</summary>
public interface IMessageRouter
{
    ValueTask RouteClientMessageAsync(string connectionId, ReadOnlyMemory<byte> payload, string hubProtocol, CancellationToken ct);

    ValueTask RouteToConnectionAsync(string connectionId, ReadOnlyMemory<byte> payload, string hubProtocol, CancellationToken ct);

    ValueTask BroadcastAsync(string hubName, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlySet<string>? excludedConnectionIds, CancellationToken ct);

    ValueTask SendToGroupAsync(string hubName, string groupName, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlySet<string>? excludedConnectionIds, CancellationToken ct);

    ValueTask SendToUserAsync(string hubName, string userId, ReadOnlyMemory<byte> payload, string hubProtocol, CancellationToken ct);
}
