using Keryhe.Switchboard.Connector.Dispatch;
using Keryhe.Switchboard.Connector.ServerConnections;
using Keryhe.Switchboard.Protocol;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Connector;

/// <summary>
/// Discovers every hub mapped with <c>MapHub&lt;T&gt;()</c> (plan decision D2) and opens
/// <c>ServerConnectionsPerHub</c> connections to the Switchboard service for each, once
/// <c>EndpointDataSource</c> is populated (which only happens once routing has finished
/// building — not at DI-registration time, hence a hosted service rather than doing this in
/// <c>AddSwitchboardConnector()</c>).
/// </summary>
public sealed class ConnectorHostedService(
    EndpointDataSource endpointDataSource,
    HubRouteNameRegistry hubRouteNameRegistry,
    ConnectorConnectionPoolRegistry poolRegistry,
    InboundDispatcher inboundDispatcher,
    IOptions<SwitchboardConnectorOptions> options,
    ILoggerFactory loggerFactory,
    IHostApplicationLifetime applicationLifetime) : IHostedService
{
    private readonly List<ConnectorConnectionPool> _pools = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // EndpointDataSource is not guaranteed populated yet when IHostedService.StartAsync runs —
        // empirically (verified against .NET 10) the composite data source that MapHub<T>()
        // endpoints land in is only finalized once the framework's own request-pipeline hosted
        // service has also run, and hosted service start order is registration order, not
        // dependency order. ApplicationStarted fires after every IHostedService.StartAsync has
        // completed, which is the first point discovery is guaranteed to see every mapped hub.
        applicationLifetime.ApplicationStarted.Register(() => _ = Task.Run(DiscoverAndConnect));
        return Task.CompletedTask;
    }

    private void DiscoverAndConnect()
    {
        var serviceUrl = new Uri(options.Value.ServiceUrl);

        foreach (var (hubType, hubName) in DiscoverHubs())
        {
            hubRouteNameRegistry.Register(hubType, hubName);

            ConnectorConnectionPool pool = null!;
            pool = new ConnectorConnectionPool(
                hubName,
                serviceUrl,
                options.Value.ServerAccessToken,
                options.Value.ServerConnectionsPerHub,
                options.Value.ReconnectDelay,
                options.Value.MaxReconnectAttempts,
                onEnvelope: (envelope, ct) => inboundDispatcher.HandleAsync(hubType, hubName, envelope, (e, c) => pool.SendAsync(e, c).AsTask(), ct),
                loggerFactory.CreateLogger($"Keryhe.Switchboard.Connector.ServerConnections.{hubName}"));

            poolRegistry.Register(hubName, pool);
            _pools.Add(pool);
            pool.Start();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var pool in _pools)
        {
            await pool.DisposeAsync();
        }
    }

    private IEnumerable<(Type HubType, string HubName)> DiscoverHubs()
    {
        foreach (var endpoint in endpointDataSource.Endpoints)
        {
            if (endpoint is not RouteEndpoint routeEndpoint)
            {
                continue;
            }

            // The negotiate endpoint carries the same HubMetadata as the main hub endpoint;
            // skip it so each hub is only discovered once, keyed by its non-negotiate route.
            if (routeEndpoint.Metadata.GetMetadata<NegotiateMetadata>() is not null)
            {
                continue;
            }

            var hubMetadata = routeEndpoint.Metadata.GetMetadata<HubMetadata>();
            if (hubMetadata is null)
            {
                continue;
            }

            var hubName = routeEndpoint.RoutePattern.RawText?.TrimStart('/') ?? string.Empty;
            yield return (hubMetadata.HubType, hubName);
        }
    }
}
