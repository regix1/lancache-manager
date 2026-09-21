using System.Net;
using System.Reflection;
using Docker.DotNet;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    [Theory]
    [InlineData("example/prefill", "example/prefill:latest")]
    [InlineData("example/prefill:stable", "example/prefill:stable")]
    [InlineData("registry.example:5000/prefill", "registry.example:5000/prefill:latest")]
    [InlineData("example/prefill@sha256:0123456789abcdef", "example/prefill@sha256:0123456789abcdef")]
    [InlineData("registry.example:5000/prefill@sha256:0123456789abcdef", "registry.example:5000/prefill@sha256:0123456789abcdef")]
    public async Task ImagePullPreservesConfiguredReferenceAsync(string configured, string expected)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true), image: configured),
            gateway);

        await PullAsync(daemon, CancellationToken.None);

        var pull = Assert.Single(gateway.ImagePulls);
        Assert.Equal(expected, pull.FromImage);
        Assert.Null(pull.Tag);
        Assert.Contains($"InspectImage:{expected}", gateway.Calls);
    }

    [Fact]
    public async Task PersistentCreatePinsInspectedImageIdWhenTagMovesAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        gateway.OnImageInspect = () => gateway.ResolvedImageId = "sha256:image-c";
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true), image: "example/prefill:stable"),
            gateway);

        await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());

        Assert.Equal("sha256:image-b", Assert.Single(gateway.CreatedParameters).Image);
    }

    [Fact]
    public async Task StartupReplacesIdlePersistentContainerWhenImageIdChangesAsync()
    {
        var (options, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var oldSessionId = await SeedActivePersistentRowAsync(sessions, DateTime.UtcNow.AddDays(17));
        var daemonInstanceId = Guid.NewGuid().ToString();
        var client = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(IdleStatus(daemonInstanceId)) };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        var oldContainer = RunningPersistentContainer(oldSessionId);
        oldContainer.ImageId = "sha256:image-a";
        gateway.AddContainer(oldContainer);
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.KeepAcrossRestart, true)),
            gateway,
            () => client);

        await daemon.StartAsync(CancellationToken.None);

        var successor = Assert.Single(SessionsOf(daemon).Values, session => session.IsPersistent);
        Assert.NotEqual(oldSessionId, successor.Id);
        Assert.Equal("sha256:image-b", successor.ImageId);
        Assert.Equal("sha256:image-b", Assert.Single(gateway.CreatedParameters).Image);
        Assert.Single(gateway.Removals, removal => removal.Id == oldContainer.Id);
        Assert.False(gateway.Removals.Single(removal => removal.Id == oldContainer.Id).Parameters.RemoveVolumes);
        Assert.Equal(0, client.LogoutCount);
        Assert.Equal(PrefillSessionStatus.Terminated, (await GetRowAsync(options, oldSessionId))!.Status);
    }

    [Fact]
    public async Task StartupKeepsPersistentContainerWhenFullImageIdMatchesAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessions, DateTime.UtcNow.AddDays(17));
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        var container = RunningPersistentContainer(sessionId);
        container.ImageId = "sha256:image-b";
        gateway.AddContainer(container);
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.KeepAcrossRestart, true)),
            gateway,
            () => new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(IdleStatus(Guid.NewGuid().ToString())) });

        await daemon.StartAsync(CancellationToken.None);

        Assert.Same(daemon.GetSession(sessionId), Assert.Single(SessionsOf(daemon).Values));
        Assert.Equal(0, gateway.DestructiveCallCount);
        Assert.Empty(gateway.CreatedParameters);
    }

    [Fact]
    public async Task PersistentStartChecksImageBeforeReturningActiveSessionAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var daemonInstanceId = Guid.NewGuid().ToString();
        var status = IdleStatus(daemonInstanceId);
        var client = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);
        var oldSession = InjectedPersistentSession(daemon, client);
        oldSession.ContainerId = "container-a";
        oldSession.ContainerName = SteamPersistentContainerName;
        oldSession.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        oldSession.ImageId = "sha256:image-a";
        oldSession.Capabilities = status;
        oldSession.LoginSettled = true;
        oldSession.AuthState = DaemonAuthState.Authenticated;
        gateway.AddContainer(new FakeContainer
        {
            Id = oldSession.ContainerId,
            Name = oldSession.ContainerName,
            Running = true,
            ImageId = oldSession.ImageId
        });

        var returned = await daemon.StartPersistentSessionAsync(
            PrefillPlatform.Steam,
            ScheduledPrefillConstants.DeriveSystemUserId());

        Assert.NotEqual(oldSession.Id, returned.Id);
        Assert.Equal("sha256:image-b", returned.ImageId);
        Assert.Single(gateway.Removals, removal => removal.Id == oldSession.ContainerId);
    }

    [Fact]
    public async Task PersistentStartRefreshesImageBeforeReusingActiveSessionAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);
        var session = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        session.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        session.ImageId = gateway.ResolvedImageId;

        var returned = await daemon.StartPersistentSessionAsync(
            PrefillPlatform.Steam,
            ScheduledPrefillConstants.DeriveSystemUserId());

        Assert.Same(session, returned);
        Assert.Equal(1, gateway.CountOf("CreateImage"));
        Assert.Equal(0, gateway.DestructiveCallCount);
    }

    [Fact]
    public async Task MissingImageIdentityNeverRemovesRunningPersistentContainerAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessions, DateTime.UtcNow.AddDays(17));
        var gateway = new RecordingContainerGateway { ResolvedImageId = string.Empty };
        var container = RunningPersistentContainer(sessionId);
        container.ImageId = "sha256:image-a";
        gateway.AddContainer(container);
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.KeepAcrossRestart, true)),
            gateway,
            () => new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(IdleStatus(Guid.NewGuid().ToString())) });

        await daemon.StartAsync(CancellationToken.None);

        Assert.NotNull(daemon.GetSession(sessionId));
        Assert.Equal(0, gateway.DestructiveCallCount);
    }

    [Fact]
    public async Task HeldRecoveryDefersImageReplacementUntilNextPassAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var daemonInstanceId = Guid.NewGuid().ToString();
        var status = IdleStatus(daemonInstanceId);
        var client = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);
        var session = InjectedPersistentSession(daemon, client);
        session.ContainerId = "container-a";
        session.ContainerName = SteamPersistentContainerName;
        session.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        session.ImageId = "sha256:image-a";
        session.Capabilities = status;
        session.LoginSettled = true;
        session.AuthState = DaemonAuthState.Authenticated;
        gateway.AddContainer(new FakeContainer
        {
            Id = session.ContainerId,
            Name = session.ContainerName,
            Running = true,
            ImageId = session.ImageId
        });

        await session.RecoveryWork.WaitAsync();
        try
        {
            await daemon.ReconcilePersistentImageAsync(CancellationToken.None);
            Assert.Equal(0, gateway.DestructiveCallCount);
        }
        finally
        {
            session.RecoveryWork.Release();
        }

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Single(gateway.Removals, removal => removal.Id == "container-a");
        Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
    }

    [Fact]
    public async Task ActiveDaemonOperationDefersImageReplacementAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var daemonInstanceId = Guid.NewGuid().ToString();
        var knownStatus = IdleStatus(daemonInstanceId);
        var busyStatus = IdleStatus(daemonInstanceId);
        busyStatus.ActiveOperations!.Add(new DaemonRunSnapshot
        {
            OperationId = Guid.NewGuid().ToString(),
            DaemonInstanceId = daemonInstanceId
        });
        var client = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(busyStatus) };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);
        var session = InjectedPersistentSession(daemon, client);
        session.ContainerId = "container-a";
        session.ContainerName = SteamPersistentContainerName;
        session.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        session.ImageId = "sha256:image-a";
        session.Capabilities = knownStatus;
        session.LoginSettled = true;
        gateway.AddContainer(new FakeContainer
        {
            Id = session.ContainerId,
            Name = session.ContainerName,
            Running = true,
            ImageId = session.ImageId
        });

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Equal(0, gateway.DestructiveCallCount);
        Assert.Same(session, daemon.GetSession(session.Id));
        Assert.False(session.AdmissionClosed);
    }

    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task PlannedImageUpdatePreservesLoginExpiryVolumeAndReloginStateAsync(PersistenceMode mode)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var daemonInstanceId = Guid.NewGuid().ToString();
        var status = IdleStatus(daemonInstanceId);
        var client = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(mode, true)),
            gateway,
            () => client);
        var session = InjectedPersistentSession(daemon, client);
        var expiresAt = DateTime.UtcNow.AddHours(-3);
        session.ExpiresAt = expiresAt;
        session.NeedsRelogin = true;
        session.ContainerId = "container-a";
        session.ContainerName = SteamPersistentContainerName;
        session.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        session.ImageId = "sha256:image-a";
        session.Capabilities = status;
        session.LoginSettled = true;
        session.AuthState = DaemonAuthState.Authenticated;
        gateway.AddContainer(new FakeContainer
        {
            Id = session.ContainerId,
            Name = session.ContainerName,
            Running = true,
            ImageId = session.ImageId
        });

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        var successor = Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
        Assert.Equal(expiresAt, successor.ExpiresAt);
        Assert.True(successor.NeedsRelogin);
        Assert.Equal(0, client.LogoutCount);
        Assert.Contains(
            $"lancache-prefill-persistent-steam:/app/Config",
            Assert.Single(gateway.CreatedParameters).HostConfig.Binds);
    }

    [Fact]
    public async Task UnknownReadoptedLoginDefersImageReplacementAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var daemonInstanceId = Guid.NewGuid().ToString();
        var status = IdleStatus(daemonInstanceId);
        var client = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);
        var session = InjectedPersistentSession(daemon, client);
        session.ContainerId = "container-a";
        session.ContainerName = SteamPersistentContainerName;
        session.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        session.ImageId = "sha256:image-a";
        session.Capabilities = status;
        session.LoginSettled = false;
        gateway.AddContainer(new FakeContainer
        {
            Id = session.ContainerId,
            Name = session.ContainerName,
            Running = true,
            ImageId = session.ImageId
        });

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Equal(0, gateway.DestructiveCallCount);
        Assert.Same(session, daemon.GetSession(session.Id));
    }

    [Fact]
    public async Task RemoveFailureRetainsPendingImageChangeForNextPassAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var daemonInstanceId = Guid.NewGuid().ToString();
        var status = IdleStatus(daemonInstanceId);
        var client = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        gateway.FailNextRemoveContainer(new InvalidOperationException("simulated remove failure"));
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);
        var session = InjectedPersistentSession(daemon, client);
        session.ContainerId = "container-a";
        session.ContainerName = SteamPersistentContainerName;
        session.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        session.ImageId = "sha256:image-a";
        session.Capabilities = status;
        session.LoginSettled = true;
        session.AuthState = DaemonAuthState.Authenticated;
        gateway.AddContainer(new FakeContainer
        {
            Id = session.ContainerId,
            Name = session.ContainerName,
            Running = true,
            ImageId = session.ImageId
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => daemon.ReconcilePersistentImageAsync(CancellationToken.None));
        Assert.True(daemon.HasPersistentImageWork);

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Equal(2, gateway.CountOf("Remove:container-a"));
        Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
    }

    [Fact]
    public async Task ExplicitStopCancelsPendingImageReplacementAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var daemonInstanceId = Guid.NewGuid().ToString();
        var status = IdleStatus(daemonInstanceId);
        var client = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        gateway.FailNextCreateContainer(new InvalidOperationException("simulated create failure"));
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);
        var session = InjectedPersistentSession(daemon, client);
        session.ContainerId = "container-a";
        session.ContainerName = SteamPersistentContainerName;
        session.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        session.ImageId = "sha256:image-a";
        session.Capabilities = status;
        session.LoginSettled = true;
        session.AuthState = DaemonAuthState.Authenticated;
        gateway.AddContainer(new FakeContainer
        {
            Id = session.ContainerId,
            Name = session.ContainerName,
            Running = true,
            ImageId = session.ImageId
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => daemon.ReconcilePersistentImageAsync(CancellationToken.None));
        await daemon.StopPersistentSessionAsync(session.Id, "admin");
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Contains("RemoveVolume:lancache-prefill-persistent-steam", gateway.Calls);
        Assert.Empty(SessionsOf(daemon));
        Assert.Equal(1, gateway.CountOf($"Create:{SteamPersistentContainerName}"));
    }

    [Fact]
    public async Task StartFailureRetriesPendingImageReplacementAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var daemonInstanceId = Guid.NewGuid().ToString();
        var status = IdleStatus(daemonInstanceId);
        var client = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        gateway.FailNextStartContainer(new InvalidOperationException("simulated start failure"));
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);
        var session = InjectedPersistentSession(daemon, client);
        session.ContainerId = "container-a";
        session.ContainerName = SteamPersistentContainerName;
        session.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        session.ImageId = "sha256:image-a";
        session.Capabilities = status;
        session.LoginSettled = true;
        gateway.AddContainer(new FakeContainer
        {
            Id = session.ContainerId,
            Name = session.ContainerName,
            Running = true,
            ImageId = session.ImageId
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => daemon.ReconcilePersistentImageAsync(CancellationToken.None));
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Equal(2, gateway.CountOf($"Create:{SteamPersistentContainerName}"));
        Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
    }

    [Fact]
    public async Task SimultaneousImagePassesReplaceOnceAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var daemonInstanceId = Guid.NewGuid().ToString();
        var status = IdleStatus(daemonInstanceId);
        var client = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);
        var session = InjectedPersistentSession(daemon, client);
        session.ContainerId = "container-a";
        session.ContainerName = SteamPersistentContainerName;
        session.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        session.ImageId = "sha256:image-a";
        session.Capabilities = status;
        session.LoginSettled = true;
        gateway.AddContainer(new FakeContainer
        {
            Id = session.ContainerId,
            Name = session.ContainerName,
            Running = true,
            ImageId = session.ImageId
        });

        await Task.WhenAll(
            daemon.ReconcilePersistentImageAsync(CancellationToken.None),
            daemon.ReconcilePersistentImageAsync(CancellationToken.None));

        Assert.Single(gateway.Removals, removal => removal.Id == session.ContainerId);
        Assert.Single(gateway.CreatedParameters);
        Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
    }

    [Fact]
    public async Task ConnectFailureAdoptsExactPendingReplacementOnNextPassAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var daemonInstanceId = Guid.NewGuid().ToString();
        var status = IdleStatus(daemonInstanceId);
        var oldClient = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) };
        var failedClient = new FakeReconnectDaemonClient
        {
            ConnectHandler = _ => Task.FromException(new IOException("simulated connect failure"))
        };
        var recoveredClient = new FakeReconnectDaemonClient();
        var clients = new Queue<IDaemonClient>([failedClient, recoveredClient]);
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway,
            () => clients.Dequeue());
        var session = InjectedPersistentSession(daemon, oldClient);
        session.ContainerId = "container-a";
        session.ContainerName = SteamPersistentContainerName;
        session.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        session.ImageId = "sha256:image-a";
        session.Capabilities = status;
        session.LoginSettled = true;
        gateway.AddContainer(new FakeContainer
        {
            Id = session.ContainerId,
            Name = session.ContainerName,
            Running = true,
            ImageId = session.ImageId
        });

        await Assert.ThrowsAsync<IOException>(
            () => daemon.ReconcilePersistentImageAsync(CancellationToken.None));
        var pendingContainer = Assert.Single(gateway.CreatedParameters);
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Single(gateway.CreatedParameters);
        var successor = Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
        Assert.Equal(pendingContainer.Name, successor.ContainerName);
        Assert.Same(recoveredClient, successor.Client);
    }

    [Theory]
    [InlineData("legacy-run")]
    [InlineData("admission-pending")]
    [InlineData("terminal-publication")]
    public async Task LocalWorkOwnershipDefersReplacementUntilSettledAsync(string ownership)
    {
        var scenario = NewImageScenario();
        using var daemon = scenario.Daemon;
        switch (ownership)
        {
            case "legacy-run":
                scenario.Session.IsPrefilling = true;
                break;
            case "admission-pending":
                var run = SettledRun(scenario.Session, admissionPending: true);
                scenario.Session.Runs.TryAdd(run.PrefillRunId, run);
                break;
            case "terminal-publication":
                scenario.Session.TerminalCompletedFlag = 1;
                break;
        }

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Equal(0, scenario.Gateway.DestructiveCallCount);
        Assert.False(scenario.Session.AdmissionClosed);

        scenario.Session.IsPrefilling = false;
        scenario.Session.TerminalCompletedFlag = 2;
        foreach (var run in scenario.Session.Runs.Values)
        {
            run.AdmissionPending = false;
        }
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Single(scenario.Gateway.Removals, removal => removal.Id == "container-a");
    }

    [Fact]
    public async Task MissingDaemonStatusDefersThenIdleStatusReplacesOnceAsync()
    {
        var client = new FakeReconnectDaemonClient
        {
            StatusHandler = _ => Task.FromResult<DaemonStatus?>(null)
        };
        var scenario = NewImageScenario(client);
        using var daemon = scenario.Daemon;

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Equal(0, scenario.Gateway.DestructiveCallCount);
        Assert.False(scenario.Session.AdmissionClosed);

        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(scenario.Status);
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Single(scenario.Gateway.Removals, removal => removal.Id == "container-a");
        Assert.Single(scenario.Gateway.CreatedParameters);
    }

    [Fact]
    public async Task HeldLegacyDispatchDefersReplacementUntilTerminalSettlementAsync()
    {
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<PrefillResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReconnectDaemonClient
        {
            StatusHandler = _ => Task.FromResult<DaemonStatus?>(null),
            DispatchHandler = (onDispatched, _) =>
            {
                dispatched.TrySetResult();
                return release.Task;
            }
        };
        var scenario = NewImageScenario(client);
        using var daemon = scenario.Daemon;
        scenario.Session.Capabilities = null;

        var prefill = daemon.PrefillAsync(scenario.Session.Id, force: true);
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        Assert.Equal(0, scenario.Gateway.DestructiveCallCount);

        release.SetResult(new PrefillResult { Success = false, ErrorCode = "simulated-terminal" });
        await prefill;
        scenario.Session.Capabilities = scenario.Status;
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(scenario.Status);
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Single(scenario.Gateway.Removals, removal => removal.Id == "container-a");
    }

    [Fact]
    public async Task InteractiveChallengeDefersUntilConfirmedCancelAsync()
    {
        var challenge = ImageChallenge("interactive-image-login");
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: challenge)
        {
            StatusOverride = IdleStatus(Guid.NewGuid().ToString())
        };
        var scenario = NewImageScenario(client, authenticated: false);
        using var daemon = scenario.Daemon;
        client.StatusOverride = scenario.Status;

        var received = await daemon.StartLoginAsync(scenario.Session.Id);
        Assert.Equal(challenge.ChallengeId, received?.ChallengeId);
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Equal(0, scenario.Gateway.DestructiveCallCount);
        Assert.False(scenario.Session.LoginSettled);

        await daemon.CancelLoginAsync(scenario.Session.Id);
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Single(scenario.Gateway.Removals, removal => removal.Id == "container-a");
    }

    [Fact]
    public async Task HeldHeadlessLoginAndCancelDeferUntilConfirmedCompletionAsync()
    {
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: ImageChallenge("headless-image-login"))
        {
            HoldStartLogin = true,
            HoldCancelLogin = true
        };
        var scenario = NewImageScenario(client, authenticated: false);
        using var daemon = scenario.Daemon;
        client.StatusOverride = scenario.Status;

        var headless = daemon.AttemptHeadlessPersistentSelfAuthAsync(scenario.Session);
        await client.StartLoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        Assert.Equal(0, scenario.Gateway.DestructiveCallCount);

        client.ReleaseStartLogin.SetResult();
        await client.CancelLoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        Assert.Equal(0, scenario.Gateway.DestructiveCallCount);

        client.ReleaseCancelLogin.SetResult();
        await headless;
        Assert.True(scenario.Session.LoginSettled);
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Single(scenario.Gateway.Removals, removal => removal.Id == "container-a");
    }

    [Fact]
    public async Task FailedHeadlessCancelKeepsImageReplacementDeferredAsync()
    {
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: ImageChallenge("failed-image-cancel"))
        {
            CancelAcknowledged = false
        };
        var scenario = NewImageScenario(client, authenticated: false);
        using var daemon = scenario.Daemon;
        client.StatusOverride = scenario.Status;

        await daemon.AttemptHeadlessPersistentSelfAuthAsync(scenario.Session);
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Equal(DaemonSessionStatus.Error, scenario.Session.Status);
        Assert.False(scenario.Session.LoginSettled);
        Assert.Equal(0, scenario.Gateway.DestructiveCallCount);
    }

    [Fact]
    public async Task PersistentStartAndImagePassShareOneInspectDecisionAsync()
    {
        var gateway = new RecordingContainerGateway
        {
            ResolvedImageId = "sha256:image-b",
            HoldImageInspect = true
        };
        var scenario = NewImageScenario(gateway: gateway);
        using var daemon = scenario.Daemon;

        var start = daemon.StartPersistentSessionAsync(
            PrefillPlatform.Steam,
            ScheduledPrefillConstants.DeriveSystemUserId());
        await gateway.ImageInspectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pass = daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        gateway.ReleaseImageInspect.SetResult();
        await Task.WhenAll(start, pass).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(gateway.Removals, removal => removal.Id == "container-a");
        Assert.Single(gateway.CreatedParameters);
        Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
    }

    [Fact]
    public async Task RunAdmissionDuringFinalStatusCannotCrossImageFenceAsync()
    {
        var status = IdleStatus(Guid.NewGuid().ToString());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReconnectDaemonClient
        {
            StatusHandler = async cancellationToken =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return status;
            },
            PrefillHandler = _ => Task.FromResult(new PrefillResult { Success = true })
        };
        var scenario = NewImageScenario(client, status: status);
        using var daemon = scenario.Daemon;

        var replacement = daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<DaemonCommandException>(
            () => daemon.PrefillAsync(scenario.Session.Id, force: true, appIds: ["10"]));
        Assert.Equal(0, client.PrefillCount);

        release.SetResult();
        await replacement;
        Assert.Single(scenario.Gateway.Removals, removal => removal.Id == "container-a");
    }

    [Fact]
    public async Task LoginAdmissionDuringFinalStatusCannotCrossImageFenceAsync()
    {
        var status = IdleStatus(Guid.NewGuid().ToString());
        status.Status = "awaiting-login";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedLoginDaemonClient
        {
            StatusHandler = async cancellationToken =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return status;
            }
        };
        var scenario = NewImageScenario(client, authenticated: false, status: status);
        using var daemon = scenario.Daemon;

        var replacement = daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<ConflictException>(() => daemon.StartLoginAsync(scenario.Session.Id));
        Assert.Equal(0, client.StartLoginCallCount);

        release.SetResult();
        await replacement;
        Assert.Single(scenario.Gateway.Removals, removal => removal.Id == "container-a");
    }

    [Fact]
    public async Task ShutdownDuringFinalStatusPreventsImageReplacementAsync()
    {
        var status = IdleStatus(Guid.NewGuid().ToString());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReconnectDaemonClient
        {
            StatusHandler = async cancellationToken =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return status;
            }
        };
        var scenario = NewImageScenario(client, status: status);
        using var daemon = scenario.Daemon;

        var replacement = daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = daemon.StopAsync(CancellationToken.None);
        release.SetResult();
        await Task.WhenAll(replacement, shutdown).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(scenario.Gateway.Removals, removal => removal.Id == "container-a");
        Assert.Empty(scenario.Gateway.CreatedParameters);
    }

    [Fact]
    public async Task EditMutationDefersImageReplacementUntilCleanupCompletesAsync()
    {
        var scenario = NewImageScenario();
        using var daemon = scenario.Daemon;
        await using (await daemon.PersistentEditSessionGate.EnterMutationAsync(CancellationToken.None))
        {
            await daemon.ReconcilePersistentImageAsync(CancellationToken.None);
            Assert.Equal(0, scenario.Gateway.DestructiveCallCount);
        }

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Single(scenario.Gateway.Removals, removal => removal.Id == "container-a");
    }

    [Fact]
    public async Task ExplicitStopWaitsForLateContainerStartAndRemovesTheSuccessorAsync()
    {
        var gateway = new RecordingContainerGateway
        {
            ResolvedImageId = "sha256:image-b",
            HoldStartContainer = true
        };
        var scenario = NewImageScenario(gateway: gateway);
        using var daemon = scenario.Daemon;

        var replacement = daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        await gateway.StartContainerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = daemon.StopPersistentSessionAsync(scenario.Session.Id, "admin");
        Assert.False(stop.IsCompleted);

        gateway.ReleaseStartContainer.SetResult();
        await Task.WhenAll(replacement, stop).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(SessionsOf(daemon));
        Assert.Equal(2, scenario.Gateway.Removals.Count);
        Assert.Single(scenario.Gateway.CreatedParameters);
    }

    [Fact]
    public async Task CreateFailureRetriesTheExactPendingImageChangeAsync()
    {
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        gateway.FailNextCreateContainer(new InvalidOperationException("simulated create failure"));
        var scenario = NewImageScenario(gateway: gateway);
        using var daemon = scenario.Daemon;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => daemon.ReconcilePersistentImageAsync(CancellationToken.None));
        Assert.True(daemon.HasPersistentImageWork);

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Equal(2, gateway.CountOf($"Create:{SteamPersistentContainerName}"));
        Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
        Assert.Equal("sha256:image-b", Assert.Single(SessionsOf(daemon).Values).ImageId);
    }

    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, false)]
    [InlineData(PersistenceMode.KeepAcrossRestart, true)]
    [InlineData(PersistenceMode.FullPersistence, true)]
    public async Task ShutdownAppliesModePolicyToPendingImageChangeAsync(PersistenceMode mode, bool pendingRetained)
    {
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        gateway.FailNextCreateContainer(new InvalidOperationException("simulated create failure"));
        var scenario = NewImageScenario(gateway: gateway, mode: mode);
        using var daemon = scenario.Daemon;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => daemon.ReconcilePersistentImageAsync(CancellationToken.None));

        await daemon.StopAsync(CancellationToken.None);

        Assert.Equal(pendingRetained, daemon.HasPersistentImageWork);
        if (!pendingRetained)
        {
            Assert.Contains("RemoveVolume:lancache-prefill-persistent-steam", gateway.Calls);
        }
    }

    [Fact]
    public async Task InvalidStoredLoginDoesNotStartOrPublishAHeadlessChallengeAsync()
    {
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: ImageChallenge("unsolicited-image-challenge"));
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)notifications;
        var scenario = NewImageScenario(client, authenticated: false, notifications: notifications);
        using var daemon = scenario.Daemon;
        client.StatusOverride = scenario.Status;

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt)
        {
            await attempt;
        }

        Assert.Equal(0, client.StartLoginCallCount);
        Assert.DoesNotContain(SignalREvents.CredentialChallenge, recorder.EventNames);
        Assert.Equal(DaemonAuthState.NotAuthenticated, Assert.Single(SessionsOf(daemon).Values).AuthState);
    }

    [Fact]
    public async Task StaleOldCallbacksCannotMutateTheReplacementOwnerAsync()
    {
        var oldStatus = IdleStatus(Guid.NewGuid().ToString());
        var oldClient = new FakeReconnectDaemonClient
        {
            StatusHandler = _ => Task.FromResult<DaemonStatus?>(oldStatus)
        };
        var newStatus = IdleStatus(Guid.NewGuid().ToString());
        var newClient = new FakeReconnectDaemonClient
        {
            StatusHandler = _ => Task.FromResult<DaemonStatus?>(newStatus)
        };
        var clients = new Queue<IDaemonClient>([oldClient, newClient]);
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-a" };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway,
            () => clients.Dequeue());
        var oldSession = await daemon.StartPersistentSessionAsync(
            PrefillPlatform.Steam,
            ScheduledPrefillConstants.DeriveSystemUserId());
        gateway.ResolvedImageId = "sha256:image-b";

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        var successor = Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
        Assert.NotEqual(oldSession.Id, successor.Id);
        Assert.Same(newClient, successor.Client);
        var challenge = ImageChallenge("stale-old-owner");
        await oldClient.RaiseCredentialChallengeAsync(challenge);
        await oldClient.RaiseStatusAsync(new DaemonStatus { Status = "logged-out" });
        await oldClient.DisconnectAsync();
        await daemon.StopPersistentSessionAsync(oldSession.Id, "stale-owner");

        Assert.Same(successor, Assert.Single(SessionsOf(daemon).Values));
        Assert.Same(newClient, successor.Client);
        Assert.Null(successor.PendingLoginChallenge);
        Assert.Equal(DaemonSessionStatus.Active, successor.Status);
    }

    [Theory]
    [InlineData(PrefillPlatform.Steam)]
    [InlineData(PrefillPlatform.Epic)]
    [InlineData(PrefillPlatform.Xbox)]
    [InlineData(PrefillPlatform.BattleNet)]
    [InlineData(PrefillPlatform.Riot)]
    public async Task ImagePullRetriesTransientFailureOnEveryPlatformAsync(PrefillPlatform platform)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        gateway.ImagePullFailures.Enqueue(new DockerApiException(HttpStatusCode.InternalServerError, "TLS handshake timeout"));
        using var daemon = BuildDaemon(platform, MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)), gateway);

        await PullAsync(daemon, CancellationToken.None);

        Assert.Equal(2, gateway.CountOf("CreateImage"));
        Assert.Equal(1, gateway.CountOf("InspectImage:"));
        Assert.Equal(0, gateway.DestructiveCallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImagePullUsesCachedImageOnlyAfterBoundedRetriesAsync(bool cached)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway { ImageExists = cached };
        for (var attempt = 0; attempt < 3; attempt++)
            gateway.ImagePullFailures.Enqueue(new DockerApiException(HttpStatusCode.InternalServerError, "TLS handshake timeout"));
        using var daemon = BuildDaemon(PrefillPlatform.Epic, MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)), gateway);

        if (cached) await PullAsync(daemon, CancellationToken.None);
        else await Assert.ThrowsAsync<DockerImageNotFoundException>(() => PullAsync(daemon, CancellationToken.None));

        Assert.Equal(3, gateway.CountOf("CreateImage"));
        Assert.Equal(1, gateway.CountOf("InspectImage:"));
        Assert.Equal(0, gateway.DestructiveCallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ImagePullDoesNotRetryPermanentFailuresAsync(HttpStatusCode status)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        gateway.ImagePullFailures.Enqueue(new DockerApiException(status, "Image is unavailable"));
        using var daemon = BuildDaemon(PrefillPlatform.Epic, MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)), gateway);

        await PullAsync(daemon, CancellationToken.None);

        Assert.Equal(1, gateway.CountOf("CreateImage"));
        Assert.Equal(1, gateway.CountOf("InspectImage:"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledImagePullDoesNotRetryOrFallBackAsync(bool cancelledBeforePull)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        using var cancel = new CancellationTokenSource();
        var gateway = new RecordingContainerGateway { OnImagePull = cancel.Cancel };
        using var daemon = BuildDaemon(PrefillPlatform.Epic, MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)), gateway);
        if (cancelledBeforePull) await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PullAsync(daemon, cancel.Token));

        Assert.Equal(cancelledBeforePull ? 0 : 1, gateway.CountOf("CreateImage"));
        Assert.Equal(0, gateway.CountOf("InspectImage:"));
    }

    private (
        TestSteamDaemon Daemon,
        RecordingContainerGateway Gateway,
        DaemonSession Session,
        DaemonStatus Status) NewImageScenario(
            IDaemonClient? client = null,
            bool authenticated = true,
            DaemonStatus? status = null,
            RecordingContainerGateway? gateway = null,
            PersistenceMode mode = PersistenceMode.FullPersistence,
            ISignalRNotificationService? notifications = null)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        status ??= IdleStatus(Guid.NewGuid().ToString());
        if (!authenticated)
        {
            status.Status = "awaiting-login";
        }
        client ??= new FakeReconnectDaemonClient();
        if (client is FakeReconnectDaemonClient reconnect && reconnect.StatusHandler is null)
        {
            reconnect.StatusHandler = _ => Task.FromResult<DaemonStatus?>(status);
        }
        if (client is ScriptedLoginDaemonClient scripted
            && scripted.StatusHandler is null
            && scripted.StatusOverride is null)
        {
            scripted.StatusOverride = status;
        }
        gateway ??= new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(mode, true), notifications: notifications),
            gateway);
        var session = InjectedPersistentSession(daemon, client);
        session.ContainerId = "container-a";
        session.ContainerName = SteamPersistentContainerName;
        session.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        session.ImageId = "sha256:image-a";
        session.Capabilities = status;
        session.LoginSettled = true;
        session.AuthState = authenticated ? DaemonAuthState.Authenticated : DaemonAuthState.NotAuthenticated;
        gateway.AddContainer(new FakeContainer
        {
            Id = session.ContainerId,
            Name = session.ContainerName,
            Running = true,
            ImageId = session.ImageId
        });
        return (daemon, gateway, session, status);
    }

    private static DaemonRun SettledRun(DaemonSession session, bool admissionPending)
    {
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new DaemonRun
        {
            PrefillRunId = runId,
            SessionId = session.Id,
            DaemonInstanceId = session.Capabilities!.DaemonInstanceId!,
            Options = new DaemonRunOptions(),
            Snapshot = new DaemonRunSnapshot
            {
                OperationId = runId.ToString(),
                DaemonInstanceId = session.Capabilities.DaemonInstanceId!,
                StartedAt = now,
                UpdatedAt = now,
                State = "completed"
            },
            TerminalCompletedFlag = 2,
            AdmissionPending = admissionPending,
            CompletedAtUtc = DateTime.UtcNow
        };
    }

    private static CredentialChallenge ImageChallenge(string challengeId) => new()
    {
        ChallengeId = challengeId,
        CredentialType = "username",
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddMinutes(5)
    };

    private static Task PullAsync(PrefillDaemonServiceBase daemon, CancellationToken cancellationToken) =>
        (Task)typeof(PrefillDaemonServiceBase).GetMethod("EnsureImageExistsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(daemon, [cancellationToken])!;

    private static DaemonStatus IdleStatus(string daemonInstanceId) => new()
    {
        ProtocolVersion = 2,
        DaemonInstanceId = daemonInstanceId,
        MaxConcurrentRuns = 1,
        MaxConcurrentRequests = 1,
        Features = ["concurrentPrefill", "operationProgress", "targetedCancel", "inlineSelection", "activeOperations"],
        ActiveOperations = [],
        RecentOperations = [],
        Status = "logged-in"
    };
}
