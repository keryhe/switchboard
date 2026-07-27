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

            builder.Services.AddOptions<Keryhe.Switchboard.Core.Models.SwitchboardOptions>()
                .Bind(builder.Configuration.GetSection("Switchboard"))
                .Validate(o => !string.IsNullOrWhiteSpace(o.PublicUrl), "PublicUrl is required.")
                .Validate(o => !string.IsNullOrWhiteSpace(o.TokenSigningKey), "TokenSigningKey is required.")
                .Validate(o => !string.IsNullOrWhiteSpace(o.ServerSigningKey), "ServerSigningKey is required.")
                .Validate(o => !o.EnableDirectNegotiate || o.TrustedProxyNetworks.Length > 0,
                    "TrustedProxyNetworks must be non-empty when EnableDirectNegotiate is true.")
                .ValidateOnStart();

            builder.Services.AddSingleton<Keryhe.Switchboard.Core.ITokenService, Keryhe.Switchboard.Server.Security.JwtTokenService>();
            builder.Services.AddSingleton<Keryhe.Switchboard.Core.IConnectionRegistry, Keryhe.Switchboard.Registry.InMemoryConnectionRegistry>();
            builder.Services.AddSingleton<Keryhe.Switchboard.Protocol.IHubRegistry, Keryhe.Switchboard.Registry.InMemoryHubRegistry>();
            builder.Services.AddSingleton<Keryhe.Switchboard.Protocol.IServerConnectionSelector, Keryhe.Switchboard.Registry.RoundRobinServerConnectionSelector>();
            builder.Services.AddSingleton<Keryhe.Switchboard.Core.ILocalTransportRegistry, Keryhe.Switchboard.Registry.LocalTransportRegistry>();
            builder.Services.AddSingleton<Keryhe.Switchboard.Core.IBackplane, Keryhe.Switchboard.Registry.NoOpBackplane>();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<Keryhe.Switchboard.Registry.IPendingConnectionStore, Keryhe.Switchboard.Registry.InMemoryPendingConnectionStore>();
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

            app.UseCors("Switchboard");
            app.UseWebSockets();

            app.MapGet("/healthz", (Keryhe.Switchboard.Protocol.IHubRegistry hubRegistry) =>
            {
                var allHealthy = hubRegistry.GetAllHubs().All(h => h.ActiveServerConnectionCount > 0);
                return allHealthy
                    ? Results.Ok(new { status = "healthy" })
                    : Results.Json(new { status = "unhealthy" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            });

            app.MapPost("/{hub}/negotiate", Keryhe.Switchboard.Server.Negotiate.NegotiateEndpoint.HandleAsync);
            app.MapGet("/server/{hub}", Keryhe.Switchboard.Server.ServerConnections.ServerConnectionEndpoint.HandleAsync);
            app.MapGet("/{hub}", Keryhe.Switchboard.Server.ClientConnections.ClientConnectionEndpoint.HandleAsync);

            return app;
        }
    }
}
