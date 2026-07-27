using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.Registry;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// GET /{hub}?id={connectionToken}&amp;access_token={jwt} with <c>Accept: text/event-stream</c> —
/// the SSE transport upgrade (03-protocol.md §1.5). Establishes the connection exactly like
/// <see cref="ClientConnectionEndpoint"/> does for WebSocket, but the request itself only ever
/// carries the write side: the handshake request and every subsequent inbound frame arrive over a
/// separate POST (<see cref="ClientEndpoints.HandlePostAsync"/>), fed into the same
/// <see cref="SseClientTransport"/> via <see cref="ClientConnectionManager.GetTransportByToken"/>.
/// </summary>
public static class SseClientEndpoint
{
    public static async Task HandleGetAsync(
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
        var ct = context.RequestAborted;

        await using var transport = new SseClientTransport(
            pending.ConnectionId, hub, pending.UserId,
            options.Value.WriteChannelCapacity, options.Value.WriteChannelFullMode);

        // Findable by POST (the handshake request itself, then every message after) before a
        // ClientConnection exists to register normally — see ClientConnectionManager's doc comment.
        connectionManager.RegisterTransport(connectionToken, transport);

        try
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.StartAsync(ct);
            // Kestrel doesn't push headers to the client on StartAsync alone — an explicit flush
            // is required, or the real SignalR SSE client's GET never observes response headers
            // and hangs forever (verified against a real HubConnection pinned to
            // HttpTransportType.ServerSentEvents; without this the handshake POST that's supposed
            // to follow never even happens, since the client is still waiting on the GET).
            await context.Response.Body.FlushAsync(ct);

            var enumerator = transport.ReadAllAsync(ct).GetAsyncEnumerator(ct);
            if (!await enumerator.MoveNextAsync())
            {
                return;
            }

            var protocol = await ClientConnectionLifecycle.NegotiateHandshakeAsync(transport, enumerator.Current);
            if (protocol is null)
            {
                // The handshake error frame is already sitting in Output — drain it out over the
                // stream before ending the request, or the client never sees why it failed.
                await WriteOutputAsync(context, transport, ct);
                return;
            }

            var connection = new ClientConnection(pending.ConnectionId, connectionToken, hub, pending.UserId, transport, protocol);

            var lifecycleTask = ClientConnectionLifecycle.RunAsync(
                connection, pending, enumerator, connectionRegistry, localTransportRegistry,
                connectionManager, serverConnectionState, router, options.Value.ClientKeepAliveInterval, ct);

            await WriteOutputAsync(context, transport, ct);
            await lifecycleTask;
        }
        finally
        {
            connectionManager.UnregisterTransport(connectionToken);
        }
    }

    private static async Task WriteOutputAsync(HttpContext context, SseClientTransport transport, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in transport.Output.Reader.ReadAllAsync(ct))
            {
                // 03-protocol.md §1.5: "data: " + frame (which already carries its own trailing
                // \x1e record separator) + a blank line.
                await context.Response.WriteAsync("data: ", ct);
                await context.Response.Body.WriteAsync(frame, ct);
                await context.Response.WriteAsync("\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // GET stream cancelled (client disconnected) — SseClientTransport.ReadAllAsync's own
            // cancellation handling is what actually ends ClientConnectionLifecycle.RunAsync's
            // message loop and runs its teardown; nothing more to do here.
        }
    }
}
