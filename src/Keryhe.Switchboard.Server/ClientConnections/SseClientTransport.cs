using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol.Framing;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// Server-Sent Events transport for a single client connection (03-protocol.md §1.5, 04-design.md
/// §6 "SSE Transport"). Unlike WebSocket, the two directions are two different HTTP requests:
/// inbound frames arrive via <see cref="FeedAsync"/>, called by the POST handler
/// (<see cref="ClientEndpoints"/>) for as long as this connection lives; outbound frames are
/// drained from <see cref="IClientTransport.Output"/> by the GET handler
/// (<see cref="SseClientEndpoint"/>), which streams them as <c>text/event-stream</c> for the
/// lifetime of that one GET request. Text transfer format only (<see cref="SupportsBinaryTransferFormat"/>
/// is always false) — SSE cannot carry MessagePack's binary frames.
/// </summary>
public sealed class SseClientTransport : IPostableClientTransport, IAsyncDisposable
{
    private readonly Pipe _receivePipe = new();
    private readonly CancellationTokenSource _cts = new();

    public SseClientTransport(string connectionId, string hubName, string? userId, int writeChannelCapacity, BoundedChannelFullMode fullMode)
    {
        ConnectionId = connectionId;
        HubName = hubName;
        UserId = userId;
        Output = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(writeChannelCapacity) { FullMode = fullMode });
    }

    public string ConnectionId { get; }
    public string HubName { get; }
    public string? UserId { get; }
    public Channel<ReadOnlyMemory<byte>> Output { get; }

    /// <summary>Always starts (and, per <see cref="SupportsBinaryTransferFormat"/>, stays at)
    /// <see cref="JsonFraming.Instance"/> — SSE has no binary framing to switch to.</summary>
    public IHubProtocolFraming Framing { get; set; } = JsonFraming.Instance;

    public bool SupportsBinaryTransferFormat => false;

    public async Task FeedAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var writer = _receivePipe.Writer;
        var memory = writer.GetMemory(data.Length);
        data.Span.CopyTo(memory.Span);
        writer.Advance(data.Length);
        await writer.FlushAsync(ct);
    }

    /// <summary>Yields each frame including its own framing, same contract as
    /// <see cref="WebSocketClientTransport.ReadAllAsync"/> — see that type's doc comment.</summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

        while (true)
        {
            ReadResult result;
            try
            {
                result = await _receivePipe.Reader.ReadAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

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

    /// <summary>Ends both directions: completes <see cref="Output"/> (so the GET write loop
    /// finishes) and the receive pipe (so <see cref="ReadAllAsync"/>, and therefore
    /// <see cref="ClientConnectionLifecycle.RunAsync"/>'s message loop, finishes) — called on
    /// DELETE (03-protocol.md §1.6) or when the GET stream itself is cancelled.</summary>
    public async ValueTask CloseAsync(string? error = null)
    {
        Output.Writer.TryComplete();
        await _receivePipe.Writer.CompleteAsync();
        await _cts.CancelAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Output.Writer.TryComplete();

        try
        {
            await _receivePipe.Writer.CompleteAsync();
        }
        catch
        {
            // best-effort
        }

        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
