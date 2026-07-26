using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Phase0.Spike.Host.Stub;

/// <summary>
/// A hand-rolled stand-in for "the Switchboard proxy service" that the negotiate redirect points
/// at. Deliberately NOT mapped via MapHub&lt;T&gt;() — it must not carry NegotiateMetadata, or
/// SwitchboardNegotiateMatcherPolicy would intercept its own negotiate and loop. It implements
/// just enough of the SignalR wire protocol (negotiate response shape + handshake) for an
/// unmodified client to reach the "Connected" state — see spike plan §3/A1, A5.
///
/// Scaffolding only — discarded when Phase 1 replaces it with the real proxy-forwarding call.
/// </summary>
public static class StubTargetEndpoints
{
    public static readonly ConcurrentBag<string> ObservedNegotiateHubs = [];
    public static readonly ConcurrentBag<string> ObservedConnectionHubs = [];

    public static void Map(WebApplication app)
    {
        app.MapPost("/stub/{hub}/negotiate", (string hub) =>
        {
            ObservedNegotiateHubs.Add(hub);
            return Results.Json(new
            {
                connectionId = Guid.NewGuid().ToString(),
                connectionToken = Guid.NewGuid().ToString("N"),
                negotiateVersion = 1,
                availableTransports = new object[]
                {
                    new { transport = "WebSockets", transferFormats = new[] { "Text", "Binary" } }
                }
            });
        });

        app.MapGet("/stub/{hub}", async (HttpContext context, string hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            ObservedConnectionHubs.Add(hub);
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await CompleteHandshakeAsync(socket, context.RequestAborted);

            var buffer = new byte[1024];
            try
            {
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Client/test teardown closed the connection — expected.
            }
            catch (WebSocketException)
            {
                // Peer closed abruptly — expected during test teardown.
            }
        });
    }

    private static async Task CompleteHandshakeAsync(WebSocket socket, CancellationToken ct)
    {
        // Read (and ignore) the client's handshake request; the stub only needs to acknowledge
        // with a successful handshake response for the client to reach "Connected".
        var buffer = new byte[4096];
        await socket.ReceiveAsync(buffer, ct);

        var responseBytes = "{}\x1e"u8.ToArray();
        await socket.SendAsync(responseBytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }
}
