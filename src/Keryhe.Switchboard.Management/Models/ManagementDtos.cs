using System.Text.Json;

namespace Keryhe.Switchboard.Management.Models;

/// <summary>
/// Request body for every <c>/api/v1/hubs/.../send</c> variant (03-protocol.md Part 3).
/// <see cref="Arguments"/> arrives as raw <see cref="JsonElement"/>s — mapped to hub-protocol
/// primitives by <c>Keryhe.Switchboard.Protocol.Framing.ManagementInvocationWriter</c>, never
/// forwarded as-is (Phase 4 plan decision D22, finding 4).
/// </summary>
public sealed record ManagementSendRequest(string Target, JsonElement[]? Arguments);

/// <summary>
/// <c>GET /api/v1/health</c> response (03-protocol.md Part 3, Phase 4 plan decision D27) — distinct
/// from <c>/healthz</c>'s deliberately minimal, public, cached body: this is the authenticated,
/// per-hub detail view, and unlike <c>/healthz</c> it may do real cluster I/O per request since it
/// is called at human/dashboard rates, not load-balancer probe rates.
/// </summary>
public sealed record ManagementHealthResponse(string Status, IReadOnlyDictionary<string, int> ServerConnections, int ClientConnections);
