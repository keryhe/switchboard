using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SampleChatApp.Api.Services;

namespace SampleChatApp.Api.Controllers;

/// <summary>Demonstrates the <c>SystemMessage</c> push (docs/docs/08-sample-app.md's "Server push
/// (no client trigger)" row) — a plain HTTP endpoint, not a hub method, driving <see cref="MessageService"/>
/// via <c>IHubContext</c> exactly like a background job or webhook handler would.</summary>
[ApiController]
[Authorize]
[Route("api/rooms")]
public class RoomsController(MessageService messageService) : ControllerBase
{
    public sealed record SystemMessageRequest(string Text);

    [HttpPost("{roomId}/system-message")]
    public async Task<IActionResult> PostSystemMessage(string roomId, [FromBody] SystemMessageRequest request, CancellationToken ct)
    {
        await messageService.BroadcastSystemMessageAsync(roomId, request.Text, ct);
        return Ok();
    }
}
