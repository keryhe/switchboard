using System.Text.Json;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.Registry;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// GET /{hub}?id={connectionToken}&amp;access_token={jwt} — the client transport upgrade
/// (03-protocol.md §1.2, 04-design.md §2). Phase 1 is WebSocket + JSON hub-protocol only.
/// </summary>
public static class ClientConnectionEndpoint
{
    public static async Task HandleAsync(
        HttpContext context,
        string hub,
        ITokenService tokenService,
        IPendingConnectionStore pendingConnections,
        IConnectionRegistry connectionRegistry,
        ILocalTransportRegistry localTransportRegistry,
        IServerConnectionSelector serverConnectionSelector,
        IMessageRouter router,
        IOptions<SwitchboardOptions> options)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // The .NET SignalR client sets the Authorization header directly on the WebSocket upgrade
        // (it isn't restricted like a browser); browser clients cannot, so they fall back to the
        // access_token query parameter (03-protocol.md §1.2) — accept either.
        var authHeader = context.Request.Headers.Authorization.ToString();
        var accessToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : context.Request.Query["access_token"].ToString();

        var principal = tokenService.Validate(accessToken, SwitchboardTokenType.Client);
        if (principal is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (principal.FindFirst("hubName")?.Value != hub)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var connectionToken = context.Request.Query["id"].ToString();
        var pending = pendingConnections.TryConsume(connectionToken);
        if (pending is null || pending.HubName != hub)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var serverConnectionState = serverConnectionSelector.SelectConnection(hub);
        if (serverConnectionState is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var ct = context.RequestAborted;

        await using var transport = new WebSocketClientTransport(
            socket, pending.ConnectionId, hub, pending.UserId,
            options.Value.WriteChannelCapacity, options.Value.WriteChannelFullMode);

        var enumerator = transport.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        if (!await enumerator.MoveNextAsync())
        {
            return;
        }

        var (protocolOk, requestedProtocol) = TryParseHandshakeProtocol(enumerator.Current);
        if (!protocolOk || requestedProtocol != "json")
        {
            await WriteFrameAsync(transport, JsonSerializer.SerializeToUtf8Bytes(new { error = $"Requested protocol '{requestedProtocol}' is not available." }));
            await transport.CloseAsync("Handshake failed.");
            return;
        }

        await WriteFrameAsync(transport, "{}"u8.ToArray());

        var state = new ClientConnectionState
        {
            ConnectionId = pending.ConnectionId,
            ConnectionToken = connectionToken,
            HubName = hub,
            UserId = pending.UserId,
            Transport = TransportType.WebSockets,
            TransportHandle = transport,
            ServerConnectionId = serverConnectionState.ConnectionId,
            ConnectedAt = DateTimeOffset.UtcNow,
        };

        await connectionRegistry.RegisterAsync(state, ct);
        localTransportRegistry.Register(transport);
        serverConnectionState.IncrementLogicalCount();

        await connectionRegistry.SetProtocolAsync(pending.ConnectionId, "json", ct);

        await serverConnectionState.Connection.SendAsync(new ServerEnvelope
        {
            Type = ServerEnvelopeType.OpenConnection,
            ConnectionId = pending.ConnectionId,
            HubProtocol = "json",
            UserId = pending.UserId,
            Claims = pending.Claims,
        }, ct);

        try
        {
            while (await enumerator.MoveNextAsync())
            {
                // Re-apply the \x1e record separator that the transport's frame reader stripped.
                // The server-facing `payload` contract (03-protocol.md §2.4, 04-design.md §11) is
                // "raw hub-protocol bytes *including* framing" — the Connector writes payload
                // verbatim into the synthetic connection's pipe, where IHubProtocol.TryParseMessage
                // needs the delimiter to see a complete message. Without it the bytes sit in the
                // pipe unparsed forever: the hub method never dispatches, nothing faults, and the
                // only symptom is the client's InvokeAsync timing out (plus, at teardown, SignalR's
                // "Connection terminated while reading a message." for the undelimited remainder).
                // The outbound direction is already framed, since IHubProtocol.WriteMessage emits
                // the delimiter itself.
                await router.RouteClientMessageAsync(pending.ConnectionId, FrameForServer(enumerator.Current), "json", ct);
            }
        }
        finally
        {
            await serverConnectionState.Connection.SendAsync(new ServerEnvelope
            {
                Type = ServerEnvelopeType.CloseConnection,
                ConnectionId = pending.ConnectionId,
            }, CancellationToken.None);

            serverConnectionState.DecrementLogicalCount();
            localTransportRegistry.Unregister(pending.ConnectionId);
            await connectionRegistry.UnregisterAsync(pending.ConnectionId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Restores the JSON hub-protocol framing around a single delimiter-stripped frame so it can be
    /// carried verbatim as a <c>client_message</c> payload.
    /// </summary>
    private static ReadOnlyMemory<byte> FrameForServer(ReadOnlyMemory<byte> frame)
    {
        var framed = new byte[frame.Length + 1];
        frame.CopyTo(framed);
        framed[frame.Length] = JsonFrameProtocol.RecordSeparator;
        return framed;
    }

    private static async ValueTask WriteFrameAsync(WebSocketClientTransport transport, byte[] json)
    {
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        JsonFrameProtocol.WriteFrame(writer, json);
        await transport.Output.Writer.WriteAsync(writer.WrittenMemory);
    }

    private static (bool Ok, string? Protocol) TryParseHandshakeProtocol(ReadOnlyMemory<byte> frame)
    {
        try
        {
            using var doc = JsonDocument.Parse(frame);
            return (true, doc.RootElement.GetProperty("protocol").GetString());
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }
}
