using Keryhe.Switchboard.Orleans;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Xunit;

namespace Keryhe.Switchboard.UnitTests.TestSupport;

/// <summary>
/// Starts one in-process Orleans silo (memory clustering + memory storage, same shape as
/// <see cref="SwitchboardOrleansExtensions.AddSwitchboardOrleans"/>'s dev/single-node branch) shared
/// by every Orleans-backed conformance test in the class that owns this fixture — starting a fresh
/// silo per test method would be needlessly slow. Grain state persists for the fixture's whole
/// lifetime, which is why the conformance suites namespace their test data per test-class instance
/// rather than assuming isolation.
/// </summary>
public sealed class OrleansTestSiloFixture : IAsyncLifetime
{
    private IHost? _host;

    public IGrainFactory GrainFactory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _host = Host.CreateDefaultBuilder()
            .UseOrleans(silo => silo
                .UseLocalhostClustering()
                .AddMemoryGrainStorage(SwitchboardOrleansExtensions.StorageProviderName))
            .ConfigureServices(services => services.AddSingleton<Keryhe.Switchboard.Core.SwitchboardMetrics>())
            .Build();

        await _host.StartAsync();
        GrainFactory = _host.Services.GetRequiredService<IGrainFactory>();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}

/// <summary>
/// Every Orleans-backed conformance test class shares exactly one silo via this collection (rather
/// than each declaring its own <c>IClassFixture&lt;OrleansTestSiloFixture&gt;</c>) for two reasons:
/// starting a silo is expensive enough to want it exactly once, and — more importantly —
/// <c>UseLocalhostClustering()</c> binds default silo/gateway ports, so two independently-started
/// fixtures running concurrently (xunit parallelizes across test classes/collections by default)
/// would collide on those ports. Being in the same collection also guarantees xunit never runs
/// these test classes concurrently with each other.
/// </summary>
[CollectionDefinition(Name)]
public sealed class OrleansTestCollection : ICollectionFixture<OrleansTestSiloFixture>
{
    public const string Name = "Orleans silo";
}
