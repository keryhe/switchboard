using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace SampleChatApp.Api.Hubs;

[Authorize]
public class ChatHub : Hub
{
    public async Task SendMessage(string roomId, string text)
    {
        var sender = Context.UserIdentifier;
        await Clients.Group(roomId).SendAsync("ReceiveMessage", new
        {
            From = sender,
            Text = text,
            SentAt = DateTimeOffset.UtcNow,
        });
    }

    public async Task JoinRoom(string roomId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).SendAsync("UserJoined", Context.UserIdentifier);
    }

    public async Task LeaveRoom(string roomId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).SendAsync("UserLeft", Context.UserIdentifier);
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", Context.ConnectionId);
        await base.OnConnectedAsync();
    }
}
