using System.Collections.Concurrent;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// Node-local registry of live <see cref="ClientConnection"/>s, indexed by both
/// <see cref="ClientConnection.ConnectionId"/> and <see cref="ClientConnection.ConnectionToken"/>
/// (plan decision D10). WebSocket doesn't need the token index today — its whole lifecycle is one
/// request — but SSE and Long Polling's separate POST (send) and GET (poll) requests carry only
/// the <c>connectionToken</c> (03-protocol.md §1.5/§1.6) and must resolve the same connection
/// <see cref="ClientConnection.ConnectionToken"/> already consumed out of the one-shot pending
/// store by the request that established it.
/// </summary>
public sealed class ClientConnectionManager
{
    private readonly ConcurrentDictionary<string, ClientConnection> _byConnectionId = new();
    private readonly ConcurrentDictionary<string, ClientConnection> _byConnectionToken = new();
    private readonly ConcurrentDictionary<string, IFramedClientTransport> _transportsByToken = new();

    public void Register(ClientConnection connection)
    {
        _byConnectionId[connection.ConnectionId] = connection;
        _byConnectionToken[connection.ConnectionToken] = connection;
    }

    public void Unregister(ClientConnection connection)
    {
        _byConnectionId.TryRemove(connection.ConnectionId, out _);
        _byConnectionToken.TryRemove(connection.ConnectionToken, out _);
    }

    public ClientConnection? GetByConnectionId(string connectionId) => _byConnectionId.GetValueOrDefault(connectionId);

    public ClientConnection? GetByConnectionToken(string connectionToken) => _byConnectionToken.GetValueOrDefault(connectionToken);

    /// <summary>
    /// SSE/Long Polling need their transport reachable by <c>connectionToken</c> for the POST
    /// (send) and DELETE (close) requests <i>before</i> the hub protocol handshake completes —
    /// the handshake request itself arrives over POST, so the transport must already be findable
    /// when it does, well before a <see cref="ClientConnection"/> (which requires a negotiated
    /// <c>hubProtocol</c>) exists to register via <see cref="Register"/>. WebSocket doesn't need
    /// this since its whole lifecycle is one request holding its own socket directly.
    /// </summary>
    public void RegisterTransport(string connectionToken, IFramedClientTransport transport) =>
        _transportsByToken[connectionToken] = transport;

    public void UnregisterTransport(string connectionToken) => _transportsByToken.TryRemove(connectionToken, out _);

    public IFramedClientTransport? GetTransportByToken(string connectionToken) => _transportsByToken.GetValueOrDefault(connectionToken);
}
