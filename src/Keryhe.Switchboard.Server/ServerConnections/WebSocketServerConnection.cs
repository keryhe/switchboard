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

    public async ValueTask SendAsync(ServerEnvelope envelope, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            ServerEnvelopeSerializer.Write(buffer, envelope);
            await _socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Binary, endOfMessage: true, ct);
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
