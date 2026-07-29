namespace Keryhe.Switchboard.Core;

/// <summary>
/// Group membership mutation, extracted from <c>RoutingServerEnvelopeDispatcher</c>'s
/// <c>AddToGroup</c>/<c>RemoveFromGroup</c> envelope handling (Phase 4 plan decision D23) so the
/// management API's group endpoints call the exact same code rather than a second copy. The
/// original inline version is where the Phase 3 Slice 7 bug lived: once server-connection
/// assignment is cluster-wide (plan decision D18), the caller naming a connectionId is not
/// guaranteed to share a node with it, and group membership is node-local state
/// (<see cref="ILocalTransportRegistry"/>, plan decision D14) — so whichever node actually holds
/// the connection must be the one whose local index gets mutated. A hand-written second
/// implementation would silently reproduce exactly that bug.
/// </summary>
public interface IGroupMembershipService
{
    Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct);

    Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct);
}

/// <summary>
/// <paramref name="nodeId"/> is <see cref="Models.SwitchboardOptions.NodeId"/> — taken as a plain
/// string rather than <c>IOptions&lt;SwitchboardOptions&gt;</c> because this type lives in
/// <c>Keryhe.Switchboard.Core</c>, which has no dependency beyond the BCL; callers resolve it from
/// options themselves (see <c>Keryhe.Switchboard.Management.ManagementApiExtensions</c> and
/// <c>Program.cs</c>'s DI wiring).
/// </summary>
public sealed class GroupMembershipService(
    IConnectionRegistry connectionRegistry,
    ILocalTransportRegistry localTransportRegistry,
    IBackplane backplane,
    string nodeId) : IGroupMembershipService
{
    private readonly string _nodeId = nodeId;

    public async Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct)
    {
        await connectionRegistry.AddToGroupAsync(connectionId, groupName, ct);
        if (localTransportRegistry.Get(connectionId) is not null)
        {
            localTransportRegistry.AddToGroup(connectionId, groupName);
        }
        else
        {
            // Not local — cluster-wide server-connection assignment (plan decision D18) means the
            // caller naming this connectionId may not share a node with it. Group membership is
            // node-local state (ILocalTransportRegistry, plan decision D14), so whichever node
            // actually has this connection must be the one whose index gets mutated.
            await backplane.PublishAddToGroupAsync(connectionId, groupName, _nodeId, ct);
        }
    }

    public async Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct)
    {
        await connectionRegistry.RemoveFromGroupAsync(connectionId, groupName, ct);
        if (localTransportRegistry.Get(connectionId) is not null)
        {
            localTransportRegistry.RemoveFromGroup(connectionId, groupName);
        }
        else
        {
            await backplane.PublishRemoveFromGroupAsync(connectionId, groupName, _nodeId, ct);
        }
    }
}
