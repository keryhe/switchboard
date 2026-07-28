using Orleans;

namespace Keryhe.Switchboard.Orleans.Grains;

/// <summary>Cluster-wide record of which node holds a <c>connectionToken</c>'s live transport (plan
/// decision D19, Phase 3 Slice 5) — keyed by <c>connectionToken</c> itself, one activation per live
/// connection. Deliberately separate from <see cref="IConnectionGrain"/>: that grain's
/// <c>ConnectionRecord</c> is only populated once the SignalR handshake completes, but the SSE/Long
/// Polling forward hop needs an answer starting the instant the establishing <c>GET</c> returns —
/// before the handshake <c>POST</c> that follows it has even been sent. Interface and every method
/// carry <see cref="AliasAttribute"/> so a rename is not a wire-breaking change (plan decision D20).</summary>
[Alias("Keryhe.Switchboard.Orleans.Grains.IConnectionTokenOwnerGrain")]
public interface IConnectionTokenOwnerGrain : IGrainWithStringKey
{
    [Alias("Claim")]
    Task ClaimAsync(string nodeId);

    [Alias("Release")]
    Task ReleaseAsync();

    [Alias("GetOwnerNodeId")]
    Task<string?> GetOwnerNodeIdAsync();
}
