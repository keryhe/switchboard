using Microsoft.AspNetCore.SignalR;

namespace Phase0.Spike.Host.Hubs;

/// <summary>
/// OnConnectedAsync always throws — proves the rejection path (B5): HubConnectionHandler
/// catches the exception, writes a close frame with allowReconnect: false, and the pipeline
/// task completes rather than hanging.
/// </summary>
public class RejectingHub : Hub
{
    public override Task OnConnectedAsync() => throw new InvalidOperationException("RejectingHub always rejects.");
}
