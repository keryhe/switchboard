using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol.Framing;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// Long Polling transport for a single client connection (03-protocol.md §1.6). Like
/// <see cref="SseClientTransport"/>, inbound frames arrive over a separate POST
/// (<see cref="FeedAsync"/>); unlike SSE, outbound frames aren't streamed continuously — they're
/// buffered in <see cref="IClientTransport.Output"/> until a poll GET drains them
/// (<see cref="PollAsync"/>), matching the real ASP.NET Core server's own behavior (verified by
/// decompiling <c>LongPollingServerTransport.ProcessRequestAsync</c>): a poll that times out with
/// no data returns 200 with an empty body so the client keeps polling, and only a poll that finds
/// the connection actually closed (<see cref="IClientTransport.Output"/>'s writer completed, with
/// nothing left buffered) returns 204 — the real client's own <c>LongPollingTransport.Poll</c>
/// treats <em>any</em> 204 as "the connection is over," so a plain idle timeout must never produce
/// one or the connection would die on every quiet period.
/// </summary>
public sealed class LongPollingClientTransport : IPostableClientTransport, IAsyncDisposable
{
    private readonly Pipe _receivePipe = new();
    private readonly CancellationTokenSource _cts = new();

    public LongPollingClientTransport(string connectionId, string hubName, string? userId, int writeChannelCapacity, BoundedChannelFullMode fullMode)
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

    public IHubProtocolFraming Framing { get; set; } = JsonFraming.Instance;

    /// <summary>Long Polling can carry MessagePack (unlike SSE) — it's plain request/response
    /// bodies, no text-only event-stream framing involved.</summary>
    public bool SupportsBinaryTransferFormat => true;

    /// <summary>Updated on every poll (establishing GET or subsequent poll GET) — read by
    /// <see cref="LongPollingReaperService"/> against <c>SwitchboardOptions.DisconnectTimeout</c>
    /// to detect a client that has simply stopped polling without ever sending DELETE.</summary>
    public DateTimeOffset LastPollAt { get; set; } = DateTimeOffset.UtcNow;

    public async Task FeedAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var writer = _receivePipe.Writer;
        var memory = writer.GetMemory(data.Length);
        data.Span.CopyTo(memory.Span);
        writer.Advance(data.Length);
        await writer.FlushAsync(ct);
    }

    /// <summary>Yields each frame including its own framing, same contract as
    /// <see cref="WebSocketClientTransport.ReadAllAsync"/>.</summary>
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

    /// <summary>
    /// Drains whatever's currently buffered in <see cref="Output"/>, waiting up to
    /// <paramref name="timeout"/> for at least one frame to arrive if nothing is buffered yet.
    /// </summary>
    public async Task<LongPollResult> PollAsync(TimeSpan timeout, CancellationToken requestAborted)
    {
        LastPollAt = DateTimeOffset.UtcNow;

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, timeoutCts.Token);

        bool hasData;
        try
        {
            hasData = await Output.Reader.WaitToReadAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            // Poll timed out with nothing buffered — 200 empty, not 204, or the real client's
            // Poll() loop would treat this quiet period as "connection over" and stop polling.
            return LongPollResult.Empty;
        }

        if (!hasData)
        {
            // Output's writer completed (CloseAsync) and nothing was left to drain — the
            // connection is genuinely done.
            return LongPollResult.ClosedResult;
        }

        var writer = new ArrayBufferWriter<byte>();
        while (Output.Reader.TryRead(out var frame))
        {
            writer.Write(frame.Span);
        }

        return LongPollResult.WithData(writer.WrittenMemory.ToArray());
    }

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

/// <summary>Outcome of one poll: either the connection is over (204), or here's whatever was
/// buffered — which may be empty if the poll simply timed out with nothing to report (200, no
/// body either way, only <see cref="Closed"/> distinguishes the two).</summary>
public readonly record struct LongPollResult(bool Closed, byte[]? Data)
{
    public static readonly LongPollResult Empty = new(false, null);
    public static readonly LongPollResult ClosedResult = new(true, null);

    public static LongPollResult WithData(byte[] data) => new(false, data);
}
