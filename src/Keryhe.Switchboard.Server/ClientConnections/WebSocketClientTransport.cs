using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol.Framing;
using Microsoft.AspNetCore.Connections;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// WebSocket transport for a single client connection (03-protocol.md §1.2). Frame boundaries and
/// the WebSocket message type both follow <see cref="Framing"/> (04-design.md §6) — mutable
/// because the handshake itself is always read as JSON (finding 1 — see
/// docs/docs/09-phase0-findings/) regardless of which protocol the client eventually negotiates,
/// so <see cref="ClientConnectionEndpoint"/> starts every connection at
/// <see cref="JsonFraming.Instance"/> and switches this property once the handshake reveals the
/// real protocol, before writing the handshake response (whose WebSocket frame type must already
/// match the negotiated protocol's transfer format — a real MessagePack client sends even its
/// JSON handshake request inside a Binary WebSocket frame).
/// </summary>
public sealed class WebSocketClientTransport : IFramedClientTransport, IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly Pipe _receivePipe = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _fillPipeTask;
    private readonly Task _writeLoopTask;

    public WebSocketClientTransport(WebSocket socket, string connectionId, string hubName, string? userId, int writeChannelCapacity, BoundedChannelFullMode fullMode)
    {
        _socket = socket;
        ConnectionId = connectionId;
        HubName = hubName;
        UserId = userId;
        Output = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(writeChannelCapacity) { FullMode = fullMode });

        _fillPipeTask = FillPipeAsync(_cts.Token);
        _writeLoopTask = RunWriteLoopAsync(_cts.Token);
    }

    public string ConnectionId { get; }
    public string HubName { get; }
    public string? UserId { get; }
    public Channel<ReadOnlyMemory<byte>> Output { get; }

    /// <summary>Frame reader/writer for the negotiated hub protocol. Starts at
    /// <see cref="JsonFraming.Instance"/> (the handshake's own fixed framing) and is switched by
    /// the caller once the handshake request reveals the real protocol.</summary>
    public IHubProtocolFraming Framing { get; set; } = JsonFraming.Instance;

    public bool SupportsBinaryTransferFormat => true;

    /// <summary>
    /// Yields each frame <b>including its own framing</b> (the JSON record separator, or the
    /// MessagePack length prefix) — this is exactly the server-facing <c>payload</c> contract
    /// (04-design.md §11), so callers no longer need to re-apply framing before forwarding a
    /// frame as a <c>client_message</c>.
    /// </summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

        while (true)
        {
            var result = await _receivePipe.Reader.ReadAsync(linked.Token);
            var buffer = result.Buffer;

            while (Framing.TryReadFrame(ref buffer, out var frame))
            {
                yield return frame.ToArray();
            }

            _receivePipe.Reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                yield break;
            }
        }
    }

    public async ValueTask CloseAsync(string? error = null)
    {
        Output.Writer.TryComplete();

        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, error, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // Already closing/closed from the other side; nothing more to do.
            }
        }

        await _cts.CancelAsync();
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
                    if (_socket.State == WebSocketState.CloseReceived)
                    {
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                    }

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

    private async Task RunWriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in Output.Reader.ReadAllAsync(ct))
            {
                if (_socket.State != WebSocketState.Open)
                {
                    break;
                }

                // The WebSocket message type follows the negotiated protocol's transfer format,
                // not the content — verified against a real client: a MessagePack HubConnection
                // sends its (always-JSON) handshake request inside a Binary frame, so the
                // service's handshake *response* must go out as Binary too once the protocol is
                // known (see the type-level doc comment above).
                var messageType = Framing.TransferFormat == TransferFormat.Binary
                    ? WebSocketMessageType.Binary
                    : WebSocketMessageType.Text;
                await _socket.SendAsync(frame, messageType, endOfMessage: true, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Output.Writer.TryComplete();
        await _cts.CancelAsync();

        try
        {
            await Task.WhenAll(_fillPipeTask, _writeLoopTask);
        }
        catch
        {
            // best-effort drain
        }

        _cts.Dispose();
    }
}
