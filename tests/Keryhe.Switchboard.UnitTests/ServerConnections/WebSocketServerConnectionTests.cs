using System.Net.WebSockets;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.Server.ServerConnections;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.ServerConnections;

public class WebSocketServerConnectionTests
{
    /// <summary>
    /// An app-server socket is shared by every client assigned to it, but callers reach
    /// <see cref="WebSocketServerConnection.SendAsync"/> holding a single client's
    /// <c>HttpContext.RequestAborted</c> — <c>DefaultMessageRouter.RouteClientMessageAsync</c> and
    /// <c>ClientConnectionLifecycle</c>'s open_connection send both do. Cancelling an in-flight
    /// WebSocket send aborts the entire socket (the protocol cannot resync after a half-written
    /// frame), so if that token reached the write, one client disconnecting at the wrong moment
    /// would reset the connection every other client on that pool slot depends on.
    /// </summary>
    /// <remarks>
    /// Asserted on the token the socket actually receives rather than by racing a real disconnect:
    /// the bug this pins reproduced roughly once per ten end-to-end runs and stopped reproducing
    /// entirely under the extra I/O of verbose logging, so a timing-based test would be a poor
    /// guard against its return.
    /// </remarks>
    [Fact]
    public async Task SendAsync_DoesNotPassTheCallersCancellationTokenToTheSocket()
    {
        var socket = new RecordingWebSocket();
        await using var connection = new WebSocketServerConnection(socket, "srv-1", "chatHub");

        using var callerCts = new CancellationTokenSource();
        await connection.SendAsync(new ServerEnvelope { Type = ServerEnvelopeType.Ping }, callerCts.Token);

        Assert.True(socket.SendWasCalled);

        // The caller cancelling afterwards must not be able to reach the socket's write — if the
        // caller's token had been handed down, this would cancel it and abort the shared socket.
        await callerCts.CancelAsync();
        Assert.False(socket.TokenSeenBySend.IsCancellationRequested);
    }

    /// <summary>The caller's token still bounds the wait for the write lock — safe to cancel there,
    /// because nothing has been written to the socket yet.</summary>
    [Fact]
    public async Task SendAsync_WithAlreadyCancelledCallerToken_ThrowsWithoutWritingToTheSocket()
    {
        var socket = new RecordingWebSocket();
        await using var connection = new WebSocketServerConnection(socket, "srv-1", "chatHub");

        using var callerCts = new CancellationTokenSource();
        await callerCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await connection.SendAsync(new ServerEnvelope { Type = ServerEnvelopeType.Ping }, callerCts.Token));

        Assert.False(socket.SendWasCalled);
    }

    private sealed class RecordingWebSocket : WebSocket
    {
        public bool SendWasCalled { get; private set; }
        public CancellationToken TokenSeenBySend { get; private set; }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SendWasCalled = true;
            TokenSeenBySend = cancellationToken;
            return ValueTask.CompletedTask;
        }

        public override WebSocketState State => WebSocketState.Open;

        // Never completes: stands in for a socket with no inbound traffic, so the connection's own
        // receive loop stays parked exactly as it would against a quiet app server.
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            new TaskCompletionSource<WebSocketReceiveResult>().Task.WaitAsync(cancellationToken);

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            new(new TaskCompletionSource<ValueWebSocketReceiveResult>().Task.WaitAsync(cancellationToken));

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
    }
}
