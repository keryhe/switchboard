using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.Registry;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// GET /{hub}?id={connectionToken}&amp;access_token={jwt} — the WebSocket transport upgrade
/// (03-protocol.md §1.2, 04-design.md §2). A thin WebSocket-specific adapter (plan decision D10):
/// everything transport-independent — handshake negotiation, registration, the message loop,
/// teardown — lives in <see cref="ClientConnectionLifecycle"/>, so SSE (Slice 5) and Long Polling
/// (Slice 6) reuse it unchanged. Only WebSocket accept/read/write specifics live here. Dispatched
/// to from <see cref="ClientEndpoints"/> once a request is confirmed to be a WebSocket upgrade.
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
        ClientConnectionManager connectionManager,
        IServerConnectionSelector serverConnectionSelector,
        IMessageRouter router,
        IOptions<SwitchboardOptions> options)
    {
        if (!ClientConnectionValidation.IsOriginAllowed(context, options.Value.AllowedOrigins))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var connectionToken = context.Request.Query["id"].ToString();
        var result = ClientConnectionValidation.Establish(
            context, hub, connectionToken, tokenService, pendingConnections, serverConnectionSelector);
        if (!result.Success)
        {
            context.Response.StatusCode = result.ErrorStatusCode!.Value;
            return;
        }

        var pending = result.Pending!;
        var serverConnectionState = result.ServerConnectionState!;

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

        var protocol = await ClientConnectionLifecycle.NegotiateHandshakeAsync(transport, enumerator.Current);
        if (protocol is null)
        {
            return;
        }

        var connection = new ClientConnection(pending.ConnectionId, connectionToken, hub, pending.UserId, transport, protocol);

        await ClientConnectionLifecycle.RunAsync(
            connection, pending, enumerator, connectionRegistry, localTransportRegistry,
            connectionManager, serverConnectionState, router, options.Value.ClientKeepAliveInterval, ct);
    }
}
