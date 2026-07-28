using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Orleans;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.Registry;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Registry;

/// <summary>Implementation-agnostic conformance suite for <see cref="IHubRegistry"/> (plan decision
/// D20) — the Orleans implementation (Phase 3 Slice 4) runs the same tests as
/// <see cref="InMemoryHubRegistry"/>.</summary>
/// <remarks>
/// Hub names are namespaced with <see cref="_id"/>, a fresh value per test-class instance, the same
/// way <c>ConnectionRegistryConformanceTestsBase</c> namespaces its own keys — the Orleans variant's
/// grains are keyed by these bare strings in a silo shared across the whole test class.
/// <see cref="InMemoryHubRegistry"/> doesn't need this, but sharing the base means it gets it too.
/// </remarks>
public abstract class HubRegistryConformanceTestsBase
{
    private readonly string _id = Guid.NewGuid().ToString("n")[..8];

    protected abstract IHubRegistry CreateRegistry();

    private string Hub(string suffix) => $"{_id}-{suffix}";

    private static ServerConnectionState NewServerConnection(string connectionId, string hubName, ServerConnectionStatus status = ServerConnectionStatus.Connected) =>
        new()
        {
            ConnectionId = connectionId,
            HubName = hubName,
            AppServerId = "app-1",
            Connection = new FakeServerConnection(connectionId, hubName),
            Status = status,
            ConnectedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task RegisterServerConnectionAsync_ThenGetHub_ReturnsIt()
    {
        var registry = CreateRegistry();
        var hub = Hub("hub");
        await registry.RegisterServerConnectionAsync(NewServerConnection("srv-1", hub), CancellationToken.None);

        var descriptor = registry.GetHub(hub);

        Assert.NotNull(descriptor);
        Assert.True(descriptor!.ServerConnections.ContainsKey("srv-1"));
    }

    [Fact]
    public void GetHub_UnknownHub_ReturnsNull()
    {
        var registry = CreateRegistry();
        Assert.Null(registry.GetHub(Hub("missing")));
    }

    [Fact]
    public async Task HasActiveServerConnectionAsync_FalseUntilOneIsRegistered()
    {
        var registry = CreateRegistry();
        var hub = Hub("hub");
        Assert.False(await registry.HasActiveServerConnectionAsync(hub, CancellationToken.None));

        await registry.RegisterServerConnectionAsync(NewServerConnection("srv-1", hub), CancellationToken.None);

        Assert.True(await registry.HasActiveServerConnectionAsync(hub, CancellationToken.None));
    }

    [Fact]
    public async Task HasActiveServerConnectionAsync_FalseWhenOnlyDegradedConnectionsRemain()
    {
        var registry = CreateRegistry();
        var hub = Hub("hub");
        await registry.RegisterServerConnectionAsync(NewServerConnection("srv-1", hub, ServerConnectionStatus.Degraded), CancellationToken.None);

        Assert.False(await registry.HasActiveServerConnectionAsync(hub, CancellationToken.None));
    }

    [Fact]
    public async Task UnregisterServerConnectionAsync_RemovesIt()
    {
        var registry = CreateRegistry();
        var hub = Hub("hub");
        await registry.RegisterServerConnectionAsync(NewServerConnection("srv-1", hub), CancellationToken.None);

        await registry.UnregisterServerConnectionAsync(hub, "srv-1", CancellationToken.None);

        Assert.False(await registry.HasActiveServerConnectionAsync(hub, CancellationToken.None));
        Assert.False(registry.GetHub(hub)!.ServerConnections.ContainsKey("srv-1"));
    }

    [Fact]
    public async Task UnregisterServerConnectionAsync_UnknownHub_DoesNotThrow()
    {
        var registry = CreateRegistry();
        await registry.UnregisterServerConnectionAsync(Hub("missing"), "srv-1", CancellationToken.None);
    }

    [Fact]
    public async Task GetAllHubs_EnumeratesEveryRegisteredHub()
    {
        var registry = CreateRegistry();
        var hubA = Hub("hubA");
        var hubB = Hub("hubB");
        await registry.RegisterServerConnectionAsync(NewServerConnection("srv-1", hubA), CancellationToken.None);
        await registry.RegisterServerConnectionAsync(NewServerConnection("srv-2", hubB), CancellationToken.None);

        var hubNames = registry.GetAllHubs().Select(h => h.HubName).OrderBy(name => name).ToList();

        Assert.Equal([hubA, hubB], hubNames);
    }

    private sealed class FakeServerConnection(string connectionId, string hubName) : IServerConnection
    {
        public string ConnectionId => connectionId;
        public string HubName => hubName;
        public int LogicalConnectionCount => 0;
        public ValueTask SendAsync(ServerEnvelope envelope, CancellationToken ct) => ValueTask.CompletedTask;
        public IAsyncEnumerable<ServerEnvelope> ReadAllAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}

public sealed class InMemoryHubRegistryConformanceTests : HubRegistryConformanceTestsBase
{
    protected override IHubRegistry CreateRegistry() => new InMemoryHubRegistry();
}

/// <summary>Same suite, same assertions, against <see cref="OrleansHubRegistry"/> — the substitution
/// ADR-002 promised (plan decision D20, Phase 3 Slice 4).</summary>
[Collection(OrleansTestCollection.Name)]
public sealed class OrleansHubRegistryConformanceTests(OrleansTestSiloFixture fixture)
    : HubRegistryConformanceTestsBase
{
    protected override IHubRegistry CreateRegistry() =>
        new OrleansHubRegistry(
            fixture.GrainFactory,
            Options.Create(new SwitchboardOptions
            {
                PublicUrl = "https://localhost",
                TokenSigningKey = "test-token-signing-key-0123456789",
                ServerSigningKey = "test-server-signing-key-0123456789",
                NodeId = "test-node",
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrleansHubRegistry>.Instance);
}
