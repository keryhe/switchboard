// Extension-method namespaces only (Phase 4 Slice 4) — everything else in this file is fully
// qualified by convention; OpenTelemetry's fluent builder API and Microsoft.Extensions.DependencyInjection's
// GetRequiredService<T>() cannot be called that way.
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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
                // Phase 4 plan decision D21: fail fast rather than map an admin surface nobody can
                // reach (mapping is a no-op without a key, per MapSwitchboardManagement below) —
                // the operator turned the feature on and almost certainly meant to configure it.
                .Validate(o => !o.EnableManagementApi || !string.IsNullOrWhiteSpace(o.ManagementSigningKey),
                    "ManagementSigningKey is required when EnableManagementApi is true.")
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
            builder.Services.AddSingleton<Keryhe.Switchboard.Core.SwitchboardMetrics>();
            builder.Services.AddSingleton<Keryhe.Switchboard.Core.SwitchboardTracing>();
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
                builder.Services.AddSingleton<Keryhe.Switchboard.Core.IClusterInventory, Keryhe.Switchboard.Registry.LocalClusterInventory>();
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

            // Registered unconditionally (Phase 4 plan decision D21) — RoutingServerEnvelopeDispatcher
            // depends on IGroupMembershipService for the app-server-originated add_to_group/
            // remove_from_group envelopes regardless of whether the management API itself is mapped.
            Keryhe.Switchboard.Management.ManagementApiExtensions.AddSwitchboardManagement(builder.Services);

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

            // Read raw configuration rather than IOptions<SwitchboardOptions>.Value for the same
            // reason MapSwitchboardManagement does (see its remarks): this runs during service
            // registration, before Build(), and resolving the bound options here would trigger the
            // whole .Validate(...) chain early. Opt-in only (Phase 4 plan decision D24) — verified
            // (finding 3) that a misconfigured OTLP endpoint fails completely silently, so no
            // pipeline is constructed at all unless an endpoint is actually configured, and the
            // endpoint is logged explicitly once the host starts rather than left to guesswork.
            var otlpEndpoint = builder.Configuration["Switchboard:OtlpEndpoint"];
            var nodeIdForTelemetry = builder.Configuration["Switchboard:NodeId"] ?? Guid.NewGuid().ToString("n");
            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                builder.Services.AddOpenTelemetry()
                    .ConfigureResource(resource => resource.AddAttributes(
                        [new KeyValuePair<string, object>("node.id", nodeIdForTelemetry)]))
                    .WithMetrics(metrics => metrics
                        .AddMeter(Keryhe.Switchboard.Core.SwitchboardMetrics.MeterName)
                        .AddAspNetCoreInstrumentation()
                        .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint)))
                    // Phase 4 Slice 5 (plan decision D26): the negotiate/client-connect/(opt-in)
                    // message-route spans SwitchboardTracing records land in the same OTLP pipeline
                    // as the metrics above, so all three signals reach one backend together.
                    .WithTracing(tracing => tracing
                        .AddSource(Keryhe.Switchboard.Core.SwitchboardTracing.ActivitySourceName)
                        .AddAspNetCoreInstrumentation()
                        .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint)));

                // Structured ILogger output (connection lifecycle, routing errors, server-connection
                // health changes — all already named-property templates, never string
                // interpolation) exported alongside traces/metrics rather than as a separate signal.
                builder.Logging.AddOpenTelemetry(logging =>
                {
                    logging.IncludeFormattedMessage = true;
                    logging.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint));
                });
            }

            var app = builder.Build();

            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                app.Logger.LogInformation("OpenTelemetry metrics export configured: OtlpEndpoint={OtlpEndpoint}", otlpEndpoint);
            }

            // Node-local observable gauges (Phase 4 plan decision D24) — registered once, here,
            // rather than as SwitchboardMetrics constructor dependencies: ILocalTransportRegistry
            // lives in Core (could be injected directly) but IHubRegistry lives in Protocol, which
            // Core cannot reference, so both gauges are wired the same way for symmetry. Each
            // callback reads only this node's own in-memory state — no grain I/O, no distributed
            // lookup — the same "answer from a cached/local field, never inline cluster I/O" posture
            // /healthz has used since Phase 3.
            {
                var metricsForGauges = app.Services.GetRequiredService<Keryhe.Switchboard.Core.SwitchboardMetrics>();
                var localTransportRegistryForGauges = app.Services.GetRequiredService<Keryhe.Switchboard.Core.ILocalTransportRegistry>();
                var hubRegistryForGauges = app.Services.GetRequiredService<Keryhe.Switchboard.Protocol.IHubRegistry>();

                metricsForGauges.RegisterClientConnectionsGauge(() =>
                    localTransportRegistryForGauges.GetKnownHubNames().Select(hubName =>
                        new System.Diagnostics.Metrics.Measurement<long>(
                            localTransportRegistryForGauges.GetConnectionsForHub(hubName).Count(),
                            new KeyValuePair<string, object?>("hub", hubName))));

                metricsForGauges.RegisterServerConnectionsGauge(() =>
                    hubRegistryForGauges.GetAllHubs().Select(hub =>
                        new System.Diagnostics.Metrics.Measurement<long>(
                            hub.ServerConnectionCount,
                            new KeyValuePair<string, object?>("hub", hub.HubName))));
            }

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

            // No-op (routes not mapped) unless EnableManagementApi is on and ManagementSigningKey
            // is configured — fails closed by absence, not by 401 (Phase 4 plan decision D21).
            Keryhe.Switchboard.Management.ManagementApiExtensions.MapSwitchboardManagement(app);

            return app;
        }
    }
}
