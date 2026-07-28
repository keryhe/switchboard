using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Keryhe.Switchboard.Protocol;

namespace Keryhe.Switchboard.Connector.ServerConnections;

/// <summary>
/// One physical WebSocket connection from this app server to the Switchboard service
/// (03-protocol.md §2.1/§2.2). The Connector opens <c>ServerConnectionsPerHub</c> of these per hub.
/// </summary>
public sealed class ConnectorServerConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Pipe _receivePipe = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _fillPipeTask;

    private ConnectorServerConnection(ClientWebSocket socket) => _socket = socket;

    /// <summary>
    /// Connects and sends the handshake request, but deliberately does NOT read the
    /// HandshakeAck itself — <see cref="PipeReader"/> allows only one logical reader across a
    /// connection's lifetime (a second, independent enumerator over the same reader throws
    /// "Reading is already in progress" if the first was abandoned mid-read without completing
    /// its AdvanceTo cycle). The caller must consume the handshake response as the first element
    /// of <see cref="ReadAllAsync"/> and keep using that same enumerator for the connection's
    /// remaining lifetime.
    /// </summary>
    public static async Task<ConnectorServerConnection> ConnectAsync(Uri serviceUrl, string hubName, string serverAccessToken, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {serverAccessToken}");

        var wsUri = new UriBuilder(serviceUrl) { Scheme = serviceUrl.Scheme == "https" ? "wss" : "ws", Port = serviceUrl.Port, Path = $"/server/{hubName}" }.Uri;
        await socket.ConnectAsync(wsUri, ct);

        var connection = new ConnectorServerConnection(socket);
        connection._fillPipeTask = connection.FillPipeAsync(connection._cts.Token);

        await connection.SendAsync(new ServerEnvelope { Type = ServerEnvelopeType.Handshake, HubName = hubName, Version = 1 }, ct);

        return connection;
    }

    public bool IsOpen => _socket.State == WebSocketState.Open;

    /// <summary>
    /// <paramref name="ct"/> bounds the wait for the write lock but deliberately does NOT reach the
    /// socket write — the app-server end of the same rule <c>WebSocketServerConnection.SendAsync</c>
    /// documents on the service end.
    /// </summary>
    /// <remarks>
    /// Cancelling an in-flight WebSocket send aborts the whole socket, and this socket is shared by
    /// every client this app server hosts for the hub — one pool slot serving all of them. Per-client
    /// tokens genuinely arrive here: a hub method's own <c>CancellationToken</c> flows through
    /// <c>SwitchboardHubLifetimeManager</c>, and <c>InboundDispatcher.RunOutboundReaderAsync</c>
    /// passes its per-connection token to <c>sendToService</c>. Letting either abort the pool
    /// connection would drop every other client's messages until it reconnected.
    /// </remarks>
    public async ValueTask SendAsync(ServerEnvelope envelope, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
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
        if (_fillPipeTask is not null)
        {
            try
            {
                await _fillPipeTask;
            }
            catch
            {
            }
        }

        _cts.Dispose();
        _writeLock.Dispose();
        _socket.Dispose();
    }
}
