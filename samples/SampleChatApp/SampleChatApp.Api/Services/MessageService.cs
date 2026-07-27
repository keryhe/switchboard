using Microsoft.AspNetCore.SignalR;
using SampleChatApp.Api.Hubs;

namespace SampleChatApp.Api.Services;

/// <summary>Demonstrates server-initiated push (not triggered by any client call) via <see cref="IHubContext{T}"/>.</summary>
public sealed class MessageService(IHubContext<ChatHub> hubContext)
{
    public Task BroadcastSystemMessageAsync(string roomId, string text, CancellationToken ct = default) =>
        hubContext.Clients.Group(roomId).SendAsync("SystemMessage", text, cancellationToken: ct);
}
