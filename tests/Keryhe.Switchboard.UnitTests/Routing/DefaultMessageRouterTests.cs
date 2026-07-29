using System.Threading.Channels;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Registry;
using Keryhe.Switchboard.Server.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Routing;

/// <summary>
/// D7 (Phase 2 Slice 3): a fan-out target whose negotiated protocol has no matching payload must be
/// skipped, never sent the wrong bytes. D14 (Phase 3 Slice 0): fan-out delivers to this node's own
/// targets from <see cref="ILocalTransportRegistry"/> — never by enumerating
/// <see cref="IConnectionRegistry"/> — and then publishes exactly once to <see cref="IBackplane"/>
/// so every other node can do the same for its own targets.
/// </summary>
public class DefaultMessageRouterTests
{
    private const string HubName = "testHub";

    [Fact]
    public async Task BroadcastAsync_SkipsTarget_WhenNoPayloadMatchesItsProtocol()
    {
        var (router, localTransportRegistry, _) = BuildRouter();

        var jsonTransport = RegisterConnection(localTransportRegistry, "json");
        var mpTransport = RegisterConnection(localTransportRegistry, "messagepack");

        // Only a json payload is supplied — messagepack has neither a Payloads entry nor a
        // matching fallback (hubProtocol == "json" too), so the mp-negotiated target must be
        // skipped entirely rather than receiving json bytes.
        await router.BroadcastAsync(HubName, new byte[] { 1, 2, 3 }, "json", payloadsByProtocol: null, excludedConnectionIds: null, CancellationToken.None);

        Assert.True(jsonTransport.Output.Reader.TryRead(out var jsonBytes));
        Assert.Equal(new byte[] { 1, 2, 3 }, jsonBytes.ToArray());

        Assert.False(mpTransport.Output.Reader.TryRead(out _));
    }

    [Fact]
    public async Task BroadcastAsync_SelectsMatchingPayload_PerTargetProtocol()
    {
        var (router, localTransportRegistry, _) = BuildRouter();

        var jsonTransport = RegisterConnection(localTransportRegistry, "json");
        var mpTransport = RegisterConnection(localTransportRegistry, "messagepack");

        var payloads = new Dictionary<string, byte[]>
        {
            ["json"] = new byte[] { 1 },
            ["messagepack"] = new byte[] { 2 },
        };

        await router.BroadcastAsync(HubName, payloads["json"], "json", payloads, excludedConnectionIds: null, CancellationToken.None);

        Assert.True(jsonTransport.Output.Reader.TryRead(out var jsonBytes));
        Assert.Equal(new byte[] { 1 }, jsonBytes.ToArray());

        Assert.True(mpTransport.Output.Reader.TryRead(out var mpBytes));
        Assert.Equal(new byte[] { 2 }, mpBytes.ToArray());
    }

    [Fact]
    public async Task BroadcastAsync_DeliversLocally_ThenPublishesToBackplaneExactlyOnce_WithThisNodeId()
    {
        var (router, localTransportRegistry, backplane) = BuildRouter(nodeId: "node-a");
        var transport = RegisterConnection(localTransportRegistry, "json");

        await router.BroadcastAsync(HubName, new byte[] { 9 }, "json", payloadsByProtocol: null, excludedConnectionIds: null, CancellationToken.None);

        // Local delivery happened before the publish — plan decision D14: this node's own clients
        // are handled from the local index, the backplane reaches every other node's.
        Assert.True(transport.Output.Reader.TryRead(out _));

        var call = Assert.Single(backplane.Broadcasts);
        Assert.Equal(HubName, call.HubName);
        Assert.Equal(new byte[] { 9 }, call.Payload);
        Assert.Equal("node-a", call.OriginNodeId);
    }

    [Fact]
    public async Task SendToGroupAsync_DeliversLocally_ThenPublishesToBackplaneExactlyOnce()
    {
        var (router, localTransportRegistry, backplane) = BuildRouter(nodeId: "node-a");
        var transport = RegisterConnection(localTransportRegistry, "json");
        localTransportRegistry.AddToGroup(transport.ConnectionId, "room-1");

        await router.SendToGroupAsync(HubName, "room-1", new byte[] { 1 }, "json", payloadsByProtocol: null, excludedConnectionIds: null, CancellationToken.None);

        Assert.True(transport.Output.Reader.TryRead(out _));
        var call = Assert.Single(backplane.GroupMessages);
        Assert.Equal("room-1", call.GroupName);
        Assert.Equal("node-a", call.OriginNodeId);
    }

    [Fact]
    public async Task SendToUserAsync_DeliversLocally_ThenPublishesToBackplaneExactlyOnce()
    {
        var (router, localTransportRegistry, backplane) = BuildRouter(nodeId: "node-a");
        var transport = new FakeClientTransport(Guid.NewGuid().ToString("n"), HubName);
        localTransportRegistry.Register(transport, HubName, "alice");
        localTransportRegistry.SetHubProtocol(transport.ConnectionId, "json");

        await router.SendToUserAsync(HubName, "alice", new byte[] { 1 }, "json", payloadsByProtocol: null, CancellationToken.None);

        Assert.True(transport.Output.Reader.TryRead(out _));
        var call = Assert.Single(backplane.UserMessages);
        Assert.Equal("alice", call.UserId);
        Assert.Equal("node-a", call.OriginNodeId);
    }

    [Fact]
    public async Task RouteToConnectionAsync_LocalHit_WritesDirectly_AndNeverPublishes()
    {
        var (router, localTransportRegistry, backplane) = BuildRouter();
        var transport = RegisterConnection(localTransportRegistry, "json");

        await router.RouteToConnectionAsync(transport.ConnectionId, new byte[] { 5 }, "json", CancellationToken.None);

        Assert.True(transport.Output.Reader.TryRead(out var bytes));
        Assert.Equal(new byte[] { 5 }, bytes.ToArray());
        Assert.Empty(backplane.TargetedMessages);
    }

