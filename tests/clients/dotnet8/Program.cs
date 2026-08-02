using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

// See tests/Keryhe.Switchboard.CompatibilityTests/ClientProbeContract.md for the scenario every
// SDK probe implements identically (plan decision D30). This is the first implementation, and the
// one that already found a real defect (Phase 5 finding 1): the real net8.0
// Microsoft.AspNetCore.SignalR.Client's SSE parser rejects a bare "\n\n" event terminator.

var completedSteps = new List<string>();

try
{
    if (args.Length != 3)
    {
        Console.Error.WriteLine("usage: DotNet8Probe <apiBaseUrl> <transport> <protocol>");
        Console.WriteLine("RESULT FAIL step=args reason=expected-3-arguments");
        return 1;
    }

    var apiBaseUrl = args[0];
    var transport = ParseTransport(args[1]);
    var useMessagePack = ParseProtocol(args[2]);
    var suffix = Guid.NewGuid().ToString("n")[..8];
    var roomId = $"probe-room-{suffix}";

    using var http = new HttpClient { BaseAddress = new Uri(apiBaseUrl) };
    var loginResponse = await http.PostAsJsonAsync("api/auth/login", new { Username = $"probe-{suffix}" });
    loginResponse.EnsureSuccessStatusCode();
    var login = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
    var userToken = login.RootElement.GetProperty("accessToken").GetString()!;

    var builder = new HubConnectionBuilder()
        .WithUrl(new Uri(new Uri(apiBaseUrl), "/chatHub"), options =>
        {
            options.Transports = transport;
            options.AccessTokenProvider = () => Task.FromResult<string?>(userToken);
        });

    if (useMessagePack)
    {
        builder.AddMessagePackProtocol();
    }

    await using var connection = builder.Build();

    var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    connection.On<string>("Connected", id => connected.TrySetResult(id));

    // A strongly typed target, not JsonElement — the MessagePack hub protocol binds arguments to
    // a CLR type from the On<T> registration, and can't deserialize into a JSON-only type like
    // JsonElement the way the JSON protocol's binder can.
    var groupMessage = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    connection.On<ChatMessage>("ReceiveMessage", msg => groupMessage.TrySetResult(msg));

    await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
    completedSteps.Add("connect");

    await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
    completedSteps.Add("receive_push");

    await connection.InvokeAsync("JoinRoom", roomId).WaitAsync(TimeSpan.FromSeconds(10));
    completedSteps.Add("join_group");

    await connection.InvokeAsync("SendMessage", roomId, "probe-payload").WaitAsync(TimeSpan.FromSeconds(10));
    completedSteps.Add("invoke");

    var message = await groupMessage.Task.WaitAsync(TimeSpan.FromSeconds(10));
    if (message.Text != "probe-payload")
    {
        Console.WriteLine($"RESULT FAIL step=receive_group reason=unexpected-payload:{message.Text}");
        return 1;
    }

    completedSteps.Add("receive_group");

    await connection.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
    completedSteps.Add("disconnect");

    Console.WriteLine($"RESULT OK steps={string.Join(',', completedSteps)}");
    return 0;
}
catch (Exception ex)
{
    var step = completedSteps.Count == 0 ? "connect" : NextStep(completedSteps[^1]);
    Console.Error.WriteLine(ex);
    Console.WriteLine($"RESULT FAIL step={step} reason={ex.GetType().Name}:{ex.Message}");
    return 1;
}

static HttpTransportType ParseTransport(string value) => value.ToLowerInvariant() switch
{
    "websockets" => HttpTransportType.WebSockets,
    "sse" => HttpTransportType.ServerSentEvents,
    "longpolling" => HttpTransportType.LongPolling,
    _ => throw new ArgumentException($"Unknown transport '{value}'."),
};

static bool ParseProtocol(string value) => value.ToLowerInvariant() switch
{
    "json" => false,
    "messagepack" => true,
    _ => throw new ArgumentException($"Unknown protocol '{value}'."),
};

static string NextStep(string lastCompleted) => lastCompleted switch
{
    "connect" => "receive_push",
    "receive_push" => "join_group",
    "join_group" => "invoke",
    "invoke" => "receive_group",
    "receive_group" => "disconnect",
    _ => "unknown",
};

// Mirrors ChatHub.SendMessage's anonymous push shape (From, Text, SentAt) — only the field the
// scenario asserts on is required, extra JSON/MessagePack fields are ignored by the binder.
public sealed class ChatMessage
{
    public string From { get; set; } = "";
    public string Text { get; set; } = "";
}
