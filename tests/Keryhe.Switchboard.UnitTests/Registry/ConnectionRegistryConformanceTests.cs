using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Orleans;
using Keryhe.Switchboard.Registry;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Registry;

/// <summary>
/// Implementation-agnostic conformance suite for <see cref="IConnectionRegistry"/> (plan decision
/// D20) — extracted so the Orleans implementation (Phase 3 Slice 1) runs the exact same tests as
/// <see cref="InMemoryConnectionRegistry"/> rather than drifting behind its own hand-written suite.
/// The cases worth calling out explicitly: unregister must clean up stale group/user membership
/// (a disconnected connection must never linger in a fan-out target set), and protocol can be set
/// either before or after a group join with the same end result.
/// </summary>
/// <remarks>
/// Every connectionId/hubName/groupName is namespaced with <see cref="_id"/>, a fresh value per
/// test-class instance (xunit creates one per test method). <see cref="InMemoryConnectionRegistry"/>
/// doesn't need this — a brand-new instance per test is already isolated — but the Orleans
/// implementation's grains are keyed by these bare strings in a silo shared across the whole test
/// class (spinning one up per test method would be needlessly slow), so an unnamespaced literal
/// like "conn-1" would collide across test methods. Only identifiers that become or compose a grain
/// key are namespaced; semantic values being asserted on (like a userId) are not.
/// </remarks>
public abstract class ConnectionRegistryConformanceTestsBase
{
    private readonly string _id = Guid.NewGuid().ToString("n")[..8];

    protected abstract IConnectionRegistry CreateRegistry();

    private string Conn(string suffix) => $"{_id}-{suffix}";
    private string Hub(string suffix) => $"{_id}-{suffix}";

