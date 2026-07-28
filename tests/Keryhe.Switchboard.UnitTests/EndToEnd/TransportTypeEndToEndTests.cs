using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Core.Models;
using Keryhe.Switchboard.Protocol;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Phase 3 Slice 0, finding 11: every connection used to be recorded as
/// <see cref="TransportType.WebSockets"/> regardless of which transport it actually used, because
/// all three transports share <c>ClientConnectionLifecycle.RunAsync</c> and it hardcoded the value.
/// Harmless while nothing read <see cref="ClientConnectionState.Transport"/>; not harmless once
/// Phase 3 persists connection state into grain storage and Phase 4 emits per-transport metrics.
/// This pins the fix directly against <see cref="IConnectionRegistry"/> rather than relying on the
/// transport-specific end-to-end tests to happen to notice, since none of them ever asserted this.
/// </summary>
public class TransportTypeEndToEndTests : IAsyncLifetime
{
    private RealKestrelServerFixture _factory = null!;
    private const string HubName = "chatHub-transport-type-e2e";

    public async Task InitializeAsync()
    {
        _factory = new RealKestrelServerFixture();
        await _factory.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task SseConnection_IsRecordedAsServerSentEvents_NotWebSockets()
    {
        await AssertRecordedTransportAsync(HttpTransportType.ServerSentEvents, TransportType.ServerSentEvents);
    }

    [Fact]
    public async Task LongPollingConnection_IsRecordedAsLongPolling_NotWebSockets()
    {
        await AssertRecordedTransportAsync(HttpTransportType.LongPolling, TransportType.LongPolling);
    }

    private async Task AssertRecordedTransportAsync(HttpTransportType transport, TransportType expected)
    {
        var tokenService = _factory.Services.GetRequiredService<ITokenService>();
        var serverToken = tokenService.IssueServerToken("test-server", [HubName], TimeSpan.FromHours(1));

        await using var appServerDouble = await AppServerDouble.ConnectAsync(_factory.ServerAddress, HubName, serverToken);

        var clientToken = tokenService.IssueClientToken(Guid.NewGuid().ToString("n"), HubName, "alice", null, TimeSpan.FromMinutes(1));
        var url = new Uri(_factory.ServerAddress, $"/{HubName}");
        await using var connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(clientToken);
                options.Transports = transport;
            })
            .Build();

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var openConnection = await appServerDouble.ReceiveEnvelopeAsync(ServerEnvelopeType.OpenConnection, TimeSpan.FromSeconds(10));
        var connectionId = openConnection.ConnectionId!;

        var connectionRegistry = _factory.Services.GetRequiredService<IConnectionRegistry>();
        var state = await connectionRegistry.GetAsync(connectionId, CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(expected, state!.Transport);

        await connection.DisposeAsync();
    }
}
