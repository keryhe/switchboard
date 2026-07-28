using System.Data.Common;
using System.Net;
using Keryhe.Switchboard.Orleans.Observers;
using Keryhe.Switchboard.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Keryhe.Switchboard.Orleans;

/// <summary>
/// Silo co-hosting plus the DI substitution of the Orleans-backed registry/pending-connection
/// store/backplane for the in-memory/no-op ones (plan decision D20). Called from the service host's
/// composition root when <c>SwitchboardOptions.UseOrleansCluster</c> is true — the Phase 2 wiring
/// (<see cref="InMemoryConnectionRegistry"/>, <see cref="InMemoryPendingConnectionStore"/>,
/// <see cref="NoOpBackplane"/>) is otherwise untouched.
/// </summary>
public static class SwitchboardOrleansExtensions
{
    /// <summary>Shared across every grain's <c>[PersistentState]</c> attribute — one storage
    /// provider for the whole silo, matching the single-database deployment ADR-002 describes.</summary>
    public const string StorageProviderName = "switchboard";

    /// <summary>
    /// Silo co-hosting. Two clustering/storage providers, chosen by whether
    /// <paramref name="adoNetConnectionString"/> is set (plan decision D20, Phase 3 Slice 6):
    /// <list type="bullet">
    /// <item>Set — ADO.NET clustering + <c>AddAdoNetGrainStorage</c>, for a real multi-node
    /// deployment sharing one database (ADR-002). The schema is vendored, not created by this
    /// service — see <c>Sql/README.md</c>. This project has no opinion on which database engine —
    /// <paramref name="adoNetInvariant"/> is required, and resolving an actual
    /// <see cref="DbProviderFactory"/> for it is the host application's job (see
    /// <see cref="RegisterConfiguredAdoNetProviderFactory"/>), not this method's.</item>
    /// <item>Unset — in-memory clustering + <c>AddMemoryGrainStorage</c> (<c>AddMemoryGrainStorage</c>
    /// ships in <c>Microsoft.Orleans.Server</c> itself, no separate package needed — finding 7).
    /// Dev/single-node and this project's own two-silos-in-one-process tests only; see
    /// <paramref name="siloPort"/>.</item>
    /// </list>
    /// <see cref="Program.BuildApp"/>'s own <c>ValidateOnStart</c> refuses to boot with
    /// <c>UseOrleansCluster = true</c>, no connection string, and no <paramref name="siloPort"/> —
    /// a real deployment must pick the ADO.NET path explicitly rather than silently falling back to
    /// single-node in-memory clustering. It also requires <paramref name="adoNetInvariant"/>
    /// whenever <paramref name="adoNetConnectionString"/> is set — there is no default engine.
    /// </summary>
    /// <param name="siloPort">Test-only — see <c>SwitchboardOptions.OrleansSiloPort</c>'s remarks.
    /// Null (the real-deployment default) uses <c>UseLocalhostClustering()</c>'s own default ports,
    /// which is exactly right for a single dev/single-node silo and never right for two silos in
    /// one process.</param>
    /// <param name="adoNetConnectionString">Set alongside <paramref name="adoNetInvariant"/> to
    /// select the ADO.NET path.</param>
    /// <param name="adoNetInvariant">The ADO.NET provider invariant name (e.g. <c>"Npgsql"</c>,
    /// <c>"Microsoft.Data.SqlClient"</c>). This project bundles no driver and picks no default — the
    /// host application supplies a matching <see cref="DbProviderFactory"/> via DI (a keyed
    /// singleton, resolved by <see cref="RegisterConfiguredAdoNetProviderFactory"/>) or registers one
    /// with <see cref="DbProviderFactories"/> itself. See <c>Sql/README.md</c>.</param>
    public static void AddSwitchboardOrleans(
        this IHostApplicationBuilder builder,
        string clusterId,
        string serviceId,
        int? siloPort = null,
        int? gatewayPort = null,
        IPEndPoint? primarySiloEndpoint = null,
        string? adoNetConnectionString = null,
        string? adoNetInvariant = null)
    {
        var useAdoNet = !string.IsNullOrWhiteSpace(adoNetConnectionString);

        builder.UseOrleans(silo =>
        {
            silo.Configure<ClusterOptions>(options =>
            {
                options.ClusterId = clusterId;
                options.ServiceId = serviceId;
            });

            if (useAdoNet)
            {
                // Real multi-node deployments discover each other purely through the membership
                // table, so no port wiring is needed between them — but two silos sharing one
                // machine (this project's own two-silos-in-one-process tests) still collide on
                // Orleans' default ports without it, exactly like the in-memory path below.
                if (siloPort is not null && gatewayPort is not null)
                {
                    silo.ConfigureEndpoints(siloPort.Value, gatewayPort.Value);
                }

                silo.UseAdoNetClustering(options =>
                {
                    options.Invariant = adoNetInvariant;
                    options.ConnectionString = adoNetConnectionString;
                });
                silo.AddAdoNetGrainStorage(StorageProviderName, options =>
                {
                    options.Invariant = adoNetInvariant;
                    options.ConnectionString = adoNetConnectionString;
                });
                return;
            }

            if (siloPort is not null && gatewayPort is not null)
            {
                silo.UseLocalhostClustering(siloPort.Value, gatewayPort.Value, primarySiloEndpoint);
            }
            else
            {
                silo.UseLocalhostClustering();
            }

            silo.AddMemoryGrainStorage(StorageProviderName);
        });

        builder.Services.AddSingleton<Core.IConnectionRegistry, OrleansConnectionRegistry>();
        builder.Services.AddSingleton<IPendingConnectionStore, OrleansPendingConnectionStore>();
        builder.Services.AddSingleton<Core.IBackplane, OrleansObserverBackplane>();
        builder.Services.AddSingleton<Protocol.IHubRegistry, OrleansHubRegistry>();
        builder.Services.AddSingleton<Protocol.IServerConnectionSelector, OrleansServerConnectionSelector>();
        builder.Services.AddSingleton<Core.ITransportOwnershipRegistry, OrleansTransportOwnershipRegistry>();
        builder.Services.AddSingleton<Core.INodeAddressResolver, OrleansNodeAddressResolver>();
        builder.Services.AddHostedService<ObserverHeartbeatService>();
        builder.Services.AddHostedService<NodeRegistryPublisherService>();

        // Registered last so it is the first hosted service stopped (the generic host stops
        // IHostedServices in reverse start order) — /healthz starts refusing traffic before the
        // observer-unsubscribe/node-deregister work below it runs, giving a load balancer the
        // earliest possible signal to stop routing here (Phase 3 Slice 7).
        builder.Services.AddSingleton<OrleansReadinessProbe>();
        builder.Services.AddSingleton<Core.IReadinessProbe>(sp => sp.GetRequiredService<OrleansReadinessProbe>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<OrleansReadinessProbe>());
    }

