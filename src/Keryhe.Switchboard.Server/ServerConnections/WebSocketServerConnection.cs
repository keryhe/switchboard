using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Keryhe.Switchboard.Protocol;

namespace Keryhe.Switchboard.Server.ServerConnections;

/// <summary>
/// A physical app-server WebSocket connection. Enforces single-writer ordering per connection
/// (plan decision D6 depends on this: open_connection and every subsequent client_message for a
/// given client must be written to the same server connection, in order, by one writer).
/// </summary>
public sealed class WebSocketServerConnection : IServerConnection, IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Pipe _receivePipe = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _fillPipeTask;
    private int _logicalCount;

    public WebSocketServerConnection(WebSocket socket, string connectionId, string hubName)
    {
        _socket = socket;
        ConnectionId = connectionId;
        HubName = hubName;
        _fillPipeTask = FillPipeAsync(_cts.Token);
    }

    public string ConnectionId { get; }
    public string HubName { get; }
    public int LogicalConnectionCount => _logicalCount;

    public void IncrementLogicalCount() => Interlocked.Increment(ref _logicalCount);
    public void DecrementLogicalCount() => Interlocked.Decrement(ref _logicalCount);

    /// <summary>
    /// <paramref name="ct"/> bounds the wait for the write lock, but deliberately does NOT reach
    /// the socket write itself — that is scoped to this connection's own lifetime instead.
    /// </summary>
    /// <remarks>
    /// Cancelling a <see cref="WebSocket.SendAsync(ReadOnlyMemory{byte}, WebSocketMessageType, bool, CancellationToken)"/>
    /// that is already in flight aborts the entire WebSocket — the protocol cannot resync after a
    /// half-written frame, so .NET has no other option. That is catastrophic here and not merely
    /// untidy: this socket is <b>shared</b>. Callers reach it with a single client's
    /// <c>HttpContext.RequestAborted</c> (<c>DefaultMessageRouter.RouteClientMessageAsync</c> and
    /// <c>ClientConnectionLifecycle</c>'s open_connection send both do), so one client
    /// disconnecting at the wrong instant would abort the app-server connection that every other
    /// client assigned to it depends on — a TCP reset with no close handshake, taking down message
    /// delivery for the whole pool slot until the app server reconnects. Verified: this is exactly
    /// what made an end-to-end test intermittently observe "connection reset by peer" right as an
    /// unrelated client called StopAsync. Waiting for the lock is safe to cancel because nothing
    /// has been written yet; the write is not.
    /// </remarks>
    public async ValueTask SendAsync(ServerEnvelope envelope, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            ServerEnvelopeSerializer.Write(buffer, envelope);
            await _socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Binary, endOfMessage: true, _cts.Token);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<ServerEnvelope> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

        while (true)
        {
            var result = await _receivePipe.Reader.ReadAsync(linked.Token);
            var buffer = result.Buffer;

            while (ServerEnvelopeSerializer.TryParseEnvelope(buffer, out var envelope, out var consumed, out var examined))
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
                    // Complete the close handshake so a client awaiting CloseAsync() doesn't hang
                    // forever waiting for our close frame.
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
            // Connection torn down; fall through to complete the pipe below.
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
            await _fillPipeTask;
        }
        catch
        {
            // best-effort drain
        }

        _cts.Dispose();
        _writeLock.Dispose();
    }
}
