using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Protocol;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Server.ServerConnections;

/// <summary>
/// wss://service/server/{hubName} — app server connection establishment (03-protocol.md §2.1/§2.2,
/// 04-design.md §3). Validates the server token, accepts the WebSocket, runs the handshake, then
/// keeps the connection registered and ping/pong-healthy until it drops.
/// </summary>
public static class ServerConnectionEndpoint
{
    private const int SupportedHandshakeVersion = 1;

    public static async Task HandleAsync(
        HttpContext context,
        string hub,
        ITokenService tokenService,
        IHubRegistry hubRegistry,
        IServerEnvelopeDispatcher dispatcher,
        IOptions<SwitchboardOptions> options,
        ILogger<WebSocketServerConnection> logger)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var principal = tokenService.Validate(authHeader["Bearer ".Length..].Trim(), SwitchboardTokenType.Server);
        if (principal is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var allowedHubs = principal.FindAll("hubs").Select(c => c.Value).ToHashSet();
        if (!allowedHubs.Contains(hub))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var appServerId = principal.FindFirst("sub")?.Value ?? "unknown";
        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new WebSocketServerConnection(socket, Guid.NewGuid().ToString("n"), hub);

        var ct = context.RequestAborted;
        var envelopes = connection.ReadAllAsync(ct).GetAsyncEnumerator(ct);

        if (!await envelopes.MoveNextAsync())
        {
            await connection.DisposeAsync();
            return;
        }

        var handshake = envelopes.Current;
        if (handshake.Type != ServerEnvelopeType.Handshake || handshake.Version != SupportedHandshakeVersion)
        {
            await connection.SendAsync(new ServerEnvelope
            {
                Type = ServerEnvelopeType.HandshakeError,
                Error = $"Unsupported protocol version: {handshake.Version}",
            }, ct);
            await connection.DisposeAsync();
            return;
        }

        var state = new ServerConnectionState
        {
            ConnectionId = connection.ConnectionId,
            HubName = hub,
            AppServerId = appServerId,
            Connection = connection,
            ConnectedAt = DateTimeOffset.UtcNow,
        };

        // Register before acking: a caller that receives the ack must be able to observe the
        // connection in the hub registry immediately afterwards.
        await hubRegistry.RegisterServerConnectionAsync(state, ct);

        await connection.SendAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.HandshakeAck,
            ConnectionId = connection.ConnectionId,
        }, ct);

        try
        {
            using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pingLoop = RunPingLoopAsync(connection, state, options.Value, pingCts.Token);

            try
            {
                while (await envelopes.MoveNextAsync())
                {
                    var envelope = envelopes.Current;
                    if (envelope.Type == ServerEnvelopeType.Pong)
                    {
                        state.LastPongReceived = DateTimeOffset.UtcNow;
                        state.Status = ServerConnectionStatus.Connected;
                        continue;
                    }

                    await dispatcher.DispatchAsync(connection.ConnectionId, envelope, ct);
                }
            }
            finally
            {
                await pingCts.CancelAsync();
                try
                {
                    await pingLoop;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            await hubRegistry.UnregisterServerConnectionAsync(hub, connection.ConnectionId, ct);
            await connection.DisposeAsync();
        }
    }

    private static async Task RunPingLoopAsync(WebSocketServerConnection connection, ServerConnectionState state, SwitchboardOptions options, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.ServerPingInterval);

        while (await timer.WaitForNextTickAsync(ct))
        {
            state.LastPingSent = DateTimeOffset.UtcNow;

            if (state.LastPongReceived != default &&
                DateTimeOffset.UtcNow - state.LastPongReceived > options.ServerPingInterval + options.ServerPingTimeout)
            {
                state.Status = ServerConnectionStatus.Degraded;
            }

            await connection.SendAsync(new ServerEnvelope { Type = ServerEnvelopeType.Ping }, ct);
        }
    }
}
