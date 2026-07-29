using System.Reflection;
using Keryhe.Switchboard.Orleans.Grains;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Orleans;

/// <summary>
/// Pins the <c>[Id(n)]</c> ordering on every grain-serializable type (plan decision D20) — the
/// same append-only wire contract as <c>ServerEnvelope</c>'s <c>[Key(n)]</c>, since this state is
/// persisted and read back by a possibly-different build across a rolling upgrade. Pinned via
/// reflection against the attribute values rather than against Orleans' own serialized byte
/// output: unlike <c>ServerEnvelopeSerializer</c> (code this project owns end to end), the actual
/// wire bytes are produced by Orleans' serializer, whose binary format is not a contract this
/// project controls or should couple a test to — the contract that matters here, and the one a
/// careless edit could actually break, is which field owns which <c>Id</c>. A change to any
/// assertion below means an <c>[Id(n)]</c> was reordered, reused, or removed — exactly the change
/// that must never happen without a new, additive <c>Id</c>.
/// </summary>
public class GrainStateIdOrderingTests
{
    [Fact]
    public void ConnectionRecord_IdOrdering_IsPinned() => AssertIds<ConnectionRecord>(
        (nameof(ConnectionRecord.OwnerNodeId), 0),
        (nameof(ConnectionRecord.HubName), 1),
        (nameof(ConnectionRecord.UserId), 2),
        (nameof(ConnectionRecord.ConnectionToken), 3),
        (nameof(ConnectionRecord.Transport), 4),
        (nameof(ConnectionRecord.HubProtocol), 5),
        (nameof(ConnectionRecord.ServerConnectionId), 6),
        (nameof(ConnectionRecord.ConnectedAt), 7),
        (nameof(ConnectionRecord.LastSeen), 8),
        (nameof(ConnectionRecord.Groups), 9));

    [Fact]
    public void ConnectionGrainState_IdOrdering_IsPinned() => AssertIds<ConnectionGrainState>(
        (nameof(ConnectionGrainState.Record), 0));

    [Fact]
    public void HubGrainState_IdOrdering_IsPinned() => AssertIds<HubGrainState>(
        (nameof(HubGrainState.ConnectionIds), 0));

    [Fact]
    public void GroupGrainState_IdOrdering_IsPinned() => AssertIds<GroupGrainState>(
        (nameof(GroupGrainState.ConnectionIds), 0));

    [Fact]
    public void UserGrainState_IdOrdering_IsPinned() => AssertIds<UserGrainState>(
        (nameof(UserGrainState.ConnectionIds), 0));

    [Fact]
    public void PendingConnectionRecord_IdOrdering_IsPinned() => AssertIds<PendingConnectionRecord>(
        (nameof(PendingConnectionRecord.ConnectionId), 0),
        (nameof(PendingConnectionRecord.HubName), 1),
        (nameof(PendingConnectionRecord.UserId), 2),
        (nameof(PendingConnectionRecord.Claims), 3),
        (nameof(PendingConnectionRecord.ExpiresAt), 4));

    [Fact]
    public void PendingConnectionGrainState_IdOrdering_IsPinned() => AssertIds<PendingConnectionGrainState>(
        (nameof(PendingConnectionGrainState.Record), 0));

    [Fact]
    public void NodeRegistryState_IdOrdering_IsPinned() => AssertIds<NodeRegistryState>(
        (nameof(NodeRegistryState.InternalUrlsByNodeId), 0),
        (nameof(NodeRegistryState.HubNamesByNodeId), 1));

    private static void AssertIds<T>(params (string PropertyName, int ExpectedId)[] expected)
    {
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var (propertyName, expectedId) in expected)
        {
            var property = Assert.Single(properties, p => p.Name == propertyName);
            var idAttribute = property.GetCustomAttribute<global::Orleans.IdAttribute>();
            Assert.NotNull(idAttribute);
            Assert.Equal((uint)expectedId, idAttribute!.Id);
        }

        // No property is missing an [Id(n)], and no two properties share one — either would be a
        // silent data-loss bug in a persisted grain, not just a wire-format nuisance.
        Assert.Equal(expected.Length, properties.Length);
        var ids = properties.Select(p => p.GetCustomAttribute<global::Orleans.IdAttribute>()!.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
