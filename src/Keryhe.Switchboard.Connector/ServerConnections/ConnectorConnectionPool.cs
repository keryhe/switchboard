using System.Collections.Concurrent;
using Keryhe.Switchboard.Protocol;
using Microsoft.Extensions.Logging;

namespace Keryhe.Switchboard.Connector.ServerConnections;

/// <summary>
/// Maintains <c>ServerConnectionsPerHub</c> physical connections to the service for one hub, with
/// exponential backoff reconnect (04-design.md §8 "Startup Connection Behaviour"). Outbound sends
/// pick any currently-open connection — which physical socket carries an outbound envelope doesn't
/// matter, since the service demultiplexes purely on the connectionId/hubName/groupName/userId
/// fields inside the envelope, not on which app-server socket it arrived over.
/// </summary>
public sealed class ConnectorConnectionPool(
    string hubName,
    Uri serviceUrl,
    string serverAccessToken,
    int connectionCount,
    TimeSpan reconnectDelay,
    int maxReconnectAttempts,
    Func<ServerEnvelope, CancellationToken, Task> onEnvelope,
    ILogger logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, ConnectorServerConnection> _connections = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _maintainTasks = [];
    private int _roundRobinCursor;

    public void Start()
    {
        for (var slot = 0; slot < connectionCount; slot++)
        {
            _maintainTasks.Add(MaintainConnectionAsync(slot, _cts.Token));
        }
    }

    /// <summary>Blocks until at least one connection is open, or all reconnect attempts are exhausted.</summary>
    public async Task WaitForFirstConnectionAsync(CancellationToken ct)
    {
        while (_connections.IsEmpty && !ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct);
        }
    }

    public async ValueTask SendAsync(ServerEnvelope envelope, CancellationToken ct)
    {
        var connections = _connections.Values.Where(c => c.IsOpen).ToArray();
        if (connections.Length == 0)
        {
            logger.LogWarning("No open server connections for hub '{HubName}'; dropping {EnvelopeType} envelope.", hubName, envelope.Type);
            return;
        }

        var index = Interlocked.Increment(ref _roundRobinCursor);
        var connection = connections[(int)((uint)index % connections.Length)];
        await connection.SendAsync(envelope, ct);
    }

    private async Task MaintainConnectionAsync(int slot, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            ConnectorServerConnection? connection = null;
            try
            {
                connection = await ConnectorServerConnection.ConnectAsync(serviceUrl, hubName, serverAccessToken, ct);

                // One enumerator for the connection's whole lifetime: the handshake ack must be
                // consumed as its first element (see ConnectorServerConnection.ConnectAsync).
                var envelopes = connection.ReadAllAsync(ct).GetAsyncEnumerator(ct);
                if (!await envelopes.MoveNextAsync() || envelopes.Current.Type != ServerEnvelopeType.HandshakeAck)
                {
                    throw new InvalidOperationException($"Switchboard server handshake failed for hub '{hubName}'.");
                }

                _connections[slot] = connection;
                attempt = 0;
                logger.LogInformation("Server connection {Slot} for hub '{HubName}' established.", slot, hubName);

                while (await envelopes.MoveNextAsync())
                {
                    await onEnvelope(envelopes.Current, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Server connection {Slot} for hub '{HubName}' failed.", slot, hubName);
            }
            finally
            {
                _connections.TryRemove(slot, out _);
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }
            }

            attempt++;
            if (maxReconnectAttempts > 0 && attempt >= maxReconnectAttempts)
            {
                logger.LogError("Server connection {Slot} for hub '{HubName}' exhausted {MaxAttempts} reconnect attempts; giving up.", slot, hubName, maxReconnectAttempts);
                break;
            }

            var delay = TimeSpan.FromSeconds(Math.Min(reconnectDelay.TotalSeconds * Math.Pow(2, attempt - 1), 60));
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await Task.WhenAll(_maintainTasks);
        }
        catch
        {
        }

        foreach (var connection in _connections.Values)
        {
            await connection.DisposeAsync();
        }

        _cts.Dispose();
    }
}
