using Keryhe.Switchboard.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Keryhe.Switchboard.Management;

/// <summary>
/// Maps <c>/api/v1</c> into the same <see cref="WebApplication"/> as every other Switchboard
/// endpoint (Phase 4 plan decision D21) — the management API is not a separate host or a separate
/// listener, so it inherits CORS, DI, and (in clustered mode) Orleans wiring for free.
/// </summary>
public static class ManagementApiExtensions
{
    /// <summary>Registers <c>IGroupMembershipService</c> unconditionally — needed by
    /// <c>RoutingServerEnvelopeDispatcher</c> for the app-server-originated <c>add_to_group</c>/
    /// <c>remove_from_group</c> envelopes regardless of whether the management API itself is ever
    /// mapped. <c>GroupMembershipService</c> lives in <c>Keryhe.Switchboard.Core</c>, which has no
    /// dependency beyond the BCL, so <c>NodeId</c> is extracted from options here rather than
    /// threading <c>IOptions&lt;SwitchboardOptions&gt;</c> into Core.</summary>
    public static IServiceCollection AddSwitchboardManagement(this IServiceCollection services)
    {
        services.AddSingleton<Keryhe.Switchboard.Core.IGroupMembershipService>(sp =>
        {
            var nodeId = sp.GetRequiredService<IOptions<SwitchboardOptions>>().Value.NodeId;
            return new Keryhe.Switchboard.Core.GroupMembershipService(
                sp.GetRequiredService<Keryhe.Switchboard.Core.IConnectionRegistry>(),
                sp.GetRequiredService<Keryhe.Switchboard.Core.ILocalTransportRegistry>(),
                sp.GetRequiredService<Keryhe.Switchboard.Core.IBackplane>(),
                nodeId);
        });

        // Registered unconditionally (cheap, no I/O) — the document itself is only ever mapped
        // (MapSwitchboardManagement below) when the management API is actually enabled, same
        // fail-closed-by-absence posture as every other route in this group (Phase 4 Slice 6).
        // Default (unnamed) document — MapOpenApi's route pattern below has no {documentName}
        // token, so it resolves to the SDK's own default document name ("v1") regardless; naming
        // this one to match avoids a name mismatch.
        services.AddOpenApi();

        return services;
    }

    /// <summary>
    /// Does not map anything at all when <see cref="SwitchboardOptions.EnableManagementApi"/> is
    /// off or no <see cref="SwitchboardOptions.ManagementSigningKey"/> is configured — fails closed
    /// by absence (404), not by an unauthenticated 401 surface (plan decision D21).
    /// </summary>
    /// <remarks>
    /// Reads raw configuration for that decision rather than <c>IOptions&lt;SwitchboardOptions&gt;</c>
    /// — this runs during <c>Program.BuildApp</c>, before <c>StartAsync</c>, and resolving
    /// <c>IOptions&lt;T&gt;.Value</c> triggers the options' <c>.Validate(...)</c> chain immediately
    /// (not only via <c>.ValidateOnStart()</c>'s hosted-service check), which would move an unrelated
    /// configuration's <see cref="OptionsValidationException"/> — e.g. Pattern A's
    /// <c>TrustedProxyNetworks</c> check — from <c>StartAsync</c> to <c>BuildApp</c> merely because
    /// this method happened to touch the same options object first. Mirrors how
    /// <c>Program.BuildApp</c> itself already reads <c>UseOrleansCluster</c> straight from
    /// <c>IConfiguration</c> for the same reason.
    /// </remarks>
    public static void MapSwitchboardManagement(this WebApplication app)
    {
        var enabled = app.Configuration.GetValue<bool>("Switchboard:EnableManagementApi");
        var signingKey = app.Configuration["Switchboard:ManagementSigningKey"];
        if (!enabled || string.IsNullOrWhiteSpace(signingKey))
        {
            return;
        }

        var group = app.MapGroup("/api/v1").AddEndpointFilter<ManagementAuthFilter>();

        group.MapPut("/hubs/{hubName}/groups/{groupName}/connections/{connectionId}", GroupEndpoints.AddToGroupAsync);
        group.MapDelete("/hubs/{hubName}/groups/{groupName}/connections/{connectionId}", GroupEndpoints.RemoveFromGroupAsync);

        group.MapPost("/hubs/{hubName}/send", SendEndpoints.BroadcastAsync);
        group.MapPost("/hubs/{hubName}/users/{userId}/send", SendEndpoints.SendToUserAsync);
        group.MapPost("/hubs/{hubName}/groups/{groupName}/send", SendEndpoints.SendToGroupAsync);

        group.MapGet("/hubs/{hubName}/connections", ConnectionsEndpoints.ListAsync);
        group.MapGet("/health", HealthEndpoints.GetAsync);

        // Outside the ManagementAuthFilter group deliberately — the spec describes shapes, not
        // data, and tooling (Swagger UI, client generators) conventionally expects an unauthenticated
        // fetch of the document itself even when the described API requires a token.
        app.MapOpenApi("/api/v1/openapi.json");
    }
}
