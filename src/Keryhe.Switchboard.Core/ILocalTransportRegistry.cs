namespace Keryhe.Switchboard.Core;

/// <summary>
/// Node-local map of connectionId -> IClientTransport. Never replicated to Orleans grain state (Phase 3) —
/// transport handles are only meaningful on the node that physically owns the socket.
/// </summary>
public interface ILocalTransportRegistry
{
    void Register(IClientTransport transport);
    void Unregister(string connectionId);
    IClientTransport? Get(string connectionId);
}
