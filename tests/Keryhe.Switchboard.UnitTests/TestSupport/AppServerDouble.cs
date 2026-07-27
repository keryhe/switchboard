using System.Net.WebSockets;
using System.Text;
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
    private readonly IAsyncEnumerator<ServerEnvelope> _envelopes;
    private readonly Task _fillPipeTask;

    private AppServerDouble(ClientWebSocket socket)
    {
        _socket = socket;
        _fillPipeTask = FillPipeAsync(_cts.Token);
        _envelopes = ReadAllAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);
    }

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
    /// Pulls the next envelope from the persistent receive pipe below — unlike a one-shot
    /// per-call socket read, this never discards bytes belonging to a second envelope that
    /// happened to arrive in the same read as the one just consumed (exactly what back-to-back
    /// sends of several small envelopes, e.g. AddToGroup, can produce).
    /// </summary>
    public async Task<ServerEnvelope> ReceiveAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _cts.Token);

        if (!await _envelopes.MoveNextAsync().AsTask().WaitAsync(linked.Token))
        {
            throw new InvalidOperationException("Server connection closed before an envelope arrived.");
        }

        return _envelopes.Current;
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
                    break;
                }

                writer.Advance(result.Count);
                var flush = await writer.FlushAsync(ct);
                if (flush.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
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
        }
        catch
        {
            // best-effort drain
        }

        _cts.Dispose();
        _socket.Dispose();
    }
}
