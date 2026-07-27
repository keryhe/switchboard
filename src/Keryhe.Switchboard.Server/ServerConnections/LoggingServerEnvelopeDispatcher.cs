using Keryhe.Switchboard.Protocol;

namespace Keryhe.Switchboard.Server.ServerConnections;

/// <summary>Placeholder dispatcher used until Slice 4 wires app-server envelopes into <c>IMessageRouter</c>.</summary>
public sealed class LoggingServerEnvelopeDispatcher(ILogger<LoggingServerEnvelopeDispatcher> logger) : IServerEnvelopeDispatcher
{
    public ValueTask DispatchAsync(string serverConnectionId, ServerEnvelope envelope, CancellationToken ct)
    {
        logger.LogWarning("Received {EnvelopeType} from server connection {ConnectionId} but no router is wired up yet.", envelope.Type, serverConnectionId);
        return ValueTask.CompletedTask;
    }
}
