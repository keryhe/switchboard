using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Keryhe.Switchboard.Connector;
using Keryhe.Switchboard.Connector.ServerConnections;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Slice 5 gate: app server + service both running; a hub method invoked by a real client
/// executes on the app server (via the real Connector, not a hand-rolled double) and its return
/// value reaches the client.
///
/// This is the only test that covers the inbound path all the way through: the hand-rolled
/// app-server double in ClientRouterEndToEndTests inspects the client_message envelope but never
/// feeds it to a real hub pipeline, and InboundDispatcherTests builds its own already-framed
/// payload rather than taking one off the wire. A regression in the framing the service puts on
/// client_message payloads is therefore invisible to both of those — it only shows up here.
/// </summary>
public class ConnectorEndToEndTests : IAsyncLifetime
{
    private RealKestrelServerFixture _service = null!;
    private WebApplication _appServer = null!;
    private Uri _appServerAddress = null!;

    public async Task InitializeAsync()
    {
        _service = new RealKestrelServerFixture();
        await _service.StartAsync();

        var tokenService = _service.Services.GetRequiredService<Keryhe.Switchboard.Core.ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-app-server", ["testHub"], TimeSpan.FromHours(1));

        var builder = WebApplication.CreateBuilder(["--urls", "http://127.0.0.1:0"]);
        // .AddMessagePackProtocol() is a standard SignalR requirement independent of the
        // Connector — HubConnectionHandler's IHubProtocolResolver only knows protocols the app
        // server itself registered, so an app wanting MessagePack clients must opt in here same
        // as it would with AddAzureSignalR().
        builder.Services.AddSignalR().AddMessagePackProtocol();
        builder.Services.AddSwitchboardConnector(options =>
        {
            options.ServiceUrl = _service.ServerAddress.ToString();
            options.ServerAccessToken = serverToken;
            options.ServerConnectionsPerHub = 1;
        });

        _appServer = builder.Build();
        _appServer.MapHub<TestHub>("/testHub");

        await _appServer.StartAsync();
        var addresses = _appServer.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        _appServerAddress = new Uri(addresses.First());

        var poolRegistry = _appServer.Services.GetRequiredService<ConnectorConnectionPoolRegistry>();
        using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!poolRegistry.All.Any())
        {
            readyCts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20);
        }

        await poolRegistry.All.Single().WaitForFirstConnectionAsync(readyCts.Token);
    }

    public async Task DisposeAsync()
    {
        await _appServer.StopAsync();
        await _appServer.DisposeAsync();
        await _service.DisposeAsync();
    }

    [Fact]
    public async Task HubMethodInvokedByRealClient_ExecutesOnAppServer_AndReturnValueReachesClient()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/testHub"))
            .Build();

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var result = await connection.InvokeAsync<string>("Echo", "hello").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("echo:hello", result);
    }

    /// <summary>
    /// Slice 2 gate: a real .NET client with <c>.AddMessagePackProtocol()</c> completes the same
    /// round trip as the JSON test above — connect, invoke, completion — over the real Connector.
    /// Exercises the handshake-is-always-JSON-but-the-frame-is-Binary subtlety (finding 1/2) and
    /// MessagePack varint framing on both the client-facing and Connector-outbound-reader sides.
    /// </summary>
    [Fact]
    public async Task HubMethodInvokedByRealMessagePackClient_ExecutesOnAppServer_AndReturnValueReachesClient()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/testHub"))
            .AddMessagePackProtocol()
            .Build();

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var result = await connection.InvokeAsync<string>("Echo", "hello").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("echo:hello", result);
    }

    /// <summary>
    /// Slice 2 gate, full milestone shape: connect (receiving the OnConnectedAsync push via
    /// Clients.Caller), JoinRoom, and a group broadcast via Clients.Group — the two paths that go
    /// through SwitchboardHubLifetimeManager rather than the Connector's inbound-dispatch
    /// completion path, so they exercise D7's per-protocol payload selection for a homogeneous
    /// MessagePack group.
    /// </summary>
    [Fact]
    public async Task MessagePackClient_ReceivesCallerPush_AndGroupBroadcast()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/testHub"))
            .AddMessagePackProtocol()
            .Build();

        var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("Connected", id => connected.SetResult(id));

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("RoomMessage", text => received.SetResult(text));

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await connection.InvokeAsync("JoinRoom", "room1").WaitAsync(TimeSpan.FromSeconds(10));
        await connection.InvokeAsync("SendRoomMessage", "room1", "hello-mp").WaitAsync(TimeSpan.FromSeconds(10));

        var text = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("hello-mp", text);
    }

    /// <summary>
    /// Slice 3 gate (plan decision D7): one JSON client and one MessagePack client in the same
    /// group both receive a single <c>Clients.Group(...).SendAsync(...)</c> call correctly — the
    /// Connector cannot know the group's protocol mix in advance, so it must populate
    /// <see cref="ServerEnvelope.Payloads"/> with both encodings and the service must pick the one
    /// matching each member's own negotiated protocol.
    /// </summary>
    [Fact]
    public async Task MixedProtocolGroup_BothMembersReceiveCorrectlyEncodedBroadcast()
    {
        await using var jsonClient = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/testHub"))
            .Build();

        await using var messagePackClient = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/testHub"))
            .AddMessagePackProtocol()
            .Build();

        var jsonReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messagePackReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        jsonClient.On<string>("RoomMessage", text => jsonReceived.SetResult(text));
        messagePackClient.On<string>("RoomMessage", text => messagePackReceived.SetResult(text));

        await jsonClient.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await messagePackClient.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        await jsonClient.InvokeAsync("JoinRoom", "mixed-room").WaitAsync(TimeSpan.FromSeconds(10));
        await messagePackClient.InvokeAsync("JoinRoom", "mixed-room").WaitAsync(TimeSpan.FromSeconds(10));

        await jsonClient.InvokeAsync("SendRoomMessage", "mixed-room", "cross-protocol-hello").WaitAsync(TimeSpan.FromSeconds(10));

        var jsonText = await jsonReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var messagePackText = await messagePackReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("cross-protocol-hello", jsonText);
        Assert.Equal("cross-protocol-hello", messagePackText);
    }

    /// <summary>
    /// Slice 7 gate (plan decision D12): server→client streaming over JSON/WebSocket — the
    /// baseline case, no new mechanism, just proving StreamInvocation/StreamItem*/Completion ride
    /// the existing inbound-dispatch path correctly.
    /// </summary>
    [Fact]
    public async Task ServerToClientStreaming_JsonOverWebSocket_DeliversAllItems_ThenCompletes()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/testHub"))
            .Build();

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var items = new List<int>();
        await foreach (var item in connection.StreamAsync<int>("CountStream", 5).WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token))
        {
            items.Add(item);
        }

        Assert.Equal([1, 2, 3, 4, 5], items);
    }

    /// <summary>
    /// Slice 7 gate: the same server→client stream over MessagePack/Long Polling (SSE can't carry
    /// this combination at all — it's Text-only by design, per Slice 5) — the D12 finding is that
    /// re-serializing a parsed StreamItem through EmptyBinder's <c>typeof(object)</c> types is
    /// lossy for MessagePack, so this specifically exercises the fixed outbound reader (raw
    /// consumed bytes forwarded verbatim, no parse-then-reencode round trip).
    /// </summary>
    [Fact]
    public async Task ServerToClientStreaming_MessagePackOverLongPolling_DeliversAllItems_ThenCompletes()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/testHub"), options => options.Transports = HttpTransportType.LongPolling)
            .AddMessagePackProtocol()
            .Build();

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var items = new List<int>();
        await foreach (var item in connection.StreamAsync<int>("CountStream", 5).WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token))
        {
            items.Add(item);
        }

        Assert.Equal([1, 2, 3, 4, 5], items);
    }

    /// <summary>Slice 7 gate: client→server streaming (<c>streamIds</c>) over Long Polling.</summary>
    [Fact]
    public async Task ClientToServerStreaming_OverLongPolling_SumsAllItems()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/testHub"), options => options.Transports = HttpTransportType.LongPolling)
            .Build();

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var channel = Channel.CreateUnbounded<int>();
        var sumTask = connection.InvokeAsync<int>("SumStream", channel.Reader).WaitAsync(TimeSpan.FromSeconds(15));

        foreach (var i in new[] { 1, 2, 3, 4 })
        {
            await channel.Writer.WriteAsync(i);
        }

        channel.Writer.Complete();

        Assert.Equal(10, await sumTask);
    }

    /// <summary>Slice 7 gate: the <c>Send</c>/<c>SendCore</c> variant with no invocation id — a
    /// fire-and-forget client invocation that produces no Completion at all, only a side effect.</summary>
    [Fact]
    public async Task Send_WithNoInvocationId_InvokesHubMethod_WithNoCompletion()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/testHub"))
            .Build();

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var correlationId = Guid.NewGuid().ToString("n");
        var signal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        TestHub.FireAndForgetSignals[correlationId] = signal;

        await connection.SendAsync("FireAndForget", correlationId, "fire-and-forget-hello").WaitAsync(TimeSpan.FromSeconds(10));

        var text = await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("fire-and-forget-hello", text);
    }

    /// <summary>
    /// Slice 7 gate: cancelling a stream (<c>CancelInvocation</c>) stops the server from producing
    /// more items — proven by the hub method's own <c>finally</c> block signaling, not merely by
    /// the client stopping enumeration — and the client-side enumeration observes cancellation.
    /// </summary>
    [Fact]
    public async Task ServerToClientStreaming_Cancelled_StopsServerProducingItems_AndCompletes()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/testHub"))
            .Build();

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var streamKey = Guid.NewGuid().ToString("n");
        var cancelledSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TestHub.StreamCancelledSignals[streamKey] = cancelledSignal;

        using var cts = new CancellationTokenSource();
        var received = 0;

        var enumerationTask = Task.Run(async () =>
        {
            await foreach (var item in connection.StreamAsync<int>("CancellableCountStream", streamKey, cts.Token))
            {
                received++;
                if (received >= 3)
                {
                    cts.Cancel();
                }
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerationTask).WaitAsync(TimeSpan.FromSeconds(10));

        await cancelledSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(received >= 3);
    }

    /// <summary>Slice 7 gate: a stream that faults surfaces the error in the <c>Completion</c>
    /// message rather than ending the stream silently.</summary>
    [Fact]
    public async Task ServerToClientStreaming_ThatFaults_SurfacesErrorInCompletion()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_appServerAddress, "/testHub"))
            .Build();

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var items = new List<int>();
        var ex = await Assert.ThrowsAsync<HubException>(async () =>
        {
            await foreach (var item in connection.StreamAsync<int>("FaultingStream").WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token))
            {
                items.Add(item);
            }
        });

        Assert.Single(items);
        Assert.Contains("error occurred on the server while streaming", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public class TestHub : Hub
{
    public Task<string> Echo(string message) => Task.FromResult($"echo:{message}");

    public async Task JoinRoom(string roomId) => await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

    public Task SendRoomMessage(string roomId, string text) => Clients.Group(roomId).SendAsync("RoomMessage", text);

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    /// <summary>Server→client streaming (StreamInvocation → StreamItem* → Completion), the plain
    /// no-fault case (04-design.md §11, plan decision D12).</summary>
    public async IAsyncEnumerable<int> CountStream(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 1; i <= count; i++)
        {
            yield return i;
            await Task.Delay(20, cancellationToken);
        }
    }

    /// <summary>Signaled (keyed by <paramref name="streamKey"/>, resolved by the test that started
    /// the stream) once the stream's own <c>finally</c> runs — proof the server actually stopped
    /// producing rather than merely that the client stopped reading.</summary>
    public static readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> StreamCancelledSignals = new();

    public async IAsyncEnumerable<int> CancellableCountStream(string streamKey, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            for (var i = 1; ; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return i;
                await Task.Delay(20, cancellationToken);
            }
        }
        finally
        {
            if (StreamCancelledSignals.TryGetValue(streamKey, out var signal))
            {
                signal.TrySetResult(true);
            }
        }
    }

    /// <summary>A stream that faults partway through — the error must surface in the
    /// <c>Completion</c> message, not just silently end the stream.</summary>
    public async IAsyncEnumerable<int> FaultingStream()
    {
        yield return 1;
        await Task.Yield();
        throw new InvalidOperationException("stream-boom");
    }

    /// <summary>Client→server streaming (<c>streamIds</c>) — the client sends items incrementally
    /// via a <c>ChannelReader&lt;T&gt;</c>/<c>IAsyncEnumerable&lt;T&gt;</c> argument rather than in
    /// one invocation payload.</summary>
    public async Task<int> SumStream(IAsyncEnumerable<int> source)
    {
        var sum = 0;
        await foreach (var item in source)
        {
            sum += item;
        }

        return sum;
    }

    /// <summary>Signaled (keyed by a per-call correlation id) by <see cref="FireAndForget"/> — the
    /// <c>Send</c>/<c>SendCore</c> variant with no invocation id, so there's no return value or
    /// Completion to await; the test has to observe the side effect instead.</summary>
    public static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> FireAndForgetSignals = new();

    public Task FireAndForget(string correlationId, string text)
    {
        if (FireAndForgetSignals.TryGetValue(correlationId, out var signal))
        {
            signal.TrySetResult(text);
        }

        return Task.CompletedTask;
    }
}
