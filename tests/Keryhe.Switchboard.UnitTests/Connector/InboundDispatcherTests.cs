using Keryhe.Switchboard.Connector;
using Keryhe.Switchboard.Connector.Dispatch;
using Keryhe.Switchboard.Connector.ServerConnections;
using Keryhe.Switchboard.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Connector;

public class InboundDispatcherTests
{
    public class TestHub : Hub
    {
        public Task<string> Echo(string message) => Task.FromResult($"echo:{message}");
    }

    [Fact]
    public async Task OpenConnection_ThenClientMessage_ProducesCompletion()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddSingleton<HubRouteNameRegistry>();
        services.AddSingleton<ConnectorConnectionPoolRegistry>();
        services.AddSingleton(typeof(HubLifetimeManager<>), typeof(SwitchboardHubLifetimeManager<>));
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<HubRouteNameRegistry>().Register(typeof(TestHub), "testHub");

        var pipelineFactory = new HubPipelineFactory(provider);
        var dispatcher = new InboundDispatcher(pipelineFactory, NullLogger<InboundDispatcher>.Instance);

        var sent = new List<ServerEnvelope>();
        Task SendToService(ServerEnvelope e, CancellationToken ct) { sent.Add(e); return Task.CompletedTask; }

        var connectionId = Guid.NewGuid().ToString("n");

        await dispatcher.HandleAsync(typeof(TestHub), "testHub", new ServerEnvelope
        {
            Type = ServerEnvelopeType.OpenConnection,
            ConnectionId = connectionId,
            HubProtocol = "json",
        }, SendToService, CancellationToken.None);

        var pingWriter = new System.Buffers.ArrayBufferWriter<byte>();
        JsonFrameProtocol.WriteFrame(pingWriter, "{\"type\":6}"u8.ToArray());
        await dispatcher.HandleAsync(typeof(TestHub), "testHub", new ServerEnvelope
        {
            Type = ServerEnvelopeType.ClientMessage,
            ConnectionId = connectionId,
            HubProtocol = "json",
            Payload = pingWriter.WrittenMemory.ToArray(),
        }, SendToService, CancellationToken.None);

        var frame = BuildInvocationFrame("1", "Echo", "hello");
        await dispatcher.HandleAsync(typeof(TestHub), "testHub", new ServerEnvelope
        {
            Type = ServerEnvelopeType.ClientMessage,
            ConnectionId = connectionId,
            HubProtocol = "json",
            Payload = frame,
        }, SendToService, CancellationToken.None);

        await Task.Delay(500);

        Assert.Contains(sent, e => e.Type == ServerEnvelopeType.SendToConnection);
    }

    private static byte[] BuildInvocationFrame(string invocationId, string target, params object[] args)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { type = 1, invocationId, target, arguments = args });
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        JsonFrameProtocol.WriteFrame(writer, System.Text.Encoding.UTF8.GetBytes(json));
        return writer.WrittenMemory.ToArray();
    }

    private sealed class NoOpLifetimeManager<THub> : HubLifetimeManager<THub> where THub : Hub
    {
        public override Task OnConnectedAsync(HubConnectionContext connection) => Task.CompletedTask;
        public override Task OnDisconnectedAsync(HubConnectionContext connection) => Task.CompletedTask;
        public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task SendAllExceptAsync(string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
