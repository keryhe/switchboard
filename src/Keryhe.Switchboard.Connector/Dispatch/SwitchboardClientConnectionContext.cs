using System.IO.Pipelines;
using System.Security.Claims;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

namespace Keryhe.Switchboard.Connector.Dispatch;

/// <summary>
/// A synthetic <see cref="ConnectionContext"/> backed by a pair of <see cref="Pipe"/>s, standing
/// in for a real socket so <c>HubConnectionHandler&lt;THub&gt;</c> can be driven with no client
/// attached. See docs/docs/04-design.md §11 and the spike plan §4 (Workstream B).
///
/// _toHub:   Connector writes  -&gt; HubConnectionHandler reads (Transport.Input)
/// _fromHub: HubConnectionHandler writes -&gt; Connector reads   (Transport.Output)
/// </summary>
public sealed class SwitchboardClientConnectionContext : ConnectionContext
{
    private readonly Pipe _toHub = new();
    private readonly Pipe _fromHub = new();
    private readonly CancellationTokenSource _closedCts = new();
    private readonly List<(Action<object?> Callback, object? State)> _heartbeatCallbacks = new();
    private readonly List<(Func<object, Task> Callback, object? State)> _completedCallbacks = new();

    public SwitchboardClientConnectionContext(string connectionId, ClaimsPrincipal user)
    {
        ConnectionId = connectionId;
        Items = new ConnectionItems();
        Features = new FeatureCollection();
        Transport = new DuplexPipe(_toHub.Reader, _fromHub.Writer);
        ConnectionClosed = _closedCts.Token;

        Features.Set<IConnectionUserFeature>(new UserFeature { User = user });
        Features.Set<IConnectionIdFeature>(new IdFeature(this));
        Features.Set<IConnectionItemsFeature>(new ItemsFeature(this));
        Features.Set<IConnectionHeartbeatFeature>(new HeartbeatFeature(this));
        Features.Set<IConnectionLifetimeFeature>(new LifetimeFeature(this));
        Features.Set<IConnectionTransportFeature>(new TransportFeature(this));
        Features.Set<IConnectionCompleteFeature>(new CompleteFeature(this));
    }

    public override string ConnectionId { get; set; }
    public override IFeatureCollection Features { get; }
    public override IDictionary<object, object?> Items { get; set; }
    public override IDuplexPipe Transport { get; set; }

    /// <summary>Connector-facing write side: feed client_message bytes / the synthesized handshake in here.</summary>
    public PipeWriter ToHubWriter => _toHub.Writer;

    /// <summary>Connector-facing read side: everything HubConnectionHandler writes back out.</summary>
    public PipeReader FromHubReader => _fromHub.Reader;

    /// <summary>close_connection (clean): complete the input side so HubConnectionHandler sees EOF and runs OnDisconnectedAsync.</summary>
    public async Task CompleteInboundAsync()
    {
        await _toHub.Writer.CompleteAsync();
    }

    public void RunHeartbeat()
    {
        foreach (var (callback, state) in _heartbeatCallbacks)
        {
            callback(state);
        }
    }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        _toHub.Writer.Complete(abortReason);
        _fromHub.Reader.Complete(abortReason);
        _closedCts.Cancel();
    }

    private async Task RunCompletionCallbacksAsync()
    {
        foreach (var (callback, state) in _completedCallbacks)
        {
            await callback(state!);
        }
    }

    private sealed class UserFeature : IConnectionUserFeature
    {
        public ClaimsPrincipal? User { get; set; } = new(new ClaimsIdentity());
    }

    private sealed class IdFeature(SwitchboardClientConnectionContext owner) : IConnectionIdFeature
    {
        public string ConnectionId
        {
            get => owner.ConnectionId;
            set => owner.ConnectionId = value;
        }
    }

    private sealed class ItemsFeature(SwitchboardClientConnectionContext owner) : IConnectionItemsFeature
    {
        public IDictionary<object, object?> Items
        {
            get => owner.Items;
            set => owner.Items = value;
        }
    }

    private sealed class HeartbeatFeature(SwitchboardClientConnectionContext owner) : IConnectionHeartbeatFeature
    {
        public void OnHeartbeat(Action<object> action, object state) =>
            owner._heartbeatCallbacks.Add((s => action(s!), state));
    }

    private sealed class LifetimeFeature(SwitchboardClientConnectionContext owner) : IConnectionLifetimeFeature
    {
        public CancellationToken ConnectionClosed
        {
            get => owner.ConnectionClosed;
            set => owner.ConnectionClosed = value;
        }

        public void Abort() => owner.Abort();
    }

    private sealed class TransportFeature(SwitchboardClientConnectionContext owner) : IConnectionTransportFeature
    {
        public IDuplexPipe Transport
        {
            get => owner.Transport;
            set => owner.Transport = value;
        }
    }

    private sealed class CompleteFeature(SwitchboardClientConnectionContext owner) : IConnectionCompleteFeature
    {
        public void OnCompleted(Func<object, Task> callback, object state) =>
            owner._completedCallbacks.Add((callback, state));
    }
}
