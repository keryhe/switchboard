using System.Net.WebSockets;
using System.Text;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Slice 4 gate: a real .NET HubConnection connects to the service against a hand-rolled app-server
/// "double" (standing in for the real Connector, which is Slice 5). Verifies open_connection is
/// observed before client_message (plan decision D6), that a targeted send from the double reaches
/// the client, and that a broadcast reaches multiple connected clients.
/// </summary>
public class ClientRouterEndToEndTests : IAsyncLifetime
{
    private RealKestrelServerFixture _factory = null!;
    private const string HubName = "chatHub-e2e";

    public async Task InitializeAsync()
    {
        _factory = new RealKestrelServerFixture();
        await _factory.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task OpenConnectionPrecedesClientMessage_AndSendToConnectionReachesClient()
    {
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerDouble = await AppServerDouble.ConnectAsync(_factory.ServerAddress, HubName, serverToken);

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));

        await using var connection = BuildClient(clientToken);

        var received = new TaskCompletionSource<(string From, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string, string>("ReceiveMessage", (from, text) => received.SetResult((from, text)));

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var openConnection = await appServerDouble.ReceiveAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ServerEnvelopeType.OpenConnection, openConnection.Type);
        Assert.Equal("json", openConnection.HubProtocol);
        Assert.Equal("alice", openConnection.UserId);
        var connectionId = openConnection.ConnectionId!;

        await connection.SendAsync("Echo", "hello").WaitAsync(TimeSpan.FromSeconds(5));

        // The client may interleave its own keep-alive Ping frames with real invocations; skip
        // past any that aren't the Echo invocation we're waiting for.
        ServerEnvelope clientMessage;
        do
        {
            clientMessage = await appServerDouble.ReceiveAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ServerEnvelopeType.ClientMessage, clientMessage.Type);
            Assert.Equal(connectionId, clientMessage.ConnectionId);
        }
        while (!Encoding.UTF8.GetString(clientMessage.Payload!).Contains("Echo"));

        // send_to_connection: the double replies directly to the client.
        await appServerDouble.SendToConnectionAsync(connectionId, "ReceiveMessage", "System", "direct-hello");

        var (from, text) = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("System", from);
        Assert.Equal("direct-hello", text);
    }

    [Fact]
    public async Task Broadcast_ReachesTwoConnectedClients()
    {
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerDouble = await AppServerDouble.ConnectAsync(_factory.ServerAddress, HubName, serverToken);

        var token1 = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "bob", null, TimeSpan.FromMinutes(1));
        var token2 = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "carol", null, TimeSpan.FromMinutes(1));

        await using var client1 = BuildClient(token1);
        await using var client2 = BuildClient(token2);

        var received1 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received2 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client1.On<string, string>("ReceiveMessage", (_, text) => received1.SetResult(text));
        client2.On<string, string>("ReceiveMessage", (_, text) => received2.SetResult(text));

        await client1.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServerDouble.ReceiveAsync(TimeSpan.FromSeconds(5)); // open_connection for client1

        await client2.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await appServerDouble.ReceiveAsync(TimeSpan.FromSeconds(5)); // open_connection for client2

        await appServerDouble.BroadcastAsync(HubName, "ReceiveMessage", "System", "broadcast-hello");

        var text1 = await received1.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var text2 = await received2.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("broadcast-hello", text1);
        Assert.Equal("broadcast-hello", text2);
    }

    private HubConnection BuildClient(string clientToken)
    {
        var url = new Uri(_factory.ServerAddress, $"/{HubName}");
        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(clientToken);
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
            })
            .Build();
    }

    /// <summary>Hand-rolled stand-in for the real Connector's server connection (Slice 5).</summary>
    private sealed class AppServerDouble : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket;

        private AppServerDouble(ClientWebSocket socket) => _socket = socket;

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

        public async Task SendToConnectionAsync(string connectionId, string target, params object[] args)
        {
            await SendAsync(new ServerEnvelope
            {
                Type = ServerEnvelopeType.SendToConnection,
                ConnectionId = connectionId,
                HubProtocol = "json",
                Payload = BuildInvocationFrame(target, args),
            });
        }

        public async Task BroadcastAsync(string hubName, string target, params object[] args)
        {
            await SendAsync(new ServerEnvelope
            {
                Type = ServerEnvelopeType.Broadcast,
                HubName = hubName,
                HubProtocol = "json",
                Payload = BuildInvocationFrame(target, args),
            });
        }

        private static byte[] BuildInvocationFrame(string target, object[] args)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new { type = 1, target, arguments = args });
            var writer = new System.Buffers.ArrayBufferWriter<byte>();
            JsonFrameProtocol.WriteFrame(writer, Encoding.UTF8.GetBytes(json));
            return writer.WrittenMemory.ToArray();
        }

        private async Task SendAsync(ServerEnvelope envelope)
        {
            var writer = new System.Buffers.ArrayBufferWriter<byte>();
            ServerEnvelopeSerializer.Write(writer, envelope);
            await _socket.SendAsync(writer.WrittenMemory, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        }

        public async Task<ServerEnvelope> ReceiveAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var buffer = new byte[16 * 1024];
            var totalRead = 0;

            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(totalRead), cts.Token);
                totalRead += result.Count;

                if (ServerEnvelopeSerializer.TryParseEnvelope(
                        new System.Buffers.ReadOnlySequence<byte>(buffer.AsMemory(0, totalRead)),
                        out var envelope, out _, out _))
                {
                    return envelope!;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch
            {
                // best-effort
            }

            _socket.Dispose();
        }
    }
}
