using System.Collections.Concurrent;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;

namespace Phase0.Spike.Connector.Dispatch;

/// <summary>
/// Builds (and caches) the real SignalR connection pipeline for a hub type — once per hub, not
/// once per client — so it can be invoked against a synthetic <see cref="SwitchboardClientConnectionContext"/>
/// per logical client connection. See docs/docs/04-design.md §11.
/// </summary>
public sealed class HubPipelineFactory(IServiceProvider serviceProvider)
{
    private readonly ConcurrentDictionary<Type, ConnectionDelegate> _pipelines = new();

    public ConnectionDelegate GetOrCreate<THub>() where THub : Hub =>
        _pipelines.GetOrAdd(
            typeof(THub),
            _ => new ConnectionBuilder(serviceProvider)
                .UseConnectionHandler<HubConnectionHandler<THub>>()
                .Build());
}