    private static ClientConnectionState NewState(string connectionId, string hubName, string? userId = null) =>
        new()
        {
            ConnectionId = connectionId,
            ConnectionToken = Guid.NewGuid().ToString("n"),
            HubName = hubName,
            UserId = userId,
            Transport = TransportType.WebSockets,
            ServerConnectionId = "server-1",
            ConnectedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task RegisterAsync_ThenGetAsync_ReturnsTheSameConnection()
    {
        var registry = CreateRegistry();
        var connectionId = Conn("conn-1");
        await registry.RegisterAsync(NewState(connectionId, Hub("hub")), CancellationToken.None);

        var result = await registry.GetAsync(connectionId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(connectionId, result!.ConnectionId);
    }

    [Fact]
    public async Task GetAsync_UnknownConnection_ReturnsNull()
    {
        var registry = CreateRegistry();
        Assert.Null(await registry.GetAsync(Conn("missing"), CancellationToken.None));
    }

    [Fact]
    public async Task RegisterAsync_TwiceForSameConnectionId_LastWriteWins()
    {
        var registry = CreateRegistry();
        var connectionId = Conn("conn-1");
        var hub = Hub("hub");
        await registry.RegisterAsync(NewState(connectionId, hub, userId: "alice"), CancellationToken.None);
        await registry.RegisterAsync(NewState(connectionId, hub, userId: "bob"), CancellationToken.None);

        var result = await registry.GetAsync(connectionId, CancellationToken.None);
        Assert.Equal("bob", result!.UserId);
    }

    [Fact]
    public async Task SetProtocolAsync_UpdatesHubProtocol()
    {
        var registry = CreateRegistry();
        var connectionId = Conn("conn-1");
        await registry.RegisterAsync(NewState(connectionId, Hub("hub")), CancellationToken.None);

        await registry.SetProtocolAsync(connectionId, "messagepack", CancellationToken.None);

        Assert.Equal("messagepack", (await registry.GetAsync(connectionId, CancellationToken.None))!.HubProtocol);
    }

    [Fact]
    public async Task SetProtocolAsync_UnknownConnection_DoesNotThrow()
    {
        var registry = CreateRegistry();
        await registry.SetProtocolAsync(Conn("missing"), "json", CancellationToken.None);
    }

    [Fact]
    public async Task UnregisterAsync_RemovesConnection()
    {
        var registry = CreateRegistry();
        var connectionId = Conn("conn-1");
        await registry.RegisterAsync(NewState(connectionId, Hub("hub")), CancellationToken.None);

        await registry.UnregisterAsync(connectionId, CancellationToken.None);

        Assert.Null(await registry.GetAsync(connectionId, CancellationToken.None));
    }

    [Fact]
    public async Task UnregisterAsync_UnknownConnection_DoesNotThrow()
    {
        var registry = CreateRegistry();
        await registry.UnregisterAsync(Conn("missing"), CancellationToken.None);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsOnlyConnectionsForTheRequestedHub()
    {
        var registry = CreateRegistry();
        var hubA = Hub("hubA");
        var connA = Conn("conn-1");
        await registry.RegisterAsync(NewState(connA, hubA), CancellationToken.None);
        await registry.RegisterAsync(NewState(Conn("conn-2"), Hub("hubB")), CancellationToken.None);

        var results = await CollectAsync(registry.GetAllAsync(hubA, CancellationToken.None));

        Assert.Equal([connA], results.Select(r => r.ConnectionId));
    }

    [Fact]
    public async Task AddToGroupAsync_ThenGetGroupMembersAsync_ReturnsTheConnection()
    {
        var registry = CreateRegistry();
        var connectionId = Conn("conn-1");
        var hub = Hub("hub");
        await registry.RegisterAsync(NewState(connectionId, hub), CancellationToken.None);

        await registry.AddToGroupAsync(connectionId, "room-1", CancellationToken.None);

        var members = await CollectAsync(registry.GetGroupMembersAsync(hub, "room-1", CancellationToken.None));
        Assert.Equal([connectionId], members.Select(m => m.ConnectionId));
    }

    [Fact]
    public async Task RemoveFromGroupAsync_RemovesFromMembership()
    {
        var registry = CreateRegistry();
        var connectionId = Conn("conn-1");
        var hub = Hub("hub");
        await registry.RegisterAsync(NewState(connectionId, hub), CancellationToken.None);
        await registry.AddToGroupAsync(connectionId, "room-1", CancellationToken.None);

        await registry.RemoveFromGroupAsync(connectionId, "room-1", CancellationToken.None);

        Assert.Empty(await CollectAsync(registry.GetGroupMembersAsync(hub, "room-1", CancellationToken.None)));
    }

    [Fact]
    public async Task UnregisterAsync_AlsoRemovesStaleGroupMembership()
    {
        var registry = CreateRegistry();
        var connectionId = Conn("conn-1");
        var hub = Hub("hub");
        await registry.RegisterAsync(NewState(connectionId, hub), CancellationToken.None);
        await registry.AddToGroupAsync(connectionId, "room-1", CancellationToken.None);

        await registry.UnregisterAsync(connectionId, CancellationToken.None);

        // A disconnected connection must not linger in a group's member set — a later fan-out to
        // the group must not find it.
        Assert.Empty(await CollectAsync(registry.GetGroupMembersAsync(hub, "room-1", CancellationToken.None)));
    }

    [Fact]
    public async Task GetUserConnectionsAsync_ReturnsEveryConnectionForThatUser()
    {
        var registry = CreateRegistry();
        var hub = Hub("hub");
        var conn1 = Conn("conn-1");
        var conn2 = Conn("conn-2");
        await registry.RegisterAsync(NewState(conn1, hub, userId: "alice"), CancellationToken.None);
        await registry.RegisterAsync(NewState(conn2, hub, userId: "alice"), CancellationToken.None);
        await registry.RegisterAsync(NewState(Conn("conn-3"), hub, userId: "bob"), CancellationToken.None);

        var aliceConnections = (await CollectAsync(registry.GetUserConnectionsAsync(hub, "alice", CancellationToken.None)))
            .Select(c => c.ConnectionId)
            .OrderBy(id => id)
            .ToList();

        Assert.Equal(new[] { conn1, conn2 }.OrderBy(id => id), aliceConnections);
    }

    [Fact]
    public async Task UnregisterAsync_AlsoRemovesStaleUserIndexEntry()
    {
        var registry = CreateRegistry();
        var connectionId = Conn("conn-1");
        var hub = Hub("hub");
        await registry.RegisterAsync(NewState(connectionId, hub, userId: "alice"), CancellationToken.None);

        await registry.UnregisterAsync(connectionId, CancellationToken.None);

        Assert.Empty(await CollectAsync(registry.GetUserConnectionsAsync(hub, "alice", CancellationToken.None)));
    }

    [Fact]
    public async Task SetProtocolAsync_BeforeOrAfterGroupJoin_ProducesTheSameResult()
    {
        var registry = CreateRegistry();
        var connectionId = Conn("conn-1");
        var hub = Hub("hub");
        await registry.RegisterAsync(NewState(connectionId, hub), CancellationToken.None);
        await registry.SetProtocolAsync(connectionId, "messagepack", CancellationToken.None);
        await registry.AddToGroupAsync(connectionId, "room-1", CancellationToken.None);

        var members = await CollectAsync(registry.GetGroupMembersAsync(hub, "room-1", CancellationToken.None));
        Assert.Equal("messagepack", members.Single().HubProtocol);
    }

    private static async Task<List<ClientConnectionState>> CollectAsync(IAsyncEnumerable<ClientConnectionState> source)
    {
        var results = new List<ClientConnectionState>();
        await foreach (var item in source)
        {
            results.Add(item);
        }

        return results;
    }
}

public sealed class InMemoryConnectionRegistryConformanceTests : ConnectionRegistryConformanceTestsBase
{
    protected override IConnectionRegistry CreateRegistry() => new InMemoryConnectionRegistry();
}

/// <summary>Same suite, same assertions, against <see cref="OrleansConnectionRegistry"/> — the
/// substitution ADR-002 promised (plan decision D20, Phase 3 Slice 1).</summary>
[Collection(OrleansTestCollection.Name)]
public sealed class OrleansConnectionRegistryConformanceTests(OrleansTestSiloFixture fixture)
    : ConnectionRegistryConformanceTestsBase
{
    protected override IConnectionRegistry CreateRegistry() =>
        new OrleansConnectionRegistry(fixture.GrainFactory, Options.Create(new SwitchboardOptions
        {
            PublicUrl = "https://localhost",
            TokenSigningKey = "test-token-signing-key-0123456789",
            ServerSigningKey = "test-server-signing-key-0123456789",
            NodeId = "test-node",
        }));
}
