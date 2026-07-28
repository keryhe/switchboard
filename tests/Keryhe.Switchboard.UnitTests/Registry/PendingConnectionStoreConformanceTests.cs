using Keryhe.Switchboard.Orleans;
using Keryhe.Switchboard.Registry;
using Keryhe.Switchboard.UnitTests.TestSupport;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.Registry;

/// <summary>
/// Implementation-agnostic conformance suite for <see cref="IPendingConnectionStore"/> (plan
/// decision D20) — the Orleans implementation (Phase 3 Slice 1) runs the same tests as
/// <see cref="InMemoryPendingConnectionStore"/>. One-shot consumption and TTL expiry are the entire
/// security value of <c>connectionToken</c> (04-design.md §1) and are invisible in a happy-path
/// test, so both get dedicated cases here rather than being assumed from the interface's XML docs.
/// </summary>
public abstract class PendingConnectionStoreConformanceTestsBase
{
    protected abstract IPendingConnectionStore CreateStore(TimeProvider timeProvider);

    private static PendingConnection NewPending(string token, DateTimeOffset expiresAt, string connectionId = "conn-1") =>
        new(token, connectionId, "hub", "alice", null, expiresAt);

    [Fact]
    public async Task TryConsumeAsync_ReturnsTheEntry()
    {
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateStore(time);
        await store.AddAsync(NewPending("token-1", time.GetUtcNow().AddMinutes(1)));

        var result = await store.TryConsumeAsync("token-1");

        Assert.NotNull(result);
        Assert.Equal("conn-1", result!.ConnectionId);
    }

    [Fact]
    public async Task TryConsumeAsync_IsOneShot_SecondCallReturnsNull()
    {
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateStore(time);
        await store.AddAsync(NewPending("token-1", time.GetUtcNow().AddMinutes(1)));

        await store.TryConsumeAsync("token-1");
        var second = await store.TryConsumeAsync("token-1");

        Assert.Null(second);
    }

    [Fact]
    public async Task TryConsumeAsync_UnknownToken_ReturnsNull()
    {
        var store = CreateStore(new FixedTimeProvider(DateTimeOffset.UtcNow));
        Assert.Null(await store.TryConsumeAsync("missing"));
    }

    [Fact]
    public async Task TryConsumeAsync_ExpiredEntry_ReturnsNull()
    {
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateStore(time);
        await store.AddAsync(NewPending("token-1", time.GetUtcNow().AddSeconds(-1)));

        Assert.Null(await store.TryConsumeAsync("token-1"));
    }

    [Fact]
    public async Task ReapExpiredAsync_RemovesOnlyExpiredEntries()
    {
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateStore(time);
        await store.AddAsync(NewPending("expired", time.GetUtcNow().AddSeconds(-1), "conn-expired"));
        await store.AddAsync(NewPending("live", time.GetUtcNow().AddMinutes(1), "conn-live"));

        await store.ReapExpiredAsync();

        Assert.Null(await store.TryConsumeAsync("expired"));
        Assert.NotNull(await store.TryConsumeAsync("live"));
    }

    [Fact]
    public async Task AddAsync_TwiceForSameToken_LastWriteWins()
    {
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateStore(time);
        await store.AddAsync(NewPending("token-1", time.GetUtcNow().AddMinutes(1), "conn-first"));
        await store.AddAsync(NewPending("token-1", time.GetUtcNow().AddMinutes(1), "conn-second"));

        var result = await store.TryConsumeAsync("token-1");

        Assert.Equal("conn-second", result!.ConnectionId);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public sealed class InMemoryPendingConnectionStoreConformanceTests : PendingConnectionStoreConformanceTestsBase
{
    protected override IPendingConnectionStore CreateStore(TimeProvider timeProvider) => new InMemoryPendingConnectionStore(timeProvider);
}

/// <summary>Same suite, same assertions, against <see cref="OrleansPendingConnectionStore"/> (plan
/// decision D19/D20, Phase 3 Slice 1) — including the one-shot-consumption and TTL-expiry cases,
/// the security-bearing ones. Every token literal in the base suite is used exactly once per test
/// method with a fresh <c>AddAsync</c> before any read, so no per-test namespacing is needed even
/// though the underlying silo (and its grain state) is shared across the whole class.</summary>
[Collection(OrleansTestCollection.Name)]
public sealed class OrleansPendingConnectionStoreConformanceTests(OrleansTestSiloFixture fixture)
    : PendingConnectionStoreConformanceTestsBase
{
    protected override IPendingConnectionStore CreateStore(TimeProvider timeProvider) =>
        new OrleansPendingConnectionStore(fixture.GrainFactory, timeProvider);
}
