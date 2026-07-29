using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.Registry;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// GET /{hub}?id={connectionToken}&amp;access_token={jwt} with no WebSocket upgrade and no
/// <c>Accept: text/event-stream</c> — Long Polling (03-protocol.md §1.6). The very first GET for a
/// given <c>connectionToken</c> establishes the connection and returns immediately (matching the
/// real client's <c>LongPollingTransport.StartAsync</c>, which only checks for a success status —
/// it never reads this response's body); every GET after that is a poll, answered by
/// <see cref="LongPollingClientTransport.PollAsync"/>. Unlike WebSocket and SSE, the connection's
/// lifecycle spans many independent short-lived requests (plan decision D10), so the handshake and
/// message loop run on a background task detached from any single request.
/// </summary>
public static class LongPollingClientEndpoint
{
    public static async Task HandleGetAsync(
        HttpContext context,
        string hub,
        ITokenService tokenService,
        IPendingConnectionStore pendingConnections,
        IConnectionRegistry connectionRegistry,
        ILocalTransportRegistry localTransportRegistry,
        ClientConnectionManager connectionManager,
        IHubRegistry hubRegistry,
        IServerConnectionSelector serverConnectionSelector,
        IBackplane backplane,
        IMessageRouter router,
        IOptions<SwitchboardOptions> options,
        LongPollingConnectionTracker tracker,
        ClientConnectionForwarder forwarder,
        ITransportOwnershipRegistry ownershipRegistry,
        SwitchboardMetrics metrics,
        SwitchboardTracing tracing)
    {
        var connectionToken = context.Request.Query["id"].ToString();

        if (connectionManager.GetTransportByToken(connectionToken) is LongPollingClientTransport pollingTransport)
        {
            await HandlePollAsync(context, hub, tokenService, options, pollingTransport);
            return;
        }

        // Not local. Two possibilities: a genuinely new connectionToken that has never been
        // established anywhere (proceed to HandleEstablishAsync, legal from any node since Slice
        // 1's pending-connection grain is cluster-wide), or an already-established connection whose
        // poll landed on a node other than the one holding its transport (plan decision D19) —
        // distinguished by resolving connectionToken against ITransportOwnershipRegistry, claimed by
        // whichever node's GET actually established it. Only the latter forwards; the former must
        // never be forwarded, since establishing here is exactly as legal as establishing anywhere
        // else.
        if (await forwarder.TryForwardAsync(context, connectionToken, context.RequestAborted))
        {
            return;
        }

        await HandleEstablishAsync(
            context, hub, connectionToken, tokenService, pendingConnections, connectionRegistry,
            localTransportRegistry, connectionManager, hubRegistry, serverConnectionSelector, backplane, router, options, tracker,
            ownershipRegistry, metrics, tracing);
    }

    private static async Task HandlePollAsync(
        HttpContext context,
        string hub,
        ITokenService tokenService,
        IOptions<SwitchboardOptions> options,
        LongPollingClientTransport transport)
    {
        var principal = ClientConnectionValidation.ValidateAccessToken(context, hub, tokenService);
        if (principal is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        LongPollResult result;
        try
        {
            result = await transport.PollAsync(options.Value.LongPollTimeout, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected mid-poll — nothing to respond with.
            return;
        }

        if (result.Closed)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/octet-stream";
        if (result.Data is { Length: > 0 } data)
        {
            context.Response.ContentLength = data.Length;
            await context.Response.Body.WriteAsync(data, context.RequestAborted);
        }
        else
        {
            context.Response.ContentLength = 0;
        }
    }

    private static async Task HandleEstablishAsync(
        HttpContext context,
        string hub,
        string connectionToken,
        ITokenService tokenService,
        IPendingConnectionStore pendingConnections,
        IConnectionRegistry connectionRegistry,
        ILocalTransportRegistry localTransportRegistry,
        ClientConnectionManager connectionManager,
        IHubRegistry hubRegistry,
        IServerConnectionSelector serverConnectionSelector,
        IBackplane backplane,
        IMessageRouter router,
        IOptions<SwitchboardOptions> options,
        LongPollingConnectionTracker tracker,
        ITransportOwnershipRegistry ownershipRegistry,
        SwitchboardMetrics metrics,
        SwitchboardTracing tracing)
    {
        var result = await ClientConnectionValidation.EstablishAsync(
            context, hub, connectionToken, tokenService, pendingConnections, serverConnectionSelector, options.Value.NodeId, metrics);
        if (!result.Success)
        {
            context.Response.StatusCode = result.ErrorStatusCode!.Value;
            return;
        }

        var pending = result.Pending!;
        var serverConnectionRef = result.ServerConnectionRef!;

        var transport = new LongPollingClientTransport(
            pending.ConnectionId, hub, pending.UserId,
            options.Value.WriteChannelCapacity, options.Value.WriteChannelFullMode);

        connectionManager.RegisterTransport(connectionToken, transport);
        tracker.Register(connectionToken, transport);

        // Claimed before the establishing GET's response is sent (plan decision D19) — the
        // handshake POST that follows can arrive on another node the instant this response reaches
        // the client, and it must already be able to resolve this node as the owner.
        await ownershipRegistry.ClaimAsync(connectionToken, options.Value.NodeId, context.RequestAborted);

        RunConnectionLifecycle(
            transport, pending, connectionToken, hub, connectionRegistry, localTransportRegistry,
            connectionManager, tracker, hubRegistry, serverConnectionSelector, backplane, serverConnectionRef,
            options.Value.NodeId, router, options.Value.ClientKeepAliveInterval, ownershipRegistry, tracing);

        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    /// <summary>Detached from the establishing request on purpose — it must keep running across
    /// every subsequent poll/send request for the rest of the connection's life, long after the
    /// establishing GET has returned.</summary>
    private static void RunConnectionLifecycle(
        LongPollingClientTransport transport,
        PendingConnection pending,
        string connectionToken,
        string hub,
        IConnectionRegistry connectionRegistry,
        ILocalTransportRegistry localTransportRegistry,
        ClientConnectionManager connectionManager,
        LongPollingConnectionTracker tracker,
        IHubRegistry hubRegistry,
        IServerConnectionSelector serverConnectionSelector,
        IBackplane backplane,
        string serverConnectionRef,
        string localNodeId,
        IMessageRouter router,
        TimeSpan keepAliveInterval,
        ITransportOwnershipRegistry ownershipRegistry,
        SwitchboardTracing tracing)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var enumerator = transport.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator(CancellationToken.None);
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
                    connection, pending, enumerator, TransportType.LongPolling, connectionRegistry, localTransportRegistry,
                    connectionManager, hubRegistry, serverConnectionSelector, backplane, serverConnectionRef, localNodeId,
                    router, keepAliveInterval, tracing, CancellationToken.None);
            }
            finally
            {
                connectionManager.UnregisterTransport(connectionToken);
                tracker.Unregister(connectionToken);
                await ownershipRegistry.ReleaseAsync(connectionToken, CancellationToken.None);
            }
        });
    }
}
