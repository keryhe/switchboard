using Keryhe.Switchboard.Protocol;

namespace Keryhe.Switchboard.UnitTests.TestSupport;

/// <summary>
/// Waits for envelopes across several <see cref="AppServerDouble"/>s at once, by inspecting their
/// non-consuming <see cref="AppServerDouble.ReceivedEnvelopes"/> record.
/// </summary>
/// <remarks>
/// <para>
/// Multi-node tests need this because cluster-wide server-connection assignment (plan decision D18,
/// Phase 3 Slice 4) removed a guarantee they were originally written against. A client's
/// OpenConnection used to be announced on the app server physically attached to the node that
/// client connected through; it is now announced on whichever server connection the least-loaded
/// pick chose, which may be either node's. So a test can no longer name the app server it expects
/// an envelope on — it has to watch all of them and match on something that actually identifies the
/// connection, such as the user id.
/// </para>
/// <para>
/// Doing that with a consuming read on each double is not an option, in two distinct ways that both
/// bite. <see cref="AppServerDouble.ReceiveEnvelopeAsync"/> renews its timeout for every envelope it
/// skips, so a consuming wait aimed at the double that didn't get the envelope doesn't fail — it
/// blocks indefinitely. Verified: that hung the entire test host after every test had already
/// passed, with no failure reported anywhere. And racing a consuming read on both doubles and
/// abandoning the loser corrupts the abandoned one (see <see cref="AppServerDouble.ReceiveAsync"/>).
/// Polling a record that reading never mutates avoids both.
/// </para>
/// </remarks>
public static class AppServerDoubleWaits
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Waits for one OpenConnection announcing <paramref name="userId"/>.</summary>
    public static async Task<ServerEnvelope> WaitForOpenConnectionAsync(string userId, params AppServerDouble[] appServers) =>
        (await WaitForOpenConnectionsAsync(userId, 1, appServers))[0];

    /// <summary>Waits for <paramref name="expected"/> OpenConnections announcing <paramref name="userId"/>
    /// — more than one when several connections share a user, as a user-targeted send requires.</summary>
    public static Task<IReadOnlyList<ServerEnvelope>> WaitForOpenConnectionsAsync(string userId, int expected, params AppServerDouble[] appServers) =>
        WaitForEnvelopesAsync(
            envelope => envelope.Type == ServerEnvelopeType.OpenConnection && envelope.UserId == userId,
            expected,
            appServers);

    /// <summary>Waits for the CloseConnection announcing <paramref name="connectionId"/>'s teardown.</summary>
    public static async Task<ServerEnvelope> WaitForCloseConnectionAsync(string connectionId, params AppServerDouble[] appServers) =>
        (await WaitForEnvelopesAsync(
            envelope => envelope.Type == ServerEnvelopeType.CloseConnection && envelope.ConnectionId == connectionId,
            1,
            appServers))[0];

    /// <summary>Waits until at least <paramref name="expected"/> envelopes matching
    /// <paramref name="match"/> have arrived across <paramref name="appServers"/> combined.</summary>
    public static async Task<IReadOnlyList<ServerEnvelope>> WaitForEnvelopesAsync(
        Func<ServerEnvelope, bool> match,
        int expected,
        params AppServerDouble[] appServers)
    {
        using var cts = new CancellationTokenSource(DefaultTimeout);
        while (true)
        {
            var matches = appServers
                .SelectMany(appServer => appServer.ReceivedEnvelopes)
                .Where(match)
                .ToList();

            if (matches.Count >= expected)
            {
                return matches;
            }

            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(25, cts.Token);
        }
    }
}
