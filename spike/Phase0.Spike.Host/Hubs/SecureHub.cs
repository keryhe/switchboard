using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Phase0.Spike.Host.Hubs;

/// <summary>
/// Class-level [Authorize] hub. Negotiate is the only surviving enforcement point for this once
/// SwitchboardNegotiateMatcherPolicy has taken over the endpoint (see docs/docs/04-design.md §8
/// and spike plan §3/A3) — a bare replacement endpoint would silently drop this.
/// </summary>
[Authorize]
public class SecureHub : Hub
{
    public string Echo(string message) => message;
}
