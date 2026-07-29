using Keryhe.Switchboard.Core;
using Microsoft.AspNetCore.Http;

namespace Keryhe.Switchboard.Management;

/// <summary>
/// <c>PUT</c>/<c>DELETE /api/v1/hubs/{hubName}/groups/{groupName}/connections/{connectionId}</c>
/// (03-protocol.md Part 3). <paramref name="hubName"/> is accepted for route-shape parity with the
/// rest of Part 3 and audit-log context, but is not otherwise consulted — group membership is keyed
/// by connectionId in <see cref="IGroupMembershipService"/>, exactly as the app-server-originated
/// <c>add_to_group</c>/<c>remove_from_group</c> envelope handling already was.
/// </summary>
public static class GroupEndpoints
{
    public static async Task<IResult> AddToGroupAsync(
        string hubName,
        string groupName,
        string connectionId,
        IGroupMembershipService groupMembership,
        CancellationToken ct)
    {
        await groupMembership.AddToGroupAsync(connectionId, groupName, ct);
        return Results.Ok();
    }

    public static async Task<IResult> RemoveFromGroupAsync(
        string hubName,
        string groupName,
        string connectionId,
        IGroupMembershipService groupMembership,
        CancellationToken ct)
    {
        await groupMembership.RemoveFromGroupAsync(connectionId, groupName, ct);
        return Results.Ok();
    }
}
