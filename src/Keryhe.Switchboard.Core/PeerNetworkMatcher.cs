using System.Net;

namespace Keryhe.Switchboard.Core;

/// <summary>
/// CIDR allowlist matching against the immediate peer address — shared by Pattern A's
/// <c>TrustedProxyNetworks</c> (plan decision D11) and the management API's
/// <c>ManagementAllowedNetworks</c> (Phase 4 plan decision D29), so the one verified matching
/// behavior (IPv4-mapped-IPv6 normalization via <see cref="IPAddress.MapToIPv4"/>) is not
/// reimplemented a second time.
/// </summary>
public static class PeerNetworkMatcher
{
    public static bool IsTrustedPeer(IPAddress? remoteIpAddress, IReadOnlyList<string> trustedNetworks)
    {
        if (remoteIpAddress is null)
        {
            return false;
        }

        var peer = remoteIpAddress.IsIPv4MappedToIPv6 ? remoteIpAddress.MapToIPv4() : remoteIpAddress;

        foreach (var cidr in trustedNetworks)
        {
            if (IPNetwork.TryParse(cidr, out var network) && network.Contains(peer))
            {
                return true;
            }
        }

        return false;
    }
}
