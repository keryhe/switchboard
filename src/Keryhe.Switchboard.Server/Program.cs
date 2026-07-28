if (args.Length > 0 && args[0] == "token")
{
    return Keryhe.Switchboard.Server.Cli.TokenCommand.Run(args);
}

var app = Keryhe.Switchboard.Server.Program.BuildApp(args);
app.Run();
return 0;

namespace Keryhe.Switchboard.Server
{
    public partial class Program
    {
        public static WebApplication BuildApp(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Verified during Phase 3 Slice 2 testing: when an Orleans silo's only cluster peer is
            // also gone (e.g. both silos torn down together, or one killed outright), graceful
            // silo shutdown can block for the full default host shutdown timeout (30s) rather than
            // detecting the peer's absence quickly. Configurable here so tests simulating a node
            // failure aren't forced to eat 30s per node just to tear themselves down; real
            // deployments can tune it too.
            var shutdownTimeoutRaw = builder.Configuration["Switchboard:ShutdownTimeout"];
            if (!string.IsNullOrWhiteSpace(shutdownTimeoutRaw) && TimeSpan.TryParse(shutdownTimeoutRaw, out var shutdownTimeout))
            {
                builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(o => o.ShutdownTimeout = shutdownTimeout);
            }

            builder.Services.AddOptions<Keryhe.Switchboard.Core.Models.SwitchboardOptions>()
                .Bind(builder.Configuration.GetSection("Switchboard"))
                .Validate(o => !string.IsNullOrWhiteSpace(o.PublicUrl), "PublicUrl is required.")
                .Validate(o => !string.IsNullOrWhiteSpace(o.TokenSigningKey), "TokenSigningKey is required.")
                .Validate(o => !string.IsNullOrWhiteSpace(o.ServerSigningKey), "ServerSigningKey is required.")
                .Validate(o => !o.EnableDirectNegotiate || o.TrustedProxyNetworks.Length > 0,
                    "TrustedProxyNetworks must be non-empty when EnableDirectNegotiate is true.")
                // Phase 3 Slice 6: a real cluster boot must pick the ADO.NET path explicitly rather
                // than silently falling back to single-node in-memory clustering — OrleansSiloPort
                // is the documented test-only escape hatch (its own remarks), never something a
                // real deployment sets.
                .Validate(o => !o.UseOrleansCluster || o.OrleansSiloPort is not null || !string.IsNullOrWhiteSpace(o.OrleansAdoNetConnectionString),
                    "OrleansAdoNetConnectionString is required when UseOrleansCluster is true (unless OrleansSiloPort is set for local/test clustering).")
                // This project bundles no default database engine (Phase 3 Slice 6) — the operator
                // must say which ADO.NET provider the connection string is for; the host resolves a
                // matching DbProviderFactory via DI (see the AddKeyedSingleton<DbProviderFactory>
                // registration below) or must have registered one with DbProviderFactories itself.
                .Validate(o => string.IsNullOrWhiteSpace(o.OrleansAdoNetConnectionString) || !string.IsNullOrWhiteSpace(o.OrleansAdoNetInvariant),
                    "OrleansAdoNetInvariant is required when OrleansAdoNetConnectionString is set.")
                .ValidateOnStart();

            builder.Services.AddSingleton<Keryhe.Switchboard.Core.ITokenService, Keryhe.Switchboard.Server.Security.JwtTokenService>();
            builder.Services.AddSingleton<Keryhe.Switchboard.Core.ILocalTransportRegistry, Keryhe.Switchboard.Registry.LocalTransportRegistry>();
            builder.Services.AddSingleton<Keryhe.Switchboard.Server.ClientConnections.ClientConnectionManager>();
            builder.Services.AddSingleton<Keryhe.Switchboard.Server.ClientConnections.LongPollingConnectionTracker>();
            builder.Services.AddHostedService<Keryhe.Switchboard.Server.ClientConnections.LongPollingReaperService>();
            builder.Services.AddSingleton(TimeProvider.System);

            // The reference ADO.NET provider this host ships configured out of the box (Phase 3
            // Slice 6) — registered unconditionally (cheap, no I/O) rather than gated on
            // UseOrleansCluster, so it's just here as a standing part of this host's composition,
            // the same as any other DI registration. Keryhe.Switchboard.Orleans knows nothing about
            // Npgsql specifically; it resolves whichever DbProviderFactory is keyed under the
            // configured OrleansAdoNetInvariant (see RegisterConfiguredAdoNetProviderFactory below).
            // Swapping to SQL Server/MySQL means adding that driver's package here and registering
            // its own keyed factory instead — see Keryhe.Switchboard.Orleans/Sql/README.md.
            builder.Services.AddKeyedSingleton<System.Data.Common.DbProviderFactory>("Npgsql", Npgsql.NpgsqlFactory.Instance);

            // Registry/pending-connection-store/backplane/hub-registry/server-connection-selector
            // substitution (plan decision D20, ADR-002/ADR-003): Orleans when clustered, in-memory/
            // no-op otherwise. IHubRegistry's node-local live-connection bookkeeping (GetHub,
            // GetAllHubs) is identical in both modes — OrleansHubRegistry wraps the same in-memory
            // storage internally and additionally informs the cluster-wide hub grain (plan decision
            // D18, Phase 3 Slice 4).
            var useOrleansCluster = builder.Configuration.GetValue<bool>("Switchboard:UseOrleansCluster");
            var adoNetInvariant = builder.Configuration["Switchboard:OrleansAdoNetInvariant"];

            if (useOrleansCluster)
            {
                var clusterId = builder.Configuration["Switchboard:OrleansClusterId"] ?? "switchboard";
                var serviceId = builder.Configuration["Switchboard:OrleansServiceId"] ?? "switchboard";
                var siloPort = builder.Configuration.GetValue<int?>("Switchboard:OrleansSiloPort");
                var gatewayPort = builder.Configuration.GetValue<int?>("Switchboard:OrleansGatewayPort");
                var adoNetConnectionString = builder.Configuration["Switchboard:OrleansAdoNetConnectionString"];

                System.Net.IPEndPoint? primarySiloEndpoint = null;
                var primarySiloEndpointRaw = builder.Configuration["Switchboard:OrleansPrimarySiloEndpoint"];
                if (!string.IsNullOrWhiteSpace(primarySiloEndpointRaw))
                {
                    var separatorIndex = primarySiloEndpointRaw.LastIndexOf(':');
                    var host = primarySiloEndpointRaw[..separatorIndex];
                    var port = int.Parse(primarySiloEndpointRaw[(separatorIndex + 1)..]);
                    primarySiloEndpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(host), port);
                }

                Keryhe.Switchboard.Orleans.SwitchboardOrleansExtensions.AddSwitchboardOrleans(
                    builder, clusterId, serviceId, siloPort, gatewayPort, primarySiloEndpoint,
                    adoNetConnectionString, adoNetInvariant);
            }
            else
            {
                builder.Services.AddSingleton<Keryhe.Switchboard.Core.IConnectionRegistry, Keryhe.Switchboard.Registry.InMemoryConnectionRegistry>();
                builder.Services.AddSingleton<Keryhe.Switchboard.Registry.IPendingConnectionStore, Keryhe.Switchboard.Registry.InMemoryPendingConnectionStore>();
                builder.Services.AddSingleton<Keryhe.Switchboard.Core.IBackplane, Keryhe.Switchboard.Registry.NoOpBackplane>();
                builder.Services.AddSingleton<Keryhe.Switchboard.Protocol.IHubRegistry, Keryhe.Switchboard.Registry.InMemoryHubRegistry>();
                builder.Services.AddSingleton<Keryhe.Switchboard.Protocol.IServerConnectionSelector, Keryhe.Switchboard.Registry.RoundRobinServerConnectionSelector>();
                builder.Services.AddSingleton<Keryhe.Switchboard.Core.ITransportOwnershipRegistry, Keryhe.Switchboard.Registry.LocalTransportOwnershipRegistry>();
                builder.Services.AddSingleton<Keryhe.Switchboard.Core.INodeAddressResolver, Keryhe.Switchboard.Registry.NullNodeAddressResolver>();
                builder.Services.AddSingleton<Keryhe.Switchboard.Core.IReadinessProbe, Keryhe.Switchboard.Registry.LocalReadinessProbe>();
            }

