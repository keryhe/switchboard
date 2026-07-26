using Microsoft.AspNetCore.SignalR.Client;

namespace Phase0.Spike.Tests.WorkstreamA;

/// <summary>
/// A5: an unmodified Microsoft.AspNetCore.SignalR.Client HubConnection negotiates against the
/// MapHub-mapped route, follows the redirect purely via the registered policy, and reaches the
/// stub target's WebSocket -- no SignalR fork, no reflection into framework internals.
/// </summary>
[Collection("HostProcess")]
public class DotNetClientEndToEndTests(HostProcessFixture host)
{
    [Fact]
    public async Task HubConnection_follows_the_redirect_and_connects_to_the_stub_target()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl($"{host.BaseUrl}/testHub")
            .Build();

        await connection.StartAsync();

        Assert.Equal(HubConnectionState.Connected, connection.State);

        var observed = await host.GetStubObservedAsync();
        var connectedHubs = observed.GetProperty("connectedHubs").EnumerateArray().Select(e => e.GetString()).ToArray();
        var negotiatedHubs = observed.GetProperty("negotiatedHubs").EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Contains("testHub", connectedHubs);
        Assert.Contains("testHub", negotiatedHubs);

        await connection.StopAsync();
    }
}
