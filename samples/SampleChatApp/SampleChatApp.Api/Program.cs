using System.Text;
using Keryhe.Switchboard.Connector;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SampleChatApp.Api.Hubs;
using SampleChatApp.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var key = builder.Configuration["Auth:UserTokenSigningKey"]!;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = "sample-chat-app",
            ValidAudience = "sample-chat-app-user",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
        };

        // The client sends the token as a query string parameter on the WebSocket upgrade
        // (standard SignalR convention — see 03-protocol.md §1.2), so it must be read from there too.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/chatHub"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

// .AddMessagePackProtocol() is required for a MessagePack client to negotiate against this app
// server at all — HubConnectionHandler's IHubProtocolResolver only knows protocols the app
// server registered (Phase 5 finding 3).
builder.Services.AddSignalR().AddMessagePackProtocol();
builder.Services.AddSwitchboardConnector(options =>
{
    options.ServiceUrl = builder.Configuration["Switchboard:Url"]!;
    options.ServerAccessToken = builder.Configuration["Switchboard:ServerToken"]!;
});

builder.Services.AddControllers();
builder.Services.AddScoped<MessageService>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ChatHub>("/chatHub");

app.Run();

namespace SampleChatApp.Api
{
    public partial class Program
    {
    }
}
