using Keryhe.Switchboard.Core;
using Keryhe.Switchboard.Protocol.Framing;

namespace Keryhe.Switchboard.Server.ClientConnections;

/// <summary>
/// Extends <see cref="IClientTransport"/> with the negotiated frame reader/writer (plan decision
/// D8/D10). This lives in <c>Keryhe.Switchboard.Server</c> rather than on
/// <see cref="IClientTransport"/> itself because <c>Keryhe.Switchboard.Core</c> has no ASP.NET
/// dependency and <see cref="IHubProtocolFraming"/> (via <c>TransferFormat</c>) does — every
/// concrete transport (WebSocket now; SSE/Long Polling in later slices) implements this so
/// <see cref="ClientConnectionLifecycle"/> can drive any of them without knowing which one it has.
/// </summary>
public interface IFramedClientTransport : IClientTransport
{
    /// <summary>
    /// Starts every connection at <see cref="JsonFraming.Instance"/> — the handshake itself is
    /// always JSON regardless of the eventual protocol (verified against a real MessagePack
    /// client) — and is switched by <see cref="ClientConnectionLifecycle"/> once the handshake
    /// request reveals the real protocol, before the handshake response is written.
    /// </summary>
    IHubProtocolFraming Framing { get; set; }

    /// <summary>
    /// Whether this transport can carry a Binary transfer-format protocol (MessagePack). SSE is
    /// text-only (03-protocol.md §1.5, plan Slice 5) — a MessagePack handshake over SSE must be
    /// rejected the same way an unsupported protocol name would be, rather than accepted and then
    /// silently mis-framed.
    /// </summary>
    bool SupportsBinaryTransferFormat { get; }
}
