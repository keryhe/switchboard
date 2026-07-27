using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;

namespace Keryhe.Switchboard.Connector.Dispatch;

/// <summary>
/// Builds (and caches) the real SignalR connection pipeline for a hub type — once per hub, not
/// once per client — so it can be invoked against a synthetic <see cref="SwitchboardClientConnectionContext"/>
/// per logical client connection. See docs/docs/04-design.md §11.
/// </summary>
public sealed class HubPipelineFactory(IServiceProvider serviceProvider)
{
    private static readonly MethodInfo BuildPipelineGenericMethod =
        typeof(HubPipelineFactory).GetMethod(nameof(BuildPipeline), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly ConcurrentDictionary<Type, ConnectionDelegate> _pipelines = new();

    public ConnectionDelegate GetOrCreate<THub>() where THub : Hub =>
        _pipelines.GetOrAdd(typeof(THub), _ => BuildPipeline<THub>());

    /// <summary>
    /// Non-generic entry point for hub discovery (plan decision D2): the hosted service only has
    /// a runtime <see cref="Type"/> from <c>HubMetadata.HubType</c>, not a compile-time type
    /// parameter, so it needs one <c>MakeGenericMethod</c> call per hub at startup — this never
    /// runs on the hot path.
    /// </summary>
    public ConnectionDelegate GetOrCreate(Type hubType) =>
        _pipelines.GetOrAdd(hubType, t => (ConnectionDelegate)BuildPipelineGenericMethod.MakeGenericMethod(t).Invoke(this, null)!);

    private ConnectionDelegate BuildPipeline<THub>() where THub : Hub =>
        new ConnectionBuilder(serviceProvider)
            .UseConnectionHandler<HubConnectionHandler<THub>>()
            .Build();
}
