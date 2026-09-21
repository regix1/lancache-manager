using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    [Fact]
    public async Task PersistentContainerImageServiceSkipsNeverStartedPlatformAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);
        using var services = new ServiceCollection()
            .AddSingleton<SteamDaemonService>(daemon)
            .BuildServiceProvider();
        var service = new PersistentContainerImageService(
            NullLogger<PersistentContainerImageService>.Instance,
            new ConfigurationBuilder().Build(),
            services.GetRequiredService<IServiceScopeFactory>());

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Empty(gateway.ImagePulls);
        Assert.Equal(0, gateway.DestructiveCallCount);
    }

    [Fact]
    public async Task PersistentContainerImageServicePullsRemotelyAtMostOnceWithinFiveMinutesAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);
        var session = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        session.ImageId = "sha256:image-b";
        using var services = new ServiceCollection()
            .AddSingleton<SteamDaemonService>(daemon)
            .BuildServiceProvider();
        var service = new PersistentContainerImageService(
            NullLogger<PersistentContainerImageService>.Instance,
            new ConfigurationBuilder().Build(),
            services.GetRequiredService<IServiceScopeFactory>());

        await service.RunOnceAsync(CancellationToken.None);
        await service.RunOnceAsync(CancellationToken.None);

        Assert.Single(gateway.ImagePulls);
        Assert.Equal(2, gateway.CountOf("InspectImage:"));
    }

    [Fact]
    public async Task ConfiguredImageChangePullsImmediatelyAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        var dependencies = MakeDeps(
            database,
            sessions,
            Config(PersistenceMode.FullPersistence, true),
            image: "example/prefill:first");
        using var daemon = new TestSteamDaemon(dependencies, gateway);
        var session = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        session.ImageId = "sha256:image-b";
        using var services = new ServiceCollection()
            .AddSingleton<SteamDaemonService>(daemon)
            .BuildServiceProvider();
        var service = new PersistentContainerImageService(
            NullLogger<PersistentContainerImageService>.Instance,
            new ConfigurationBuilder().Build(),
            services.GetRequiredService<IServiceScopeFactory>());

        await service.RunOnceAsync(CancellationToken.None);
        dependencies.Configuration["Prefill:SteamDockerImage"] = "example/prefill:second";
        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, gateway.ImagePulls.Count);
        Assert.Equal("example/prefill:first", gateway.ImagePulls[0].FromImage);
        Assert.Equal("example/prefill:second", gateway.ImagePulls[1].FromImage);
    }

    [Fact]
    public async Task EqualImageIdUpdatesReferenceBeforeThatReferenceMovesAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var status = IdleStatus(Guid.NewGuid().ToString());
        var client = new FakeReconnectDaemonClient
        {
            StatusHandler = _ => Task.FromResult<DaemonStatus?>(status)
        };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-a" };
        var dependencies = MakeDeps(
            database,
            sessions,
            Config(PersistenceMode.FullPersistence, true),
            image: "example/prefill:first");
        using var daemon = new TestSteamDaemon(dependencies, gateway);
        var session = InjectedPersistentSession(daemon, client);
        session.ContainerId = "container-a";
        session.ContainerName = SteamPersistentContainerName;
        session.ConfiguredImage = "example/prefill:first";
        session.ImageId = "sha256:image-a";
        session.Capabilities = status;
        session.AuthState = DaemonAuthState.Authenticated;
        session.LoginSettled = true;
        gateway.AddContainer(new FakeContainer
        {
            Id = session.ContainerId,
            Name = session.ContainerName,
            Running = true,
            ImageId = session.ImageId
        });

        dependencies.Configuration["Prefill:SteamDockerImage"] = "example/prefill:second";
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Equal("example/prefill:second", session.ConfiguredImage);
        Assert.Equal(0, gateway.DestructiveCallCount);

        gateway.ResolvedImageId = "sha256:image-b";
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        var successor = Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
        Assert.NotEqual(session.Id, successor.Id);
        Assert.Equal("example/prefill:second", successor.ConfiguredImage);
        Assert.Equal("sha256:image-b", successor.ImageId);
        Assert.Single(gateway.Removals, removal => removal.Id == "container-a");
    }

    [Fact]
    public async Task StartupReplacementFailureRetriesOnScheduledPassWithoutRestartAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var oldSessionId = await SeedActivePersistentRowAsync(sessions, DateTime.UtcNow.AddDays(17));
        var oldStatus = IdleStatus(Guid.NewGuid().ToString());
        var newStatus = IdleStatus(Guid.NewGuid().ToString());
        var clients = new Queue<IDaemonClient>(
        [
            new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(oldStatus) },
            new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(newStatus) }
        ]);
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        gateway.FailNextCreateContainer(new InvalidOperationException("simulated startup create failure"));
        var oldContainer = RunningPersistentContainer(oldSessionId);
        oldContainer.ImageId = "sha256:image-a";
        gateway.AddContainer(oldContainer);
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway,
            () => clients.Dequeue());

        await daemon.StartAsync(CancellationToken.None);

        Assert.True(gateway.IsAvailable);
        Assert.True(daemon.HasPersistentImageWork);
        using var services = new ServiceCollection()
            .AddSingleton<SteamDaemonService>(daemon)
            .BuildServiceProvider();
        var service = new PersistentContainerImageService(
            NullLogger<PersistentContainerImageService>.Instance,
            new ConfigurationBuilder().Build(),
            services.GetRequiredService<IServiceScopeFactory>());

        await service.RunOnceAsync(CancellationToken.None);

        Assert.True(gateway.IsAvailable);
        var successor = Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
        Assert.Equal("sha256:image-b", successor.ImageId);
        Assert.Equal(2, gateway.CountOf($"Create:{SteamPersistentContainerName}"));
        Assert.Single(gateway.Removals, removal => removal.Id == oldContainer.Id);
    }

    [Fact]
    public async Task PlatformResolutionFailureDoesNotStopAnotherImageCheckAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);
        var session = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        session.ImageId = "sha256:image-b";
        using var services = new ServiceCollection()
            .AddSingleton<SteamDaemonService>(daemon)
            .AddSingleton<EpicPrefillDaemonService>(_ => throw new InvalidOperationException("simulated platform resolution failure"))
            .BuildServiceProvider();
        var service = new PersistentContainerImageService(
            NullLogger<PersistentContainerImageService>.Instance,
            new ConfigurationBuilder().Build(),
            services.GetRequiredService<IServiceScopeFactory>());

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Single(gateway.ImagePulls);
        Assert.Equal(1, gateway.CountOf("InspectImage:"));
    }
}
