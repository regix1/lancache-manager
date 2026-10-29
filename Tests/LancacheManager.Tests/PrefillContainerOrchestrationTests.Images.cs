using System.Net;
using System.Reflection;
using System.Text.Json;
using Docker.DotNet;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
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

    // A pull that ends with no image to start from leaves nothing to wait on, so the next Start
    // pulls again instead of failing on the missing image for the rest of the check interval.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PullThatLeavesNoImageIsTriedAgainOnTheNextStartAsync(bool canceled)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);
        await PullAsync(daemon, CancellationToken.None);
        // The check interval since that pull has passed.
        typeof(PrefillDaemonServiceBase).GetField("_nextImageCheckAtUtc", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(daemon, DateTime.MinValue);
        using var cts = new CancellationTokenSource();
        if (canceled)
        {
            gateway.OnImagePull = () => cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PullAsync(daemon, cts.Token));
            gateway.OnImagePull = null;
        }
        else
        {
            gateway.ImageExists = false;
            gateway.ImagePullFailures.Enqueue(new InvalidOperationException("The registry refused the pull."));
            await Assert.ThrowsAsync<DockerImageNotFoundException>(() => PullAsync(daemon, CancellationToken.None));
            gateway.ImageExists = true;
        }

        await PullAsync(daemon, CancellationToken.None);

        Assert.Equal(3, gateway.ImagePulls.Count);
    }

    // A pull that fails while a cached image exists starts from that image and keeps the wait.
    [Fact]
    public async Task FailedPullWithCachedImageWaitsBeforePullingAgainAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);
        gateway.ImagePullFailures.Enqueue(new InvalidOperationException("The registry refused the pull."));

        await PullAsync(daemon, CancellationToken.None);
        await PullAsync(daemon, CancellationToken.None);

        Assert.Single(gateway.ImagePulls);
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

    // An image update ends the old session without a person, so an Orphaned row stays Orphaned and a
    // FullPersistence restart can still recreate from it. A person's Stop of the successor ends its row
    // as Terminated and erases the login in every mode.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task ImageUpdateLeavesOrphanedRowAndExplicitStopTerminatesAsync(PersistenceMode mode)
    {
        var (options, database) = NewDatabase();
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
        await sessions.CreateSessionAsync(
            session.Id, session.UserId, session.ContainerId, SteamPersistentContainerName, session.ExpiresAt, session.LastLoginAt, PrefillPlatform.Steam.ToString());
        await sessions.MarkOrphanedAsync(session.Id);

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        var successor = Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
        Assert.NotEqual(session.Id, successor.Id);
        Assert.Equal(PrefillSessionStatus.Orphaned, (await GetRowAsync(options, session.Id))!.Status);
        Assert.Equal(0, client.LogoutCount);

        await daemon.StopPersistentSessionAsync(successor.Id, "admin");

        var stopped = (await GetRowAsync(options, successor.Id))!;
        Assert.Equal(PrefillSessionStatus.Terminated, stopped.Status);
        Assert.Equal("admin", stopped.TerminatedBy);
        Assert.Equal(1, client.LogoutCount);
        Assert.Equal(PrefillSessionStatus.Orphaned, (await GetRowAsync(options, session.Id))!.Status);
    }

    // A manager restart after an image update created its new container, but before that container's
    // daemon registered (its connect failed, or the host stop canceled it), re-adopts the new container as
    // a reconnect with the stored expiry and keeps the login. A new daemon starts signed out, so the
    // re-adopt sends it one login from the saved login; a daemon that already reports signed in gets none.
    // KillOnRestart removes it at shutdown and closes its row.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, false, false)]
    [InlineData(PersistenceMode.KeepAcrossRestart, false, false)]
    [InlineData(PersistenceMode.FullPersistence, false, false)]
    [InlineData(PersistenceMode.KillOnRestart, true, false)]
    [InlineData(PersistenceMode.KeepAcrossRestart, true, false)]
    [InlineData(PersistenceMode.FullPersistence, true, false)]
    [InlineData(PersistenceMode.KillOnRestart, false, true)]
    [InlineData(PersistenceMode.KeepAcrossRestart, false, true)]
    [InlineData(PersistenceMode.FullPersistence, false, true)]
    public async Task RestartBeforeTheNewContainerRegistersAppliesTheModeAsync(PersistenceMode mode, bool hostStop, bool daemonSignedIn)
    {
        var connectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedClient = hostStop
            ? new FakeReconnectDaemonClient
            {
                ConnectHandler = async token =>
                {
                    connectEntered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
            }
            : new FakeReconnectDaemonClient
            {
                ConnectHandler = _ => Task.FromException(new IOException("simulated connect failure"))
            };
        var scenario = await NewPendingImageChangeAsync(mode, new Queue<IDaemonClient>([failedClient]));
        using var daemon = scenario.Daemon;
        using var stop = new CancellationTokenSource();
        var pass = daemon.ReconcilePersistentImageAsync(stop.Token);
        if (hostStop)
        {
            await connectEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await stop.CancelAsync();
        }
        Assert.NotNull(await Record.ExceptionAsync(() => pass));
        var successorId = Assert.Single(scenario.Gateway.CreatedParameters).Labels[SessionIdLabel];
        await daemon.StopAsync(CancellationToken.None);

        var restartGateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        if (scenario.Gateway.FindByName(SteamPersistentContainerName) is { } survivor)
        {
            restartGateway.AddContainer(survivor);
        }
        var restartClient = new ScriptedLoginDaemonClient(alreadyLoggedIn: daemonSignedIn);
        using var restarted = new TestSteamDaemon(
            MakeDeps(scenario.Database, scenario.Sessions, Config(mode, true)), restartGateway, () => restartClient);
        await restarted.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        if (restarted.LastHeadlessSelfAuthAttempt is { } attempt)
        {
            await attempt.WaitAsync(TimeSpan.FromSeconds(15));
        }

        var successorRow = await GetRowAsync(scenario.Options, successorId);
        Assert.NotNull(successorRow);
        if (mode == PersistenceMode.KillOnRestart)
        {
            Assert.Null(restarted.GetActivePersistentSession());
            Assert.Equal(PrefillSessionStatus.Terminated, successorRow.Status);
            Assert.Equal(0, restartClient.StartLoginCallCount);
            return;
        }
        var adopted = restarted.GetActivePersistentSession();
        Assert.Equal(successorId, adopted?.Id);
        Assert.Equal(0, restartClient.LogoutCount);
        Assert.Equal(daemonSignedIn ? 0 : 1, restartClient.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, adopted!.AuthState);
        // A due scheduled run reads this status before it decides whether a sign-in is needed.
        Assert.Equal("logged-in", (await restarted.GetSessionStatusAsync(adopted.Id))?.Status);
        Assert.Equal(scenario.Session.ExpiresAt, adopted.ExpiresAt);
    }

    // A person's Cancel of a sign-in, made before an image update whose new daemon never registered, is
    // stored on the new container's row. A manager restart then keeps that container signed out: the
    // re-adopt reads the mark from that row, and a FullPersistence recreate of a vanished container reads it
    // from the latest row. KillOnRestart ends the row at shutdown and adopts nothing.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, false)]
    [InlineData(PersistenceMode.KeepAcrossRestart, false)]
    [InlineData(PersistenceMode.FullPersistence, false)]
    [InlineData(PersistenceMode.KillOnRestart, true)]
    [InlineData(PersistenceMode.KeepAcrossRestart, true)]
    [InlineData(PersistenceMode.FullPersistence, true)]
    public async Task CancelBeforeAnImageUpdateKeepsTheNewContainerSignedOutAcrossARestartAsync(PersistenceMode mode, bool containerGone)
    {
        var failedClient = new FakeReconnectDaemonClient
        {
            ConnectHandler = _ => Task.FromException(new IOException("simulated connect failure"))
        };
        var scenario = await NewPendingImageChangeAsync(mode, new Queue<IDaemonClient>([failedClient]));
        using var daemon = scenario.Daemon;
        lock (scenario.Session.PrefillLock)
        {
            scenario.Session.LoginCanceledByPerson = true;
        }
        await scenario.Sessions.SetLoginStateAsync(scenario.Session.Id, true, false, false,
            scenario.Session.LastLoginAt, scenario.Session.ExpiresAt);
        await Assert.ThrowsAsync<IOException>(() => daemon.ReconcilePersistentImageAsync(CancellationToken.None));
        var successorId = Assert.Single(scenario.Gateway.CreatedParameters).Labels[SessionIdLabel];
        Assert.True((await GetRowAsync(scenario.Options, successorId))!.LoginCanceledByPerson);
        await daemon.StopAsync(CancellationToken.None);

        var restartGateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        if (!containerGone && scenario.Gateway.FindByName(SteamPersistentContainerName) is { } survivor)
        {
            restartGateway.AddContainer(survivor);
        }
        var restartClient = new ScriptedLoginDaemonClient();
        using var restarted = new TestSteamDaemon(
            MakeDeps(scenario.Database, scenario.Sessions, Config(mode, true)), restartGateway, () => restartClient);
        await restarted.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        if (restarted.LastHeadlessSelfAuthAttempt is { } attempt)
        {
            await attempt.WaitAsync(TimeSpan.FromSeconds(15));
        }

        var active = restarted.GetActivePersistentSession();
        Assert.Equal(0, restartClient.StartLoginCallCount);
        if (mode == PersistenceMode.KillOnRestart || (containerGone && mode == PersistenceMode.KeepAcrossRestart))
        {
            Assert.Null(active);
            return;
        }
        Assert.NotNull(active);
        Assert.Equal(containerGone, active.Id != successorId);
        Assert.True(active.LoginCanceledByPerson);
        Assert.Equal(DaemonAuthState.NotAuthenticated, active.AuthState);
        Assert.True((await GetRowAsync(scenario.Options, active.Id))!.LoginCanceledByPerson);
    }

    // A validity change saved while an image update waits on its new container moves the date the update
    // carries by the change in days, so the next pass registers the new container with the moved date,
    // whether it adopts the waiting container or creates another under the same id.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, false)]
    [InlineData(PersistenceMode.KeepAcrossRestart, false)]
    [InlineData(PersistenceMode.FullPersistence, false)]
    [InlineData(PersistenceMode.KillOnRestart, true)]
    [InlineData(PersistenceMode.KeepAcrossRestart, true)]
    [InlineData(PersistenceMode.FullPersistence, true)]
    public async Task ValidityChangeDuringAPendingImageUpdateMovesItsDateAsync(PersistenceMode mode, bool containerGone)
    {
        var failedClient = new FakeReconnectDaemonClient
        {
            ConnectHandler = _ => Task.FromException(new IOException("simulated connect failure"))
        };
        var scenario = await NewPendingImageChangeAsync(
            mode, new Queue<IDaemonClient>([failedClient, new ScriptedLoginDaemonClient()]));
        using var daemon = scenario.Daemon;
        var expected = scenario.Session.ExpiresAt.AddDays(-23);
        await Assert.ThrowsAsync<IOException>(() => daemon.ReconcilePersistentImageAsync(CancellationToken.None));
        var attemptId = Assert.Single(scenario.Gateway.CreatedParameters).Labels[SessionIdLabel];
        if (containerGone)
        {
            scenario.Gateway.FindByName(SteamPersistentContainerName)!.Running = false;
        }

        StateOf(daemon).SetAdminPersistentLoginValidityDays(7);
        await daemon.UpdatePersistentSessionExpiryAsync();
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt)
        {
            await attempt.WaitAsync(TimeSpan.FromSeconds(15));
        }

        var successor = daemon.GetActivePersistentSession();
        Assert.NotNull(successor);
        Assert.Equal(attemptId, successor.Id);
        Assert.Equal(containerGone ? 2 : 1, scenario.Gateway.CreatedParameters.Count);
        Assert.Equal(expected, successor.ExpiresAt);
        Assert.Equal(expected, (await GetRowAsync(scenario.Options, successor.Id))!.ExpiresAtUtc);
    }

    // A validity save while an image update's new daemon is still connecting waits for the update, and the new
    // container then reads its sign-in time plus the new days, in memory and in its row.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task AValiditySaveDuringAnImageUpdateIsKeptByTheNewContainerAsync(PersistenceMode mode)
    {
        var connectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var instance = Guid.NewGuid().ToString();
        var nextClient = new FakeReconnectDaemonClient
        {
            ConnectHandler = async token =>
            {
                connectEntered.TrySetResult();
                await releaseConnect.Task.WaitAsync(token);
            },
            StatusHandler = _ => Task.FromResult<DaemonStatus?>(IdleStatus(instance))
        };
        var scenario = await NewPendingImageChangeAsync(mode, new Queue<IDaemonClient>([nextClient]));
        using var daemon = scenario.Daemon;
        var controller = new PersistentPrefillController(
            new SingleDaemonServiceProvider(daemon), StateOf(daemon), new PrefillCacheService(scenario.Database, NullLogger<PrefillCacheService>.Instance),
            NullLogger<PersistentPrefillController>.Instance);
        var pass = daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        await connectEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var save = controller.SetValidityAsync(new PersistentLoginValidityDto { Days = 7 });
        releaseConnect.SetResult();
        await pass.WaitAsync(TimeSpan.FromSeconds(30));
        await save.WaitAsync(TimeSpan.FromSeconds(30));

        var successor = daemon.GetActivePersistentSession()!;
        Assert.NotEqual(scenario.Session.Id, successor.Id);
        Assert.Equal(scenario.Session.CreatedAt.AddDays(7), successor.ExpiresAt, TimeSpan.FromMinutes(1));
        Assert.Equal(successor.LastLoginAt.AddDays(7), successor.ExpiresAt);
        Assert.Equal(successor.ExpiresAt, (await GetRowAsync(scenario.Options, successor.Id))!.ExpiresAtUtc);
    }

    // A person's Stop while an image update waits on its new container ends that container's row as
    // Terminated, so the next Start erases the saved login in every mode.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task StopDuringPendingImageUpdateTerminatesTheNewContainersRowAsync(PersistenceMode mode)
    {
        var failedClient = new FakeReconnectDaemonClient
        {
            ConnectHandler = _ => Task.FromException(new IOException("simulated connect failure"))
        };
        var nextClient = new ScriptedLoginDaemonClient();
        var scenario = await NewPendingImageChangeAsync(mode, new Queue<IDaemonClient>([failedClient, nextClient]));
        using var daemon = scenario.Daemon;
        await Assert.ThrowsAsync<IOException>(() => daemon.ReconcilePersistentImageAsync(CancellationToken.None));
        var successorId = Assert.Single(scenario.Gateway.CreatedParameters).Labels[SessionIdLabel];

        await daemon.StopPersistentSessionAsync(scenario.Session.Id, "admin");

        var successorRow = await GetRowAsync(scenario.Options, successorId);
        Assert.NotNull(successorRow);
        Assert.Equal(PrefillSessionStatus.Terminated, successorRow.Status);
        Assert.Equal("admin", successorRow.TerminatedBy);
        await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt)
        {
            await attempt.WaitAsync(TimeSpan.FromSeconds(15));
        }
        Assert.Equal(1, nextClient.LogoutCount);
        Assert.Equal(0, nextClient.StartLoginCallCount);
    }

    // A new container whose daemon keeps refusing its socket is removed and created again by the next image
    // pass under the removed attempt's id, so the update keeps one row however many attempts fail, and that
    // row ends Active for the registered successor.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task RecreatedImageUpdateContainerReusesTheRemovedAttemptsRowAsync(PersistenceMode mode)
    {
        IDaemonClient Refusing() => new FakeReconnectDaemonClient
        {
            ConnectHandler = _ => Task.FromException(new IOException("simulated connect failure"))
        };
        var scenario = await NewPendingImageChangeAsync(
            mode, new Queue<IDaemonClient>([Refusing(), Refusing(), new FakeReconnectDaemonClient()]));
        using var daemon = scenario.Daemon;
        await Assert.ThrowsAsync<IOException>(() => daemon.ReconcilePersistentImageAsync(CancellationToken.None));
        var removedAttemptId = Assert.Single(scenario.Gateway.CreatedParameters).Labels[SessionIdLabel];

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        var successor = Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
        Assert.Equal(removedAttemptId, successor.Id);
        Assert.Equal(2, scenario.Gateway.CountOf($"Create:{SteamPersistentContainerName}"));
        await using var context = new AppDbContext(scenario.Options);
        var updateRow = Assert.Single(await context.PrefillSessions.AsNoTracking()
            .Where(row => row.IsPersistent && row.SessionId != scenario.Session.Id)
            .ToListAsync());
        Assert.Equal(successor.Id, updateRow.SessionId);
        Assert.Equal(PrefillSessionStatus.Active, updateRow.Status);
    }

    // A person's Stop of an image update's new container that had already started ends that daemon's life.
    // The next attempt takes a new id, so a page still showing a sign-in for the stopped id drops it, and a
    // new row, so a person's cancel stored on the stopped attempt's row does not keep the new one signed out.
    [Fact]
    public async Task AttemptAfterAStoppedStartedAttemptTakesANewIdAndRowAsync()
    {
        using var cancel = new CancellationTokenSource();
        var canceledRead = new FakeReconnectDaemonClient
        {
            StatusHandler = async token =>
            {
                await cancel.CancelAsync();
                token.ThrowIfCancellationRequested();
                return null;
            }
        };
        var nextStatus = IdleStatus(Guid.NewGuid().ToString());
        var next = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(nextStatus) };
        var scenario = await NewPendingImageChangeAsync(
            PersistenceMode.FullPersistence, new Queue<IDaemonClient>([canceledRead, next]));
        using var daemon = scenario.Daemon;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => daemon.ReconcilePersistentImageAsync(cancel.Token));
        var stopped = Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
        Assert.NotEqual(scenario.Session.Id, stopped.Id);
        await scenario.Sessions.SetLoginStateAsync(stopped.Id, true, false, false, stopped.LastLoginAt, stopped.ExpiresAt);
        await daemon.StopPersistentSessionAsync(stopped.Id, "admin");

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        var successor = Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
        Assert.False((await GetRowAsync(scenario.Options, successor.Id))!.LoginCanceledByPerson);
        Assert.NotEqual(stopped.Id, successor.Id);
        Assert.Equal(PrefillSessionStatus.Terminated, (await GetRowAsync(scenario.Options, stopped.Id))!.Status);
    }

    // A scheduled run during an image update whose new image exits as soon as it starts fails with the key
    // that names that cause, not the generic daemon failure.
    [Fact]
    public async Task ScheduledAdopt_DuringAnUpdateWhoseImageExitsOnStart_FailsWithTheExitKeyAsync()
    {
        var scenario = await NewPendingImageChangeAsync(
            PersistenceMode.KeepAcrossRestart, new Queue<IDaemonClient>([new FakeReconnectDaemonClient()]));
        using var daemon = scenario.Daemon;
        scenario.Gateway.ExitOnStart = true;
        Assert.NotNull(await Record.ExceptionAsync(() => daemon.ReconcilePersistentImageAsync(CancellationToken.None)));

        var failure = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => daemon.AdoptRunningPersistentSessionAsync(CancellationToken.None));

        Assert.Equal("errors.prefill.containerExitedOnStart", failure.StageKey);
        Assert.True(daemon.HasPersistentImageWork);
    }

    // A request canceled while an image update creates the new container leaves the old session's row
    // Orphaned rather than Active, since its container is gone; a person's Stop still ends that row.
    [Fact]
    public async Task CanceledImageUpdateResume_LeavesTheOldRowOrphanedAndStopEndsItAsync()
    {
        var scenario = await NewPendingImageChangeAsync(
            PersistenceMode.FullPersistence, new Queue<IDaemonClient>([new FakeReconnectDaemonClient()]));
        using var daemon = scenario.Daemon;
        scenario.Gateway.HoldStartContainer = true;
        using var cancel = new CancellationTokenSource();

        var pass = daemon.ReconcilePersistentImageAsync(cancel.Token);
        await scenario.Gateway.StartContainerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);

        Assert.Equal(PrefillSessionStatus.Orphaned, (await GetRowAsync(scenario.Options, scenario.Session.Id))!.Status);

        scenario.Gateway.HoldStartContainer = false;
        await daemon.StopPersistentSessionAsync(scenario.Session.Id, "admin");

        Assert.Equal(PrefillSessionStatus.Terminated, (await GetRowAsync(scenario.Options, scenario.Session.Id))!.Status);
    }

    // A Stop retried after a pending image update's Stop failed finds that session, so the Stop route
    // runs the cancel again instead of answering that no session exists.
    [Fact]
    public async Task FailedTeardownLookup_DuringAPendingImageUpdate_ReturnsItsSessionAsync()
    {
        IDaemonClient Refusing() => new FakeReconnectDaemonClient
        {
            ConnectHandler = _ => Task.FromException(new IOException("simulated connect failure"))
        };
        var scenario = await NewPendingImageChangeAsync(
            PersistenceMode.KeepAcrossRestart, new Queue<IDaemonClient>([Refusing()]));
        using var daemon = scenario.Daemon;
        await Assert.ThrowsAsync<IOException>(() => daemon.ReconcilePersistentImageAsync(CancellationToken.None));
        Assert.Null(daemon.GetSession(scenario.Session.Id));

        Assert.Same(scenario.Session, daemon.GetFailedTeardownSession(scenario.Session.Id));
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

        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => daemon.ReconcilePersistentImageAsync(CancellationToken.None));
        Assert.True(daemon.HasPersistentImageWork);

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Equal(2, gateway.CountOf("Remove:container-a"));
        Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
    }

    // A person's Stop on a container whose image update could not remove it finishes that removal. While
    // Docker still fails, the Stop answers the removal failure and the next image pass removes the old
    // container without creating a new one; with Docker working, the Stop itself closes the old session.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopAfterFailedImageTeardownFinishesTheRemovalAsync(bool removalFailsAtStop)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)(object)notifications;
        var status = IdleStatus(Guid.NewGuid().ToString());
        var client = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        var deps = MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true), notifications: notifications);
        using var daemon = new TestSteamDaemon(deps, gateway);
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
        await sessions.CreateSessionAsync(
            session.Id, session.UserId, session.ContainerId, SteamPersistentContainerName, session.ExpiresAt, DateTime.UtcNow);
        gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));
        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => daemon.ReconcilePersistentImageAsync(CancellationToken.None));
        if (removalFailsAtStop)
            gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));
        var controller = new PersistentPrefillController(
            new SingleDaemonServiceProvider(daemon), deps.StateService, deps.CacheService,
            NullLogger<PersistentPrefillController>.Instance);
        var middleware = new GlobalExceptionMiddleware(
            async _ => await controller.StopAsync(new StopPersistentSessionRequest { SessionId = session.Id }),
            NullLogger<GlobalExceptionMiddleware>.Instance,
            DispatchProxy.Create<IHostEnvironment, NullReturningProxy>());
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context);

        if (removalFailsAtStop)
        {
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
            using var answer = JsonDocument.Parse(body.ToArray());
            Assert.Equal("errors.prefill.containerRemovalFailed", answer.RootElement.GetProperty("stageKey").GetString());
            Assert.True(gateway.ContainsContainer(session.ContainerId));
            // The person's Stop is recorded before Docker answers, so a manager restart does not bring the
            // container back from the saved login.
            var restartedGateway = new RecordingContainerGateway();
            using (var restarted = new TestSteamDaemon(
                MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)), restartedGateway,
                () => new ScriptedLoginDaemonClient()))
            {
                await restarted.StartAsync(CancellationToken.None);
                Assert.Equal(0, restartedGateway.CountOf("Create:"));
                Assert.Null(restarted.GetActivePersistentSession());
            }
            var stopped = (await sessions.GetSessionAsync(session.Id))!;
            Assert.Equal(PrefillSessionStatus.Terminated, stopped.Status);
            Assert.Equal("admin", stopped.TerminatedBy);
            await daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        }
        else
        {
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }
        Assert.False(gateway.ContainsContainer(session.ContainerId));
        Assert.False(daemon.HasPersistentImageWork);
        Assert.Equal(0, gateway.CountOf("Create:"));
        var row = (await sessions.GetSessionAsync(session.Id))!;
        Assert.Equal(PrefillSessionStatus.Terminated, row.Status);
        Assert.Equal("admin", row.TerminatedBy);
        Assert.Null(daemon.GetFailedTeardownSession(session.Id));
        Assert.True(client.Disposed);
        Assert.Single(recorder.EventNames, name => name == SignalREvents.DaemonSessionTerminated);
    }

    // A Stop that removes the old container of a failed image update, while Docker does not answer the
    // login volume removal, answers that Docker is unavailable rather than a canceled request.
    [Fact]
    public async Task StopAfterFailedImageTeardownVolumeRemovalTimesOutAnswersDockerUnavailableAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var status = IdleStatus(Guid.NewGuid().ToString());
        var client = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        var deps = MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true));
        using var daemon = new TestSteamDaemon(deps, gateway);
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
        await sessions.CreateSessionAsync(
            session.Id, session.UserId, session.ContainerId, SteamPersistentContainerName, session.ExpiresAt, DateTime.UtcNow);
        gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));
        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => daemon.ReconcilePersistentImageAsync(CancellationToken.None));
        gateway.HoldRemoveVolume = true;
        var controller = new PersistentPrefillController(
            new SingleDaemonServiceProvider(daemon), deps.StateService, deps.CacheService,
            NullLogger<PersistentPrefillController>.Instance);
        var middleware = new GlobalExceptionMiddleware(
            async _ => await controller.StopAsync(new StopPersistentSessionRequest { SessionId = session.Id }),
            NullLogger<GlobalExceptionMiddleware>.Instance,
            DispatchProxy.Create<IHostEnvironment, NullReturningProxy>());
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        using var answer = JsonDocument.Parse(body.ToArray());
        Assert.Equal("errors.prefill.dockerUnavailable", answer.RootElement.GetProperty("stageKey").GetString());
        Assert.False(gateway.ContainsContainer(session.ContainerId));
        Assert.Equal(PrefillSessionStatus.Terminated, (await sessions.GetSessionAsync(session.Id))!.Status);
    }

    // A shutdown that cannot finish a pending image update still stops every other session's container.
    [Fact]
    public async Task KillOnRestartShutdownStopsGuestSessionsWhenThePendingImageCancelFailsAsync()
    {
        var (options, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var status = IdleStatus(Guid.NewGuid().ToString());
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.KillOnRestart, true)),
            gateway);
        var persistent = InjectedPersistentSession(
            daemon, new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) });
        persistent.ContainerId = "container-a";
        persistent.ContainerName = SteamPersistentContainerName;
        persistent.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        persistent.ImageId = "sha256:image-a";
        persistent.Capabilities = status;
        persistent.LoginSettled = true;
        persistent.AuthState = DaemonAuthState.Authenticated;
        gateway.AddContainer(new FakeContainer
        {
            Id = persistent.ContainerId,
            Name = persistent.ContainerName,
            Running = true,
            ImageId = persistent.ImageId
        });
        gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));
        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => daemon.ReconcilePersistentImageAsync(CancellationToken.None));
        var guestId = Guid.NewGuid().ToString("N")[..16];
        var guestUser = Guid.NewGuid();
        await sessions.CreateSessionAsync(guestId, guestUser, "guest-container", "guest-container", DateTime.UtcNow.AddHours(1), DateTime.UtcNow);
        daemon.InjectSession(new DaemonSession
        {
            Id = guestId,
            UserId = guestUser,
            Status = DaemonSessionStatus.Active,
            ContainerId = "guest-container",
            ContainerName = "guest-container",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Client = new ScriptedLoginDaemonClient()
        });
        gateway.AddContainer(new FakeContainer { Id = "guest-container", Name = "guest-container", Running = true });
        gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));

        await daemon.StopAsync(CancellationToken.None);

        Assert.False(gateway.ContainsContainer("guest-container"));
        Assert.Equal(PrefillSessionStatus.Terminated, (await GetRowAsync(options, guestId))!.Status);
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
        var (options, database) = NewDatabase();
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
        await using var context = new AppDbContext(options);
        var row = Assert.Single(await context.PrefillSessions.AsNoTracking()
            .Where(current => current.SessionId == successor.Id)
            .ToListAsync());
        Assert.Equal(PrefillSessionStatus.Active, row.Status);
    }

    [Theory]
    [InlineData("admission-pending")]
    [InlineData("terminal-publication")]
    public async Task LocalWorkOwnershipDefersReplacementUntilSettledAsync(string ownership)
    {
        var scenario = NewImageScenario();
        using var daemon = scenario.Daemon;
        var ownedRun = SettledRun(scenario.Session, admissionPending: ownership == "admission-pending");
        if (ownership == "terminal-publication")
            ownedRun.TerminalCompletedFlag = 1;
        scenario.Session.Runs.TryAdd(ownedRun.PrefillRunId, ownedRun);

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Equal(0, scenario.Gateway.DestructiveCallCount);
        Assert.False(scenario.Session.AdmissionClosed);

        foreach (var run in scenario.Session.Runs.Values)
        {
            run.AdmissionPending = false;
            run.TerminalCompletedFlag = 2;
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
    public async Task AHeadlessSignInFromTheStoredLoginLetsTheImageUpdateApplyAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        var scenario = NewImageScenario(client, authenticated: false);
        using var daemon = scenario.Daemon;
        var daemonInstanceId = scenario.Status.DaemonInstanceId!;
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(
            client.StartLoginCallCount > 0 ? IdleStatus(daemonInstanceId) : scenario.Status);

        await daemon.AttemptHeadlessPersistentSelfAuthAsync(scenario.Session);

        Assert.Equal(DaemonAuthState.Authenticated, scenario.Session.AuthState);
        Assert.True(scenario.Session.LoginSettled);
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Single(scenario.Gateway.Removals, removal => removal.Id == "container-a");
    }

    [Fact]
    public async Task AnImageUpdateWaitsForASignInCancelTheDaemonHasNotAnsweredAsync()
    {
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: ImageChallenge("cancel-wait"))
        {
            HoldCancelLogin = true
        };
        var scenario = NewImageScenario(client, authenticated: false);
        using var daemon = scenario.Daemon;
        var daemonInstanceId = scenario.Status.DaemonInstanceId!;
        client.StatusOverride = scenario.Status;

        var first = await daemon.StartLoginAsync(scenario.Session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        var cancel = daemon.CancelLoginAsync(scenario.Session.Id, CancellationToken.None, first!.LoginAttempt);
        await client.CancelLoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        client.StatusOverride = IdleStatus(daemonInstanceId);
        client.LoginFinished = true;
        await daemon.GetSessionStatusAsync(scenario.Session.Id);
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Equal(0, scenario.Gateway.DestructiveCallCount);

        client.ReleaseCancelLogin.SetResult();
        Assert.True(await cancel);
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
            }
        };
        var scenario = NewImageScenario(client, status: status);
        using var daemon = scenario.Daemon;

        var replacement = daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<DaemonCommandException>(
            () => daemon.PrefillAsync(scenario.Session.Id, force: true, appIds: ["10"]));

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
        await using (await daemon.PersistentChangeGate.EnterChangeAsync(CancellationToken.None))
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

    // A pull that Docker gave up on after its silence limit already waited that whole limit, so it is not
    // tried again: the cached image is used at once.
    [Fact]
    public async Task ImagePullThatDockerGaveUpOnUsesTheCachedImageWithoutRetryAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        for (var attempt = 0; attempt < 3; attempt++)
            gateway.ImagePullFailures.Enqueue(RecordingContainerGateway.NoAnswer());
        using var daemon = BuildDaemon(PrefillPlatform.Steam, MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)), gateway);

        await PullAsync(daemon, CancellationToken.None);

        Assert.Equal(1, gateway.CountOf("CreateImage"));
        Assert.Equal(1, gateway.CountOf("InspectImage:"));
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

    private const string SessionIdLabel = "lancache.prefill.sessionId";

    // A signed-in persistent session on image A with its Active row, whose next image pass replaces it with
    // image B. Each new daemon client comes from the queue in order.
    private async Task<(
        TestSteamDaemon Daemon,
        RecordingContainerGateway Gateway,
        DaemonSession Session,
        DbContextOptions<AppDbContext> Options,
        IDbContextFactory<AppDbContext> Database,
        PrefillSessionService Sessions)> NewPendingImageChangeAsync(PersistenceMode mode, Queue<IDaemonClient> clients)
    {
        var (options, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var status = IdleStatus(Guid.NewGuid().ToString());
        var oldClient = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(mode, true)),
            gateway,
            () => clients.Dequeue());
        var session = InjectedPersistentSession(daemon, oldClient);
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
        await sessions.CreateSessionAsync(
            session.Id, session.UserId, session.ContainerId, SteamPersistentContainerName, session.ExpiresAt, session.LastLoginAt, PrefillPlatform.Steam.ToString());
        return (daemon, gateway, session, options, database, sessions);
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
