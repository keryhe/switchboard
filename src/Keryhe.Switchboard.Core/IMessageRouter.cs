namespace Keryhe.Switchboard.Core;

/// <summary>Central dispatch engine. Operates purely on resolved connection/server-connection state.</summary>
public interface IMessageRouter
{
    ValueTask RouteClientMessageAsync(string connectionId, ReadOnlyMemory<byte> payload, string hubProtocol, CancellationToken ct);

    ValueTask RouteToConnectionAsync(string connectionId, ReadOnlyMemory<byte> payload, string hubProtocol, CancellationToken ct);

    /// <summary>
    /// Fans out to every connection on the hub. <paramref name="payload"/>/<paramref name="hubProtocol"/>
    /// is the fallback single-protocol payload; <paramref name="payloadsByProtocol"/> (plan
    /// decision D7) carries one entry per hub protocol the sender knows about, keyed by protocol
    /// name — when present, each target's own negotiated protocol picks the entry it needs. A
    /// target whose protocol has no matching entry is skipped and logged, never sent the wrong
    /// bytes.
    /// </summary>
    ValueTask BroadcastAsync(string hubName, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, IReadOnlySet<string>? excludedConnectionIds, CancellationToken ct);

    ValueTask SendToGroupAsync(string hubName, string groupName, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, IReadOnlySet<string>? excludedConnectionIds, CancellationToken ct);

    ValueTask SendToUserAsync(string hubName, string userId, ReadOnlyMemory<byte> payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, CancellationToken ct);
}
