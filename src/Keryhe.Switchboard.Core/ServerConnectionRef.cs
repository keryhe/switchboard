namespace Keryhe.Switchboard.Core;

/// <summary>
/// Format/parse for a node-qualified server-connection reference. Not yet used by any lookup site
/// as of Phase 3 Slice 0 — <see cref="Models.ClientConnectionState.ServerConnectionId"/> still holds
/// a bare id, since server-connection assignment is still node-local. Introduced here ahead of plan
/// decision D18 (Phase 3 Slice 4), so that when assignment becomes cluster-wide there is exactly one
/// place that knows the composite format — the two lookup sites that key off this value
/// (<c>DefaultMessageRouter.RouteClientMessageAsync</c> and
/// <c>RoutingServerEnvelopeDispatcher.CloseClientConnectionAsync</c>) parse it through here instead
/// of doing string surgery inline, so neither can silently drift from the other's expectation of the
/// format.
/// </summary>
public static class ServerConnectionRef
{
    private const char Separator = ':';

    public static string Format(string nodeId, string serverConnectionId) =>
        $"{nodeId}{Separator}{serverConnectionId}";

    /// <summary>False for anything that isn't exactly one separator with non-empty parts on both
    /// sides — deliberately strict, since a malformed reference silently missing a lookup (rather
    /// than throwing) is exactly the failure mode finding 12 warns about.</summary>
    public static bool TryParse(string reference, out string nodeId, out string serverConnectionId)
    {
        var separatorIndex = reference.IndexOf(Separator);
        if (separatorIndex <= 0 || separatorIndex == reference.Length - 1 ||
            reference.IndexOf(Separator, separatorIndex + 1) >= 0)
        {
            nodeId = "";
            serverConnectionId = "";
            return false;
        }

        nodeId = reference[..separatorIndex];
        serverConnectionId = reference[(separatorIndex + 1)..];
        return true;
    }
}