    [Fact]
    public async Task RouteToConnectionAsync_LocalMiss_PublishesToBackplane_InsteadOfSilentlyDropping()
    {
        var (router, _, backplane) = BuildRouter(nodeId: "node-a");

        await router.RouteToConnectionAsync("connection-on-another-node", new byte[] { 5 }, "json", CancellationToken.None);

        var call = Assert.Single(backplane.TargetedMessages);
        Assert.Equal("connection-on-another-node", call.ConnectionId);
        Assert.Equal(new byte[] { 5 }, call.Payload);
        Assert.Equal("node-a", call.OriginNodeId);
    }

    private static (DefaultMessageRouter Router, ILocalTransportRegistry LocalTransportRegistry, RecordingBackplane Backplane) BuildRouter(string nodeId = "node-under-test")
    {
        var connectionRegistry = new InMemoryConnectionRegistry();
        var localTransportRegistry = new LocalTransportRegistry();
        var backplane = new RecordingBackplane();
        var options = Options.Create(new SwitchboardOptions
        {
            PublicUrl = "https://localhost",
            TokenSigningKey = "test-token-signing-key-0123456789",
            ServerSigningKey = "test-server-signing-key-0123456789",
            NodeId = nodeId,
        });

        var router = new DefaultMessageRouter(
            connectionRegistry, new InMemoryHubRegistry(), localTransportRegistry, backplane, options,
            new SwitchboardMetrics(), new SwitchboardTracing(), NullLogger<DefaultMessageRouter>.Instance);

        return (router, localTransportRegistry, backplane);
    }

    private static FakeClientTransport RegisterConnection(ILocalTransportRegistry localTransportRegistry, string hubProtocol)
    {
        var connectionId = Guid.NewGuid().ToString("n");
        var transport = new FakeClientTransport(connectionId, HubName);

        localTransportRegistry.Register(transport, HubName, userId: null);
        localTransportRegistry.SetHubProtocol(connectionId, hubProtocol);

        return transport;
    }

    private sealed class FakeClientTransport(string connectionId, string hubName) : IClientTransport
    {
        public string ConnectionId { get; } = connectionId;
        public string HubName { get; } = hubName;
        public string? UserId => null;
        public Channel<ReadOnlyMemory<byte>> Output { get; } = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken ct) => throw new NotSupportedException();

        public ValueTask CloseAsync(string? error = null) => ValueTask.CompletedTask;
    }

    /// <summary>Records every publish call rather than asserting on a live backplane implementation
    /// — Slice 0 has none yet (Slice 2 adds the Orleans one); this is what proves the router's own
    /// "local first, then publish once" contract independent of any real backplane.</summary>
    private sealed class RecordingBackplane : IBackplane
    {
        public List<(string HubName, byte[] Payload, string HubProtocol, IReadOnlyDictionary<string, byte[]>? PayloadsByProtocol, string[] ExcludedConnectionIds, string OriginNodeId)> Broadcasts { get; } = [];
        public List<(string HubName, string GroupName, byte[] Payload, string HubProtocol, IReadOnlyDictionary<string, byte[]>? PayloadsByProtocol, string[] ExcludedConnectionIds, string OriginNodeId)> GroupMessages { get; } = [];
        public List<(string HubName, string UserId, byte[] Payload, string HubProtocol, IReadOnlyDictionary<string, byte[]>? PayloadsByProtocol, string OriginNodeId)> UserMessages { get; } = [];
        public List<(string ConnectionId, byte[] Payload, string HubProtocol, string OriginNodeId)> TargetedMessages { get; } = [];

        public Task PublishBroadcastAsync(string hubName, byte[] payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds, string originNodeId, CancellationToken ct)
        {
            Broadcasts.Add((hubName, payload, hubProtocol, payloadsByProtocol, excludedConnectionIds, originNodeId));
            return Task.CompletedTask;
        }

        public Task PublishGroupMessageAsync(string hubName, string groupName, byte[] payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, string[] excludedConnectionIds, string originNodeId, CancellationToken ct)
        {
            GroupMessages.Add((hubName, groupName, payload, hubProtocol, payloadsByProtocol, excludedConnectionIds, originNodeId));
            return Task.CompletedTask;
        }

        public Task PublishUserMessageAsync(string hubName, string userId, byte[] payload, string hubProtocol, IReadOnlyDictionary<string, byte[]>? payloadsByProtocol, string originNodeId, CancellationToken ct)
        {
            UserMessages.Add((hubName, userId, payload, hubProtocol, payloadsByProtocol, originNodeId));
            return Task.CompletedTask;
        }

        public Task PublishToConnectionAsync(string connectionId, byte[] payload, string hubProtocol, string originNodeId, CancellationToken ct)
        {
            TargetedMessages.Add((connectionId, payload, hubProtocol, originNodeId));
            return Task.CompletedTask;
        }

        public Task PublishServerEnvelopeAsync(string hubName, string serverConnectionRef, byte[] serializedEnvelope, CancellationToken ct) => Task.CompletedTask;

        public Task PublishCloseConnectionAsync(string connectionId, string? error, bool allowReconnect, string originNodeId, CancellationToken ct) => Task.CompletedTask;

        public Task PublishAddToGroupAsync(string connectionId, string groupName, string originNodeId, CancellationToken ct) => Task.CompletedTask;

        public Task PublishRemoveFromGroupAsync(string connectionId, string groupName, string originNodeId, CancellationToken ct) => Task.CompletedTask;
    }
}