    /// <summary>
    /// .NET does not auto-register ADO.NET provider factories the way classic .NET Framework's
    /// <c>machine.config</c> did — <c>UseAdoNetClustering</c>/<c>AddAdoNetGrainStorage</c> resolve a
    /// provider by <c>Invariant</c> through the process-global <see cref="DbProviderFactories"/>
    /// registry, so something has to register one before the silo starts (Phase 3 Slice 6,
    /// <c>Sql/README.md</c>). This project bundles no driver and knows no default engine — the host
    /// application registers a keyed <see cref="DbProviderFactory"/> singleton for
    /// <paramref name="invariant"/> (e.g. <c>services.AddKeyedSingleton&lt;DbProviderFactory&gt;("Npgsql",
    /// NpgsqlFactory.Instance)</c>), and this bridges that DI registration into
    /// <see cref="DbProviderFactories"/>.
    ///
    /// Must run after the host's <see cref="IServiceProvider"/> exists (<c>builder.Build()</c>) but
    /// before the silo actually starts (<c>app.Run()</c>/<c>RunAsync()</c>) — Orleans doesn't touch
    /// the database until its own hosted service starts, which is after both of those, so there is a
    /// safe window between them. <see cref="Program.BuildApp"/> is the one caller.
    ///
    /// A no-op, not an error, when nothing is registered for <paramref name="invariant"/> under that
    /// key — the host may have registered the provider some other way (a direct
    /// <see cref="DbProviderFactories.RegisterFactory(string, DbProviderFactory)"/> call, or an
    /// ambient provider the runtime already knows about). Orleans' own ADO.NET providers surface a
    /// clear error at connection time if nothing was actually registered for the invariant.
    /// </summary>
    public static void RegisterConfiguredAdoNetProviderFactory(IServiceProvider services, string invariant)
    {
        if (DbProviderFactories.TryGetFactory(invariant, out _))
        {
            return;
        }

        var factory = services.GetKeyedService<DbProviderFactory>(invariant);
        if (factory is not null)
        {
            DbProviderFactories.RegisterFactory(invariant, factory);
        }
    }
}
