using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// WebSocket transport for a single client connection (03-protocol.md §1.2). Phase 1 is JSON
/// hub-protocol only, so framing is the \x1e record separator (04-design.md §6, plan Slice 4).
/// </summary>
public sealed class WebSocketClientTransport : IClientTransport, IAsyncDisposable
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

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

        while (true)
        {
            var result = await _receivePipe.Reader.ReadAsync(linked.Token);
            var buffer = result.Buffer;

            while (JsonFrameProtocol.TryParseFrame(ref buffer, out var frame))
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

                await _socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, ct);
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