            // D19 (Phase 3 Slice 5) SSE/Long Polling forward hop. LongPollTimeout's default 90s
            // (plus network/queueing slack) is comfortably inside this client's own timeout — a
            // forwarded long poll that outlives it would surface as a spurious 502 rather than the
            // 204 a direct poll would return.
            builder.Services.AddHttpClient(Keryhe.Switchboard.Server.ClientConnections.ClientConnectionForwarder.HttpClientName)
                .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMinutes(5));
            builder.Services.AddSingleton<Keryhe.Switchboard.Server.ClientConnections.ClientConnectionForwarder>();

            builder.Services.AddSingleton<Keryhe.Switchboard.Core.INegotiationService, Keryhe.Switchboard.Server.Negotiate.DefaultNegotiationService>();
            builder.Services.AddSingleton<Keryhe.Switchboard.Core.IMessageRouter, Keryhe.Switchboard.Server.Routing.DefaultMessageRouter>();
            builder.Services.AddSingleton<Keryhe.Switchboard.Server.ServerConnections.IServerEnvelopeDispatcher, Keryhe.Switchboard.Server.ServerConnections.RoutingServerEnvelopeDispatcher>();
            builder.Services.AddHostedService<Keryhe.Switchboard.Server.Negotiate.PendingConnectionReaperService>();

