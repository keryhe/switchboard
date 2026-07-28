using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Protocol;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Registry;

/// <summary>
/// Weighted round-robin: picks the active connection with the lowest current logical connection
/// count. Single-node scope only (plan decision D18 is the Orleans-backed
/// <c>OrleansServerConnectionSelector</c>'s job) — every assignment this makes is local by
/// construction, but the returned reference is still <see cref="ServerConnectionRef"/>-formatted
/// with this node's own id, so <c>ClientConnectionLifecycle</c>'s local-vs-remote check works
/// identically in both modes rather than needing a single-node special case.
/// </summary>
public sealed class RoundRobinServerConnectionSelector(IHubRegistry hubRegistry, IOptions<SwitchboardOptions> options) : IServerConnectionSelector
{
    private readonly string _nodeId = options.Value.NodeId;

    /// <summary><paramref name="requestingNodeId"/> is unused here: this selector only ever runs in
    /// single-node mode, so every assignment is local by construction already (see class remarks) —
    /// it exists on the interface purely for <c>OrleansServerConnectionSelector</c>'s benefit.</summary>
    public Task<string?> AssignConnectionAsync(string hubName, string requestingNodeId, CancellationToken ct)
    {
        var descriptor = hubRegistry.GetHub(hubName);
        if (descriptor is null)
        {
            return Task.FromResult<string?>(null);
        }

        var candidates = descriptor.ServerConnections.Values
            .Where(s => s.Status == ServerConnectionStatus.Connected)
            .OrderBy(s => s.LogicalConnectionCount)
            .ToList();

        if (candidates.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }

        var chosen = candidates[0];
        chosen.IncrementLogicalCount();
        return Task.FromResult<string?>(ServerConnectionRef.Format(_nodeId, chosen.ConnectionId));
    }

    public Task ReleaseConnectionAsync(string hubName, string serverConnectionRef, CancellationToken ct)
    {
        if (!ServerConnectionRef.TryParse(serverConnectionRef, out _, out var serverConnectionId))
        {
            return Task.CompletedTask;
        }

        hubRegistry.GetHub(hubName)?.ServerConnections.GetValueOrDefault(serverConnectionId)?.DecrementLogicalCount();
        return Task.CompletedTask;
    }
}
