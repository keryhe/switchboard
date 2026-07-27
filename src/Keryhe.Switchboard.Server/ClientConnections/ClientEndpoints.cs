using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.Registry;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// Top-level GET/POST/DELETE /{hub} dispatch (03-protocol.md §1). GET decides which transport is
/// upgrading based on the request shape — a WebSocket upgrade request, an SSE subscription
/// (<c>Accept: text/event-stream</c>), or otherwise a Long Polling establish/poll. POST and DELETE
/// are transport-agnostic: both just need the already-established connection's transport, found by
/// <c>connectionToken</c> via <see cref="ClientConnectionManager.GetTransportByToken"/>.
/// </summary>
public static class ClientEndpoints
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
        IOptions<SwitchboardOptions> options,
        LongPollingConnectionTracker longPollingTracker)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            await ClientConnectionEndpoint.HandleAsync(
                context, hub, tokenService, pendingConnections, connectionRegistry,
                localTransportRegistry, connectionManager, serverConnectionSelector, router, options);
            return;
        }

        if (AcceptsEventStream(context))
        {
            await SseClientEndpoint.HandleGetAsync(
                context, hub, tokenService, pendingConnections, connectionRegistry,
                localTransportRegistry, connectionManager, serverConnectionSelector, router, options);
            return;
        }

        await LongPollingClientEndpoint.HandleGetAsync(
            context, hub, tokenService, pendingConnections, connectionRegistry,
            localTransportRegistry, connectionManager, serverConnectionSelector, router, options, longPollingTracker);
    }

    public static async Task HandlePostAsync(
        HttpContext context,
        string hub,
        ITokenService tokenService,
        ClientConnectionManager connectionManager)
    {
        var principal = ClientConnectionValidation.ValidateAccessToken(context, hub, tokenService);
        if (principal is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var connectionToken = context.Request.Query["id"].ToString();
        if (connectionManager.GetTransportByToken(connectionToken) is not IPostableClientTransport transport)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var ct = context.RequestAborted;
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, ct);
        await transport.FeedAsync(buffer.ToArray(), ct);

        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    public static async Task HandleDeleteAsync(
        HttpContext context,
        string hub,
        ITokenService tokenService,
        ClientConnectionManager connectionManager)
    {
        var principal = ClientConnectionValidation.ValidateAccessToken(context, hub, tokenService);
        if (principal is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var connectionToken = context.Request.Query["id"].ToString();
        var transport = connectionManager.GetTransportByToken(connectionToken);
        if (transport is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await transport.CloseAsync();
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    private static bool AcceptsEventStream(HttpContext context) =>
        context.Request.Headers.Accept.Any(value =>
            value is not null && value.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase));
}
