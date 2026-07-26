using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Phase0.Spike.Connector.Negotiate;
using Phase0.Spike.Host;
using Phase0.Spike.Host.Diagnostics;
using Phase0.Spike.Host.Hubs;
using Phase0.Spike.Host.Negotiate;
using Phase0.Spike.Host.Stub;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddControllers();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = TestConstants.Issuer,
            ValidAudience = TestConstants.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestConstants.SigningKey)),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
        };
    });
builder.Services.AddAuthorization();

// The mechanism under test: A2.
builder.Services.TryAddEnumerable(
    ServiceDescriptor.Singleton<MatcherPolicy, SwitchboardNegotiateMatcherPolicy>());

// Harmless policies at orders below/above the Switchboard policy — A4 ordering/isolation.
builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, LowOrderNoOpPolicy>());
builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, HighOrderNoOpPolicy>());

builder.Services.AddSingleton<INegotiateRedirectHandler, HostNegotiateRedirectHandler>();

var app = builder.Build();

app.UseWebSockets();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<TestHub>("/testHub");
app.MapHub<SecureHub>("/secureHub");

app.MapControllers();
app.MapGet("/api/ping", () => Results.Ok(new { pong = true, via = "minimal-api" }));

StubTargetEndpoints.Map(app);
EndpointDumpEndpoint.Map(app);

app.MapGet("/__diag/stub-observed", () => Results.Json(new
{
    negotiatedHubs = StubTargetEndpoints.ObservedNegotiateHubs.ToArray(),
    connectedHubs = StubTargetEndpoints.ObservedConnectionHubs.ToArray()
}));

app.Run();

// Exposed so WebApplicationFactory<Program> can be used from the test project.
public partial class Program;
