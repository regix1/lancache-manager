using LancacheManager.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LancacheManager.Tests;

/// <summary>
/// What <see cref="ServiceCollectionExtensions.AddDatabaseBackedHostedService{T}"/> registers on a
/// setup-only boot, where the app has decided it has no database and has skipped migrations.
///
/// The distinction these tests pin is the whole point of the helper: the singleton must stay
/// registered so a controller that resolves the service still works, while the
/// <see cref="IHostedService"/> registration that starts its loop must not be added. Asserting on
/// the resolved <c>IEnumerable&lt;IHostedService&gt;</c> rather than on the descriptor list matters,
/// because that sequence is both what the host starts and what
/// <c>ServiceScheduleRegistry</c> is constructed from.
///
/// These build a bare <see cref="ServiceCollection"/> rather than the real host, so nothing here
/// depends on a database, a configuration file or an environment.
/// </summary>
public class SetupOnlyHostedServiceGateTests
{
    [Fact]
    public void WithoutADatabase_TheServiceIsResolvableButNeverStarted()
    {
        var services = new ServiceCollection();

        services.AddDatabaseBackedHostedService<IdleHostedService>(databaseAvailable: false);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IdleHostedService>());
        Assert.Empty(provider.GetServices<IHostedService>());
    }

    [Fact]
    public void WithADatabase_TheServiceStartsAndIsTheSameInstanceTheControllersResolve()
    {
        var services = new ServiceCollection();

        services.AddDatabaseBackedHostedService<IdleHostedService>(databaseAvailable: true);

        using var provider = services.BuildServiceProvider();

        var singleton = provider.GetRequiredService<IdleHostedService>();
        var hosted = Assert.Single(provider.GetServices<IHostedService>());

        Assert.Same(singleton, hosted);
    }

    /// <summary>
    /// Registering with the gate open must produce exactly what the ungated helper produces, or the
    /// 24 call sites that moved onto it would have changed behaviour for every normal boot.
    /// </summary>
    [Fact]
    public void WithADatabase_TheRegistrationMatchesTheUngatedHelper()
    {
        var gated = new ServiceCollection();
        gated.AddDatabaseBackedHostedService<IdleHostedService>(databaseAvailable: true);

        var ungated = new ServiceCollection();
        ungated.AddSingletonHostedService<IdleHostedService>();

        Assert.Equal(ungated.Count, gated.Count);
        Assert.Equal(
            ungated.Select(descriptor => descriptor.ServiceType),
            gated.Select(descriptor => descriptor.ServiceType));
        Assert.Equal(
            ungated.Select(descriptor => descriptor.Lifetime),
            gated.Select(descriptor => descriptor.Lifetime));
    }

    /// <summary>
    /// The control for the two tests above: this is what the 24 call sites did before they were
    /// gated, and it is why they churned against an absent database. The ungated helper registers
    /// the <see cref="IHostedService"/> unconditionally, so there is no argument it could have been
    /// passed that would have kept the service from starting on a setup-only boot. Without this,
    /// the pair above would still pass if the gate were quietly a no-op.
    /// </summary>
    [Fact]
    public void TheUngatedHelperStartsTheServiceWithNoWayToPreventIt()
    {
        var services = new ServiceCollection();

        services.AddSingletonHostedService<IdleHostedService>();

        using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IHostedService>());
    }

    /// <summary>
    /// Stands in for any of the gated services. It is deliberately inert: the helper decides whether
    /// a loop is ever started, and no assertion here should depend on what the loop would do.
    /// </summary>
    private sealed class IdleHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
