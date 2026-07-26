using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Phase0.Spike.Tests.WorkstreamB;

/// <summary>
/// Test-only stand-in for the real IHubLifetimeManager. Records every outbound call so B4 can
/// assert Clients.All.SendAsync(...) lands here and never touches the synthetic pipe.
/// </summary>
public sealed class RecordingHubLifetimeManager<THub> : HubLifetimeManager<THub> where THub : Hub
{
    public ConcurrentQueue<(string Method, object?[] Args)> AllSends { get; } = new();

    public override Task OnConnectedAsync(HubConnectionContext connection) => Task.CompletedTask;

    public override Task OnDisconnectedAsync(HubConnectionContext connection) => Task.CompletedTask;

    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        AllSends.Enqueue((methodName, args));
        return Task.CompletedTask;
    }

    public override Task SendAllExceptAsync(string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public override Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
