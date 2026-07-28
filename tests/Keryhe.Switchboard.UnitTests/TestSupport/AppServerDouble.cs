using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Keryhe.Switchboard.Protocol;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace Keryhe.Switchboard.UnitTests.TestSupport;

/// <summary>Hand-rolled stand-in for the real Connector's server connection, shared by every
/// end-to-end test that needs an app server on the other side of the service (extracted from
/// Slice 4's <c>ClientRouterEndToEndTests</c> once Slice 5's SSE tests needed the identical
/// double).</summary>
public sealed class AppServerDouble : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly System.IO.Pipelines.Pipe _receivePipe = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<ServerEnvelope> _envelopes =
        Channel.CreateUnbounded<ServerEnvelope>(new UnboundedChannelOptions { SingleWriter = true });

    private readonly ConcurrentQueue<ServerEnvelope> _received = new();
    private readonly Task _fillPipeTask;
    private readonly Task _pumpTask;

    private volatile string? _fillPipeEndReason;
    private volatile Exception? _fillPipeFailure;

    private AppServerDouble(ClientWebSocket socket)
    {
        _socket = socket;
        _fillPipeTask = FillPipeAsync(_cts.Token);
        _pumpTask = PumpEnvelopesAsync(_cts.Token);
    }

    /// <summary>
    /// Every envelope drained off the wire so far, in arrival order — a record, not a queue:
    /// reading it consumes nothing, so any number of callers can look at it concurrently.
    /// Multi-node tests need exactly this. Which app server a cluster-wide least-loaded assignment
    /// picked (plan decision D18) is genuinely unknowable to the test in advance, so it has to be
    /// able to inspect both doubles — and it must not do that by racing a consuming
    /// <see cref="ReceiveAsync"/> on each and abandoning the loser. An abandoned consuming read is
    /// not free: it stays pending and steals a later envelope out from under the next read.
    /// </summary>
    public IReadOnlyList<ServerEnvelope> ReceivedEnvelopes => _received.ToList();

    public static async Task<AppServerDouble> ConnectAsync(Uri baseAddress, string hubName, string serverToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {serverToken}");

        var wsUri = new UriBuilder(baseAddress) { Scheme = "ws", Port = baseAddress.Port, Path = $"/server/{hubName}" }.Uri;
        await socket.ConnectAsync(wsUri, CancellationToken.None);

        var self = new AppServerDouble(socket);
        await self.SendAsync(new ServerEnvelope { Type = ServerEnvelopeType.Handshake, HubName = hubName, Version = 1 });
        var ack = await self.ReceiveAsync(TimeSpan.FromSeconds(5));
        if (ack.Type != ServerEnvelopeType.HandshakeAck)
        {
            throw new InvalidOperationException($"Expected HandshakeAck, got {ack.Type}: {ack.Error}");
        }

        return self;
    }

    public async Task SendToConnectionAsync(string connectionId, string target, params object[] args) =>
        await SendToConnectionUsingProtocolAsync(connectionId, "json", target, args);

    /// <summary>A direct send targets exactly one connection, so — unlike broadcast/group/user
    /// sends, which don't know every recipient's protocol in advance and so carry both encodings
    /// via <see cref="ServerEnvelope.Payloads"/> (plan decision D7) — the real Connector already
    /// knows this recipient's protocol and encodes for it directly. This double does the same.
    /// Distinct method name (rather than an overload of <see cref="SendToConnectionAsync(string,string,object[])"/>)
    /// deliberately, since both would be applicable to the same 4-positional-string-argument call
    /// shape via <c>params</c> expansion and silently resolve to the wrong one.</summary>
    public async Task SendToConnectionUsingProtocolAsync(string connectionId, string hubProtocol, string target, params object[] args)
    {
        await SendAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.SendToConnection,
            ConnectionId = connectionId,
            HubProtocol = hubProtocol,
            Payload = BuildInvocationFrame(hubProtocol, target, args),
        });
    }

    public async Task BroadcastAsync(string hubName, string target, params object[] args)
    {
        var payloads = BuildAllProtocolPayloads(target, args);
        await SendAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.Broadcast,
            HubName = hubName,
            HubProtocol = "json",
            Payload = payloads["json"],
            Payloads = payloads,
        });
    }

    public async Task AddToGroupAsync(string connectionId, string groupName)
    {
        await SendAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.AddToGroup,
            ConnectionId = connectionId,
            GroupName = groupName,
        });
    }

    /// <summary>Fan-out sends don't know every recipient's protocol in advance — a group or
    /// broadcast can be mixed-protocol — so, exactly like the real Connector (plan decision D7),
    /// this always carries both encodings via <see cref="ServerEnvelope.Payloads"/> and lets the
    /// router pick the one matching each recipient.</summary>
    public async Task SendToGroupAsync(string hubName, string groupName, string target, IReadOnlyList<string>? excludedConnectionIds, params object[] args)
    {
        var payloads = BuildAllProtocolPayloads(target, args);
        await SendAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.SendToGroup,
            HubName = hubName,
            GroupName = groupName,
            HubProtocol = "json",
            Payload = payloads["json"],
            Payloads = payloads,
            ExcludedConnectionIds = excludedConnectionIds,
        });
    }

    public async Task SendToUserAsync(string hubName, string userId, string target, params object[] args)
    {
        var payloads = BuildAllProtocolPayloads(target, args);
        await SendAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.SendToUser,
            HubName = hubName,
            UserId = userId,
            HubProtocol = "json",
            Payload = payloads["json"],
            Payloads = payloads,
        });
    }

    private static Dictionary<string, byte[]> BuildAllProtocolPayloads(string target, object[] args) => new()
    {
        ["json"] = BuildInvocationFrame("json", target, args),
        ["messagepack"] = BuildInvocationFrame("messagepack", target, args),
    };

    private static byte[] BuildInvocationFrame(string hubProtocol, string target, object[] args)
    {
        if (hubProtocol == "messagepack")
        {
            return EncodeMessagePackInvocation(target, args);
        }

        var json = System.Text.Json.JsonSerializer.Serialize(new { type = 1, target, arguments = args });
        var jsonWriter = new System.Buffers.ArrayBufferWriter<byte>();
        JsonFrameProtocol.WriteFrame(jsonWriter, Encoding.UTF8.GetBytes(json));
        return jsonWriter.WrittenMemory.ToArray();
    }

    private static readonly IHubProtocol MessagePackProtocol = new MessagePackHubProtocol();

    private static byte[] EncodeMessagePackInvocation(string target, object[] args)
    {
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        MessagePackProtocol.WriteMessage(new InvocationMessage(target, args), writer);
        return writer.WrittenMemory.ToArray();
    }

    private async Task SendAsync(ServerEnvelope envelope)
    {
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        ServerEnvelopeSerializer.Write(writer, envelope);
        await _socket.SendAsync(writer.WrittenMemory, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
    }

    /// <summary>
    /// Takes the next envelope the pump has drained — unlike a one-shot per-call socket read, this
    /// never discards bytes belonging to a second envelope that happened to arrive in the same read
    /// as the one just consumed (exactly what back-to-back sends of several small envelopes, e.g.
    /// AddToGroup, can produce).
    /// </summary>
    /// <remarks>
    /// Reads from a channel the pump fills, rather than driving an <see cref="IAsyncEnumerator{T}"/>
    /// directly. That is a correctness requirement, not a style choice: this method abandons its
    /// read when <paramref name="timeout"/> expires, and abandoning a <c>MoveNextAsync</c> leaves
    /// the async iterator's single state machine mid-flight, so the next call re-enters it
    /// concurrently. That double-schedules the compiler-generated state machine box and the second
    /// <c>MoveNext</c> on an already-completed box dereferences a field the runtime nulls out on
    /// completion — an unhandled <see cref="NullReferenceException"/> on a thread pool thread, which
    /// takes the whole process down. Verified: it crashed the test host outright. A channel read is
    /// safe to abandon; the item is only dequeued when the read actually completes.
    /// </remarks>
    public async Task<ServerEnvelope> ReceiveAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _cts.Token);

        try
        {
            return await _envelopes.Reader.ReadAsync(linked.Token);
        }
        catch (ChannelClosedException ex)
        {
            var types = string.Join(", ", _received.Select(e => e.Type));
            throw new InvalidOperationException(
                $"Server connection closed before an envelope arrived. Received [{types}]; socket state {_socket.State}; close status {_socket.CloseStatus?.ToString() ?? "(none)"} '{_socket.CloseStatusDescription}'; receive-loop ended by: {_fillPipeEndReason ?? "(still running)"}.",
                ex.InnerException ?? _fillPipeFailure);
        }
    }

    /// <summary>
    /// Reads envelopes until one of <paramref name="type"/> arrives, skipping anything else —
    /// a real SignalR client interleaves its own keep-alive <c>Ping</c> client_messages with
    /// whatever this helper is waiting for, so callers that expect exactly one envelope per
    /// client action must not assume it arrives first.
    /// </summary>
    public async Task<ServerEnvelope> ReceiveEnvelopeAsync(ServerEnvelopeType type, TimeSpan timeout)
    {
        while (true)
        {
            var envelope = await ReceiveAsync(timeout);
            if (envelope.Type == type)
            {
                return envelope;
            }
        }
    }

    /// <summary>
    /// The one and only consumer of <see cref="ReadAllAsync"/>, draining it start to finish on a
    /// single task so the async iterator is never entered concurrently. Everything it drains goes
    /// both into <see cref="_received"/> (the non-consuming record) and the channel
    /// <see cref="ReceiveAsync"/> takes from.
    /// </summary>
    private async Task PumpEnvelopesAsync(CancellationToken ct)
    {
        // Completed *with* whatever killed the pump, so a waiting ReceiveAsync reports the real
        // cause. Completing it bare would turn any pump-side failure (a malformed frame, a socket
        // fault) into an indistinguishable "the connection closed", which is a misleading thing for
        // a test to fail on.
        Exception? failure = null;
        try
        {
            await foreach (var envelope in ReadAllAsync(ct))
            {
                _received.Enqueue(envelope);
                await _envelopes.Writer.WriteAsync(envelope, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            _envelopes.Writer.TryComplete(failure);
        }
    }

    private async IAsyncEnumerable<ServerEnvelope> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            var result = await _receivePipe.Reader.ReadAsync(ct);
            var buffer = result.Buffer;

            while (ServerEnvelopeSerializer.TryParseEnvelope(buffer, out var envelope, out var consumed, out _))
            {
                buffer = buffer.Slice(consumed);
                yield return envelope!;
            }

            _receivePipe.Reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                yield break;
            }
        }
    }

    private async Task FillPipeAsync(CancellationToken ct)
    {
        var writer = _receivePipe.Writer;
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var memory = writer.GetMemory(4096);
                var result = await _socket.ReceiveAsync(memory, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _fillPipeEndReason = $"peer sent Close ({_socket.CloseStatus}: {_socket.CloseStatusDescription})";
                    break;
                }

                writer.Advance(result.Count);
                var flush = await writer.FlushAsync(ct);
                if (flush.IsCompleted)
                {
                    _fillPipeEndReason = "pipe reader completed";
                    break;
                }
            }

            _fillPipeEndReason ??= $"socket left Open state ({_socket.State})";
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Recorded rather than only swallowed: when this loop dies the channel completes, and
            // every later ReceiveAsync would otherwise report a bare "connection closed" that says
            // nothing about *why*. Kept in memory rather than logged, deliberately — the one time
            // this mattered, the underlying fault was a race that stopped reproducing entirely once
            // synchronous console I/O was added to the path, so the diagnostic has to cost nothing.
            _fillPipeFailure = ex;
            _fillPipeEndReason = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        try
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch
        {
            // best-effort
        }

        try
        {
            await _fillPipeTask;
            await _pumpTask;
        }
        catch
        {
            // best-effort drain
        }

        _cts.Dispose();
        _socket.Dispose();
    }
}
