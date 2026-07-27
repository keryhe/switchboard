using System.Net.Http.Headers;
using Keryhe.Switchboard.Connector.Dispatch;
using Keryhe.Switchboard.Connector.Negotiate;
using Keryhe.Switchboard.Connector.ServerConnections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Keryhe.Switchboard.Connector;

public static class SwitchboardConnectorExtensions
{
    /// <summary>
    /// Wires the app server up to a Switchboard proxy service in place of <c>AddAzureSignalR()</c>:
    /// negotiate is diverted to a redirect (04-design.md §8) and every mapped hub gets a pool of
    /// physical connections driving the real hub pipeline over synthetic client connections
    /// (04-design.md §11). Requires no changes to hub code — the app author writes
    /// <c>MapHub&lt;ChatHub&gt;("/chatHub")</c> and nothing else.
    /// </summary>
    public static IServiceCollection AddSwitchboardConnector(this IServiceCollection services, Action<SwitchboardConnectorOptions> configure)
    {
        services.Configure(configure);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, SwitchboardNegotiateMatcherPolicy>());
        services.AddSingleton<INegotiateRedirectHandler, HttpNegotiateRedirectHandler>();

        services.AddHttpClient("switchboard-negotiate", (provider, client) =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SwitchboardConnectorOptions>>().Value;
            client.BaseAddress = new Uri(options.ServiceUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ServerAccessToken);
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        services.AddSingleton<HubRouteNameRegistry>();
        services.AddSingleton<ConnectorConnectionPoolRegistry>();
        services.AddSingleton<HubPipelineFactory>();
        services.AddSingleton<InboundDispatcher>();
        services.AddSingleton(typeof(HubLifetimeManager<>), typeof(SwitchboardHubLifetimeManager<>));
        services.AddHostedService<ConnectorHostedService>();

        return services;
    }
}
