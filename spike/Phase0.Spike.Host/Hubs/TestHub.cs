using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Phase0.Spike.Host.Hubs;

/// <summary>
/// The primary hub used by both workstreams: real HTTP negotiate/connect tests (Workstream A)
/// and synthetic-connection dispatch tests (Workstream B, driven directly via
/// HubPipelineFactory — no HTTP involved). Connection IDs are unique per test, so the static
/// activity bags are safe to share across parallel test runs as long as tests filter by their
/// own connection ID rather than asserting on bag contents wholesale.
/// </summary>
public class TestHub : Hub
{
    public static readonly ConcurrentBag<string> ConnectedIds = [];
    public static readonly ConcurrentBag<string> DisconnectedIds = [];

    public override Task OnConnectedAsync()
    {
        ConnectedIds.Add(Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        DisconnectedIds.Add(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public string Echo(string message) => message;

    public string UserIdentifierValue() => Context.UserIdentifier ?? "(none)";

    public bool IsAuthenticated() => Context.User?.Identity?.IsAuthenticated ?? false;

    public string HttpContextStatus() => Context.GetHttpContext() is null ? "null" : "present";

    [Authorize]
    public string SecretEcho(string message) => message;

    /// <summary>
    /// Returns a value on the invocation's Completion AND separately calls Clients.All —
    /// exercises the two distinct outbound paths from docs/docs/04-design.md §11 (B4).
    /// </summary>
    public async Task<string> EchoAndBroadcast(string message)
    {
        await Clients.All.SendAsync("Broadcast", message);
        return message;
    }
}
