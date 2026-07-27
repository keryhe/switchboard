using System.Buffers;
using System.Collections.Concurrent;
using Keryhe.Switchboard.Connector.ServerConnections;
using Keryhe.Switchboard.Protocol;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;

namespace Keryhe.Switchboard.Connector.Dispatch;

/// <summary>
/// Turns open_connection / client_message / close_connection envelopes into real Hub method
/// invocations over a synthetic connection per client (04-design.md §11). One instance is shared
/// across all of a hub's physical server connections — inbound envelopes for a given connectionId
/// can arrive on any of them equally, so there is nothing per-physical-connection to key on here.
/// </summary>
public sealed class InboundDispatcher(HubPipelineFactory pipelineFactory, ILogger<InboundDispatcher> logger)
{
    private static readonly IHubProtocol JsonProtocol = new JsonHubProtocol();

    private readonly ConcurrentDictionary<string, ConnectionEntry> _connections = new();

    public async Task HandleAsync(Type hubType, string hubName, ServerEnvelope envelope, Func<ServerEnvelope, CancellationToken, Task> sendToService, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case ServerEnvelopeType.OpenConnection:
                await OpenConnectionAsync(hubType, hubName, envelope, sendToService, ct);
                break;

            case ServerEnvelopeType.ClientMessage:
                await FeedClientMessageAsync(envelope, ct);
                break;

            case ServerEnvelopeType.CloseConnection:
                await CloseConnectionAsync(envelope.ConnectionId!);
                break;

            default:
                logger.LogWarning("InboundDispatcher received unexpected envelope type {EnvelopeType}.", envelope.Type);
                break;
        }
    }

    private async Task OpenConnectionAsync(Type hubType, string hubName, ServerEnvelope envelope, Func<ServerEnvelope, CancellationToken, Task> sendToService, CancellationToken ct)
    {
        var connectionId = envelope.ConnectionId!;
        var hubProtocol = envelope.HubProtocol ?? "json";

        var principal = IdentityReconstruction.Build(envelope.UserId, envelope.Claims);
        var context = new SwitchboardClientConnectionContext(connectionId, principal);

        var pipeline = pipelineFactory.GetOrCreate(hubType);
        var pipelineTask = pipeline(context);
        _ = pipelineTask.ContinueWith(
            t => logger.LogWarning(t.Exception, "Hub pipeline for connection {ConnectionId} faulted.", connectionId),
            TaskContinuationOptions.OnlyOnFaulted);

        await HandshakeWriter.WriteHandshakeRequestAsync(context.ToHubWriter, hubProtocol);

        var entry = new ConnectionEntry(context, pipelineTask);
        _connections[connectionId] = entry;

        entry.OutboundReaderTask = RunOutboundReaderAsync(connectionId, context, sendToService, ct);
    }

    private async Task FeedClientMessageAsync(ServerEnvelope envelope, CancellationToken ct)
    {
        if (!_connections.TryGetValue(envelope.ConnectionId!, out var entry))
        {
            logger.LogWarning("client_message for unknown connection {ConnectionId}.", envelope.ConnectionId);
            return;
        }

        await entry.Context.ToHubWriter.WriteAsync(envelope.Payload!, ct);
    }

    private async Task CloseConnectionAsync(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var entry))
        {
            return;
        }

        await entry.Context.CompleteInboundAsync();

        try
        {
            await entry.PipelineTask;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Hub pipeline for connection {ConnectionId} faulted during teardown.", connectionId);
        }
    }

    /// <summary>
    /// Reads everything HubConnectionHandler writes back out and forwards it as send_to_connection,
    /// except the synthetic handshake response and Ping (the service owns client keep-alive) — see
    /// 04-design.md §11 "Outbound from the pipeline". Detects pipeline completion (rejection path:
    /// OnConnectedAsync threw) and reports it back as close_connection.
    /// </summary>
    private async Task RunOutboundReaderAsync(string connectionId, SwitchboardClientConnectionContext context, Func<ServerEnvelope, CancellationToken, Task> sendToService, CancellationToken ct)
    {
        var reader = context.FromHubReader;
        var handshakeConsumed = false;
        string? rejectionError = null;

        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;

                if (!handshakeConsumed)
                {
                    if (!HandshakeProtocol.TryParseResponseMessage(ref buffer, out var handshakeResponse))
                    {
                        reader.AdvanceTo(buffer.Start, buffer.End);
                        if (result.IsCompleted)
                        {
                            break;
                        }

                        continue;
                    }

                    handshakeConsumed = true;
                    if (handshakeResponse.Error is not null)
                    {
                        rejectionError = handshakeResponse.Error;
                    }
                }

                while (JsonProtocol.TryParseMessage(ref buffer, EmptyBinder.Instance, out var message))
                {
                    switch (message)
                    {
                        case PingMessage:
                            break;

                        case CloseMessage close:
                            rejectionError = close.Error;
                            break;

                        default:
                            var frame = new System.Buffers.ArrayBufferWriter<byte>();
                            JsonProtocol.WriteMessage(message, frame);
                            await sendToService(new ServerEnvelope
                            {
                                Type = ServerEnvelopeType.SendToConnection,
                                ConnectionId = connectionId,
                                HubProtocol = "json",
                                Payload = frame.WrittenMemory.ToArray(),
                            }, ct);
                            break;
                    }
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Outbound reader for connection {ConnectionId} faulted.", connectionId);
            rejectionError ??= "Connector outbound reader faulted.";
        }

        if (rejectionError is not null && _connections.TryRemove(connectionId, out _))
        {
            await sendToService(new ServerEnvelope
            {
                Type = ServerEnvelopeType.CloseConnection,
                ConnectionId = connectionId,
                Error = rejectionError,
            }, CancellationToken.None);
        }
    }

    private sealed class ConnectionEntry(SwitchboardClientConnectionContext context, Task pipelineTask)
    {
        public SwitchboardClientConnectionContext Context { get; } = context;
        public Task PipelineTask { get; } = pipelineTask;
        public Task? OutboundReaderTask { get; set; }
    }

    private sealed class EmptyBinder : Microsoft.AspNetCore.SignalR.IInvocationBinder
    {
        public static readonly EmptyBinder Instance = new();
        public IReadOnlyList<Type> GetParameterTypes(string methodName) => Type.EmptyTypes;
        public Type GetReturnType(string invocationId) => typeof(object);
        public Type GetStreamItemType(string streamId) => typeof(object);
    }
}
