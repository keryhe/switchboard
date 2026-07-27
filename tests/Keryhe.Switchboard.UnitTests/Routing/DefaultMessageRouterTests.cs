using System.Threading.Channels;
using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Registry;
using Keryhe.Switchboard.Server.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Routing;

/// <summary>
/// D7 (Slice 3): a fan-out target whose negotiated protocol has no matching payload must be
/// skipped, never sent the wrong bytes — verified directly against the router rather than a full
/// E2E harness, since it's a pure routing decision.
/// </summary>
public class DefaultMessageRouterTests
{
    private const string HubName = "testHub";

    [Fact]
    public async Task BroadcastAsync_SkipsTarget_WhenNoPayloadMatchesItsProtocol()
    {
        var connectionRegistry = new InMemoryConnectionRegistry();
        var localTransportRegistry = new LocalTransportRegistry();
        var router = new DefaultMessageRouter(connectionRegistry, new InMemoryHubRegistry(), localTransportRegistry, NullLogger<DefaultMessageRouter>.Instance);

        var (jsonTransport, jsonState) = await RegisterConnectionAsync(connectionRegistry, localTransportRegistry, "json");
        var (mpTransport, mpState) = await RegisterConnectionAsync(connectionRegistry, localTransportRegistry, "messagepack");

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
        var connectionRegistry = new InMemoryConnectionRegistry();
        var localTransportRegistry = new LocalTransportRegistry();
        var router = new DefaultMessageRouter(connectionRegistry, new InMemoryHubRegistry(), localTransportRegistry, NullLogger<DefaultMessageRouter>.Instance);

        var (jsonTransport, _) = await RegisterConnectionAsync(connectionRegistry, localTransportRegistry, "json");
        var (mpTransport, _) = await RegisterConnectionAsync(connectionRegistry, localTransportRegistry, "messagepack");

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

    private static async Task<(FakeClientTransport Transport, ClientConnectionState State)> RegisterConnectionAsync(
        IConnectionRegistry connectionRegistry, ILocalTransportRegistry localTransportRegistry, string hubProtocol)
    {
        var connectionId = Guid.NewGuid().ToString("n");
        var transport = new FakeClientTransport(connectionId, HubName);

        var state = new ClientConnectionState
        {
            ConnectionId = connectionId,
            ConnectionToken = Guid.NewGuid().ToString("n"),
            HubName = HubName,
            Transport = TransportType.WebSockets,
            TransportHandle = transport,
            ServerConnectionId = "server-1",
            ConnectedAt = DateTimeOffset.UtcNow,
        };

        await connectionRegistry.RegisterAsync(state, CancellationToken.None);
        await connectionRegistry.SetProtocolAsync(connectionId, hubProtocol, CancellationToken.None);
        localTransportRegistry.Register(transport);

        return (transport, state);
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
}
