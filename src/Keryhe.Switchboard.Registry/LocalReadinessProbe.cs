using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol;

namespace Keryhe.Switchboard.Registry;

/// <summary>
/// Single-node/in-memory <see cref="IReadinessProbe"/> (Phase 3 Slice 7) — <see cref="IHubRegistry"/>
/// is already node-local, synchronous, in-memory state in this mode, so there is no I/O to cache
/// against and every request can simply re-check it directly. Vacuously ready when no hub has ever
/// registered a server connection on this node, matching <c>/healthz</c>'s pre-Slice-7 behavior.
/// </summary>
public sealed class LocalReadinessProbe(IHubRegistry hubRegistry) : IReadinessProbe
{
    public bool IsReady => hubRegistry.GetAllHubs().All(h => h.ActiveServerConnectionCount > 0);
}
