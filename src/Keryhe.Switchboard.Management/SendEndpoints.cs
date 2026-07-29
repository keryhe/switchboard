using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Management.Models;
using Keryhe.Switchboard.Protocol.Framing;
using Microsoft.AspNetCore.Http;

namespace Keryhe.Switchboard.Management;

/// <summary>
/// <c>POST /api/v1/hubs/{hubName}/send</c> / <c>.../users/{userId}/send</c> /
/// <c>.../groups/{groupName}/send</c> (03-protocol.md Part 3). Each constructs the invocation via
/// <see cref="ManagementInvocationWriter"/> (Phase 4 plan decision D22) and hands it straight to
/// <see cref="IMessageRouter"/> fan-out — hub code never runs; there is no app server involved at
/// all. All three always return <c>202 Accepted</c>, including when the hub/group/user has no
/// connections: fan-out to zero targets is not an error (mirrors app-server-originated broadcasts).
/// </summary>
public static class SendEndpoints
{
    public static async Task<IResult> BroadcastAsync(
        string hubName,
        ManagementSendRequest request,
        IMessageRouter router,
        CancellationToken ct)
    {
        var payloads = ManagementInvocationWriter.WriteInvocation(request.Target, request.Arguments);
        await router.BroadcastAsync(hubName, payloads["json"], "json", payloads, excludedConnectionIds: null, ct);
        return Results.Accepted();
    }

    public static async Task<IResult> SendToUserAsync(
        string hubName,
        string userId,
        ManagementSendRequest request,
        IMessageRouter router,
        CancellationToken ct)
    {
        var payloads = ManagementInvocationWriter.WriteInvocation(request.Target, request.Arguments);
        await router.SendToUserAsync(hubName, userId, payloads["json"], "json", payloads, ct);
        return Results.Accepted();
    }

    public static async Task<IResult> SendToGroupAsync(
        string hubName,
        string groupName,
        ManagementSendRequest request,
        IMessageRouter router,
        CancellationToken ct)
    {
        var payloads = ManagementInvocationWriter.WriteInvocation(request.Target, request.Arguments);
        await router.SendToGroupAsync(hubName, groupName, payloads["json"], "json", payloads, excludedConnectionIds: null, ct);
        return Results.Accepted();
    }
}
