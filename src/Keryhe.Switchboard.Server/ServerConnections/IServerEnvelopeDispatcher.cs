using Keryhe.Switchboard.Protocol;

namespace Keryhe.Switchboard.Server.ServerConnections;

/// <summary>Handles envelopes received from an app server connection, other than the handshake/ping/pong
/// frames the connection loop itself owns. Slice 3: registration/ping only (stub implementation).
/// Slice 4 wires this to <c>IMessageRouter</c>.</summary>
public interface IServerEnvelopeDispatcher
{
    ValueTask DispatchAsync(string serverConnectionId, ServerEnvelope envelope, CancellationToken ct);
}
