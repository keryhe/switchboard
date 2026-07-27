using System.Collections.Concurrent;
using Keryhe.Switchboard.Core;

namespace Keryhe.Switchboard.Registry;

public sealed class LocalTransportRegistry : ILocalTransportRegistry
{
    private readonly ConcurrentDictionary<string, IClientTransport> _transports = new();

    public void Register(IClientTransport transport) => _transports[transport.ConnectionId] = transport;

    public void Unregister(string connectionId) => _transports.TryRemove(connectionId, out _);

    public IClientTransport? Get(string connectionId) => _transports.GetValueOrDefault(connectionId);
}