            builder.Services.AddCors(corsOptions =>
            {
                corsOptions.AddPolicy("Switchboard", policy =>
                {
                    var allowedOrigins = builder.Configuration.GetSection("Switchboard:AllowedOrigins").Get<string[]>() ?? [];
                    policy.WithOrigins(allowedOrigins)
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
                });
            });

            var app = builder.Build();

            // Bridges the DI-registered keyed DbProviderFactory into the process-global
            // DbProviderFactories registry Orleans' AdoNet providers actually consult — has to run
            // after Build() (needs a real IServiceProvider) but before Run()/RunAsync() (before
            // Orleans' own hosted service starts touching the database). A no-op outside ADO.NET
            // clustering mode.
            if (useOrleansCluster && !string.IsNullOrWhiteSpace(adoNetInvariant))
            {
                Keryhe.Switchboard.Orleans.SwitchboardOrleansExtensions.RegisterConfiguredAdoNetProviderFactory(app.Services, adoNetInvariant);
            }

            app.UseCors("Switchboard");
            app.UseWebSockets();

            // Phase 3 Slice 7: answered from IReadinessProbe.IsReady, a synchronous field read —
            // never grain I/O inline with the request. In Orleans mode that also gates on the silo
            // being SiloStatus.Active, not just "at least one server connection" (see
            // OrleansReadinessProbe); in single-node/in-memory mode it's the same node-local check
            // this endpoint always did.
            app.MapGet("/healthz", (Keryhe.Switchboard.Core.IReadinessProbe readiness) =>
                readiness.IsReady
                    ? Results.Ok(new { status = "healthy" })
                    : Results.Json(new { status = "unhealthy" }, statusCode: StatusCodes.Status503ServiceUnavailable));

            app.MapPost("/{hub}/negotiate", Keryhe.Switchboard.Server.Negotiate.NegotiateEndpoint.HandleAsync);
            app.MapGet("/server/{hub}", Keryhe.Switchboard.Server.ServerConnections.ServerConnectionEndpoint.HandleAsync);
            app.MapGet("/{hub}", Keryhe.Switchboard.Server.ClientConnections.ClientEndpoints.HandleGetAsync);
            app.MapPost("/{hub}", Keryhe.Switchboard.Server.ClientConnections.ClientEndpoints.HandlePostAsync);
            app.MapDelete("/{hub}", Keryhe.Switchboard.Server.ClientConnections.ClientEndpoints.HandleDeleteAsync);

            return app;
        }
    }
}
