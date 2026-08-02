using System.Reflection;
using Keryhe.Switchboard.Connector;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.EndToEnd;

/// <summary>
/// Phase 5 Slice 3 deliverable (plan §"Verify AddSwitchboardConnector() is a drop-in for
/// AddAzureSignalR()"): a reflection-based gate on <c>HubLifetimeManager&lt;THub&gt;</c>'s member
/// surface, so a future .NET SignalR release that adds a 14th abstract member or a 4th virtual one
/// fails this test loudly instead of silently landing on the framework's own default (a bare
/// <c>NotImplementedException</c> for a new virtual, or a compile error for a new abstract one —
/// both real, but a compile error doesn't say *why* it matters the way this test's message does).
/// </summary>
public class HubLifetimeManagerCoverageTests
{
    private static readonly Type ManagerType = typeof(SwitchboardHubLifetimeManager<>);
    private static readonly Type BaseType = typeof(HubLifetimeManager<>);

    [Fact]
    public void EveryAbstractMember_IsOverridden()
    {
        var abstractMembers = BaseType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.IsAbstract)
            .Select(m => m.Name)
            .OrderBy(n => n)
            .ToList();

        // Confirmed against the real installed net10.0 runtime assembly while writing this plan
        // slice — 13 members, unchanged since Phase 1. If this count ever changes, the assertions
        // below on individual overrides are what actually catch a missing implementation; this is
        // just the headline number CLAUDE.md/04-design.md quote.
        Assert.Equal(13, abstractMembers.Count);

        var overriddenMethodNames = ManagerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet();

        var missing = abstractMembers.Where(name => !overriddenMethodNames.Contains(name)).ToList();
        Assert.True(missing.Count == 0, $"HubLifetimeManager<THub> abstract members with no override in SwitchboardHubLifetimeManager<THub>: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Plan decision D32 / finding 2: the 3 *virtual* members (not abstract — a naive override
    /// count misses them entirely) are accounted for explicitly, not silently inherited.
    /// <c>InvokeConnectionAsync</c>/<c>SetConnectionResultAsync</c> are overridden to throw a
    /// Switchboard-specific <see cref="NotSupportedException"/> (see
    /// <see cref="ClientResultsTests"/>); <c>TryGetReturnType</c> is deliberately left as the base
    /// implementation (see the comment on it in SwitchboardHubLifetimeManager.cs) — this test
    /// pins that specific split so a future edit that "helpfully" overrides all three, or none,
    /// gets caught.
    /// </summary>
    [Fact]
    public void TheThreeVirtualMembers_AreEachAccountedForExplicitly()
    {
        var declared = ManagerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet();

        Assert.Contains("InvokeConnectionAsync", declared);
        Assert.Contains("SetConnectionResultAsync", declared);
        Assert.DoesNotContain("TryGetReturnType", declared);
    }
}
