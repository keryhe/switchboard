using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Phase0.Spike.Connector.Dispatch;
using Phase0.Spike.Host.Hubs;

namespace Phase0.Spike.Tests.WorkstreamB;

/// <summary>
/// Builds a real SignalR hub pipeline (per docs/docs/04-design.md §11) in-process, with no
/// ASP.NET Core host and no HTTP involved at all -- just ServiceCollection + ConnectionBuilder.
/// The recording HubLifetimeManager stands in for the outbound-only IHubLifetimeManager the
/// Connector implements in Phase 1.
/// </summary>
public sealed class HubDispatchHarness : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly HubPipelineFactory _pipelineFactory;

    public HubDispatchHarness()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddAuthorization();
        services.Replace(ServiceDescriptor.Singleton(typeof(HubLifetimeManager<>), typeof(RecordingHubLifetimeManager<>)));

        _services = services.BuildServiceProvider();
        _pipelineFactory = new HubPipelineFactory(_services);
    }

    public RecordingHubLifetimeManager<THub> GetRecorder<THub>() where THub : Hub =>
        (RecordingHubLifetimeManager<THub>)_services.GetRequiredService<HubLifetimeManager<THub>>();

    public Microsoft.AspNetCore.Connections.ConnectionDelegate GetPipeline<THub>() where THub : Hub =>
        _pipelineFactory.GetOrCreate<THub>();

    public void Dispose() => _services.Dispose();
}
