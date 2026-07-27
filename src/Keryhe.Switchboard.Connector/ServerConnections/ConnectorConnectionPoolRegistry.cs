using System.Collections.Concurrent;
using Keryhe.Switchboard.Protocol;

namespace Keryhe.Switchboard.Connector.ServerConnections;

/// <summary>Shared lookup from hub name to its <see cref="ConnectorConnectionPool"/>, populated by the
/// hosted service at startup and consulted by <see cref="Keryhe.Switchboard.Connector.SwitchboardHubLifetimeManager{THub}"/>.</summary>
public sealed class ConnectorConnectionPoolRegistry
{
    private readonly ConcurrentDictionary<string, ConnectorConnectionPool> _pools = new();

    public void Register(string hubName, ConnectorConnectionPool pool) => _pools[hubName] = pool;

    public IEnumerable<ConnectorConnectionPool> All => _pools.Values;

    public async ValueTask SendAsync(string hubName, ServerEnvelope envelope, CancellationToken ct)
    {
        if (_pools.TryGetValue(hubName, out var pool))
        {
            await pool.SendAsync(envelope, ct);
        }
    }
}
