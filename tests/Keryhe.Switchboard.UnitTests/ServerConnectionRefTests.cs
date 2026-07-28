using Keryhe.Switchboard.Core;
using Xunit;

namespace Keryhe.Switchboard.UnitTests;

/// <summary>Not yet wired into any lookup site (that's Phase 3 Slice 4, plan decision D18) — this
/// pins the format/parse contract ahead of that, per finding 12 of the Phase 3 plan.</summary>
public class ServerConnectionRefTests
{
    [Fact]
    public void Format_ThenTryParse_RoundTrips()
    {
        var reference = ServerConnectionRef.Format("node-a", "srv-1");

        Assert.True(ServerConnectionRef.TryParse(reference, out var nodeId, out var serverConnectionId));
        Assert.Equal("node-a", nodeId);
        Assert.Equal("srv-1", serverConnectionId);
    }

    [Theory]
    [InlineData("no-separator-at-all")]
    [InlineData(":missing-node-id")]
    [InlineData("missing-server-connection-id:")]
    [InlineData("too:many:separators")]
    [InlineData("")]
    public void TryParse_RejectsMalformedInput_RatherThanSilentlyMisparsing(string malformed)
    {
        Assert.False(ServerConnectionRef.TryParse(malformed, out _, out _));
    }
}
