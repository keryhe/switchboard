using System.Collections.Concurrent;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// Node-local registry of live <see cref="LongPollingClientTransport"/>s, keyed by
/// <c>connectionToken</c> — scanned by <see cref="LongPollingReaperService"/> to detect a client
/// that has stopped polling without ever sending DELETE. Separate from
/// <see cref="ClientConnectionManager"/>'s generic transport-by-token index because the reaper
/// needs to enumerate every long-polling connection specifically (checking
/// <see cref="LongPollingClientTransport.LastPollAt"/>), not just look one up.
/// </summary>
public sealed class LongPollingConnectionTracker
{
    private readonly ConcurrentDictionary<string, LongPollingClientTransport> _byToken = new();

    public void Register(string connectionToken, LongPollingClientTransport transport) => _byToken[connectionToken] = transport;

    public void Unregister(string connectionToken) => _byToken.TryRemove(connectionToken, out _);

    public ICollection<KeyValuePair<string, LongPollingClientTransport>> All => _byToken;
}
