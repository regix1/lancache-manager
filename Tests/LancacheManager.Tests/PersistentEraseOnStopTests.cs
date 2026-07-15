using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// Erase-on-stop backend: proves the two best-effort logout guards added to
/// <see cref="PrefillDaemonServiceBase"/> so a persistent container's login never outlives the
/// container per the user's "login exists ONLY while the container is alive" policy.
/// (1) <see cref="PrefillDaemonServiceBase.TerminateSessionAsync"/> sends a logout for a PERSISTENT
/// session BEFORE tearing its container down (covers explicit Stop, Error-state replacement, and
/// service shutdown - they all funnel through this one method), and never for a non-persistent
/// session. (2) <see cref="PrefillDaemonServiceBase.ApplyFreshPersistentLoginGuardAsync"/> is the
/// belt-and-braces guard called right after a persistent session's socket connects: it fires only for
/// a NEWLY CREATED persistent session (isReconnect:false), never for a re-adopted one
/// (isReconnect:true) or a non-persistent one - an adopted running container's login IS its current
/// life and must be preserved. Both failure paths (daemon reports failure / round-trip throws) must
/// be swallowed so a dead socket can never block teardown or startup.
/// Mirrors the exact fake-<see cref="IDaemonClient"/> + <c>TestableSteamDaemonService</c> harness in
/// the sibling PersistentLogoutTests.cs.
/// </summary>
public class PersistentEraseOnStopTests
{
    [Fact]
    public async Task TerminateSessionAsync_PersistentSession_SendsLogoutBeforeTeardown()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: true);

        await daemon.TerminateSessionAsync(session.Id, "test stop");

        Assert.Equal(1, client.LogoutCallCount);
        Assert.Equal(DaemonSessionStatus.Terminated, session.Status);
        Assert.Null(daemon.GetSession(session.Id));
    }

    [Fact]
    public async Task TerminateSessionAsync_NonPersistentSession_NeverSendsLogout()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: false);

        await daemon.TerminateSessionAsync(session.Id, "test stop");

        Assert.Equal(0, client.LogoutCallCount);
        Assert.Equal(DaemonSessionStatus.Terminated, session.Status);
    }

    [Fact]
    public async Task TerminateSessionAsync_PersistentSession_LogoutRoundTripThrows_TerminationStillCompletes()
    {
        var client = new ThrowingLogoutDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: true);

        // Must not throw and must not block: a dead/hung socket cannot be allowed to prevent the stop.
        await daemon.TerminateSessionAsync(session.Id, "test stop");

        Assert.Equal(DaemonSessionStatus.Terminated, session.Status);
        Assert.Null(daemon.GetSession(session.Id));
    }

    [Fact]
    public async Task TerminateSessionAsync_PersistentSession_DaemonReportsLogoutFailure_TerminationStillCompletes()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: false);
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: true);

        await daemon.TerminateSessionAsync(session.Id, "test stop");

        Assert.Equal(1, client.LogoutCallCount);
        Assert.Equal(DaemonSessionStatus.Terminated, session.Status);
    }

    [Fact]
    public async Task ApplyFreshPersistentLoginGuardAsync_FreshPersistentCreate_SendsLogout()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: true);

        await daemon.ApplyFreshPersistentLoginGuardAsync(session, isPersistent: true, isReconnect: false);

        Assert.Equal(1, client.LogoutCallCount);
    }

    [Fact]
    public async Task ApplyFreshPersistentLoginGuardAsync_ReAdoptedPersistentSession_NeverSendsLogout()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: true);

        await daemon.ApplyFreshPersistentLoginGuardAsync(session, isPersistent: true, isReconnect: true);

        Assert.Equal(0, client.LogoutCallCount);
    }

    [Fact]
    public async Task ApplyFreshPersistentLoginGuardAsync_NonPersistentCreate_NeverSendsLogout()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: false);

        await daemon.ApplyFreshPersistentLoginGuardAsync(session, isPersistent: false, isReconnect: false);

        Assert.Equal(0, client.LogoutCallCount);
    }

    [Fact]
    public async Task ApplyFreshPersistentLoginGuardAsync_LogoutThrows_NeverPropagates()
    {
        var client = new ThrowingLogoutDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: true);

        // A dead socket on a fresh container must never block session creation from returning.
        await daemon.ApplyFreshPersistentLoginGuardAsync(session, isPersistent: true, isReconnect: false);
    }

    [Fact]
    public async Task TerminateSessionAsync_DaemonRejectsPreLoginLogout_LogsDistinctFromGenericFailure()
    {
        // Older daemon image: its pre-login command gate rejects "logout" outright while the session
        // hasn't finished authenticating (erase-on-stop regression diagnosis) - this must be logged as
        // "nothing to log out" rather than "daemon reported failure", so an admin cancelling a
        // mid-challenge login doesn't see what looks like a real daemon fault every time.
        var client = new PreLoginRejectionDaemonClient();
        var logger = new CapturingLogger();
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: true, logger: logger);

        await daemon.TerminateSessionAsync(session.Id, "test stop");

        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("declined logout") &&
            entry.Message.Contains("nothing to log out"));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("daemon reported failure"));
    }

    // === Mode-aware StopAsync (restart persistence) ===
    // These drive the IHostedService shutdown entry point (StopAsync) with a config-returning state
    // service, unlike the TerminateSessionAsync cases above which exercise the shared teardown funnel
    // directly. The old NullReturningProxy state service is reused only for the null-config case.

    [Fact]
    public async Task StopAsync_GuestSession_TerminatesAsToday()
    {
        // A guest session is never persistent, so shutdown terminates it regardless of the global mode.
        var client = new RecordingDaemonClient();
        var (daemon, session, sessionService, notifications) =
            CreateModeAwareDaemon(client, isPersistent: false, CreateConfigStateService(PersistenceMode.FullPersistence));
        await SeedSessionRowAsync(sessionService, session);

        await daemon.StopAsync(CancellationToken.None);

        Assert.Null(daemon.GetSession(session.Id));
        Assert.Equal(0, client.LogoutCallCount); // non-persistent sessions never log out
        Assert.Equal(PrefillSessionStatus.Terminated, (await sessionService.GetSessionAsync(session.Id))!.Status);
        Assert.True(notifications.InvocationCount > 0); // termination is broadcast
    }

    [Fact]
    public async Task StopAsync_PersistentKillOnRestart_TerminatesAndErases()
    {
        // Pinned to KillOnRestart: today's behavior (terminate + erase) is now mode 1, asserted explicitly.
        var client = new RecordingDaemonClient();
        var (daemon, session, sessionService, notifications) =
            CreateModeAwareDaemon(client, isPersistent: true, CreateConfigStateService(PersistenceMode.KillOnRestart));
        await SeedSessionRowAsync(sessionService, session);

        await daemon.StopAsync(CancellationToken.None);

        Assert.Null(daemon.GetSession(session.Id));
        Assert.Equal(1, client.LogoutCallCount); // erase-on-stop: logout attempted before teardown
        Assert.Equal(PrefillSessionStatus.Terminated, (await sessionService.GetSessionAsync(session.Id))!.Status);
        Assert.True(notifications.InvocationCount > 0); // termination is broadcast
    }

    [Theory]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task StopAsync_PersistentDetachMode_DetachesWithoutTerminating(PersistenceMode mode)
    {
        var client = new RecordingDaemonClient();
        var (daemon, session, sessionService, notifications) =
            CreateModeAwareDaemon(client, isPersistent: true, CreateConfigStateService(mode));
        await SeedSessionRowAsync(sessionService, session);
        var cts = session.CancellationTokenSource;

        await daemon.StopAsync(CancellationToken.None);

        // Detach path: dropped from _sessions, NO logout, NO broadcast, DB row LEFT Active for re-adopt,
        // client + CTS disposed. The container is never stopped/removed (no Docker in this harness).
        Assert.Null(daemon.GetSession(session.Id));
        Assert.Equal(0, client.LogoutCallCount);
        Assert.True(client.Disposed);
        Assert.Equal(0, notifications.InvocationCount);
        Assert.Equal(PrefillSessionStatus.Active, (await sessionService.GetSessionAsync(session.Id))!.Status);
        Assert.Throws<ObjectDisposedException>(() => _ = cts.Token); // CTS was disposed
    }

    [Fact]
    public async Task StopAsync_NullConfig_TerminatesAsKillOnRestartWithWarning()
    {
        // Should-not-happen path (config is required + Validate-guaranteed non-null in production): a null
        // config during shutdown falls back to terminate + erase (mode 1) with a LogWarning, rather than
        // throwing (which would abort teardown of the remaining sessions) or silently leaving it running.
        var client = new RecordingDaemonClient();
        var logger = new CapturingLogger();
        var (daemon, session, sessionService, _) =
            CreateModeAwareDaemon(client, isPersistent: true, CreateNullStateService(), logger);
        await SeedSessionRowAsync(sessionService, session);

        await daemon.StopAsync(CancellationToken.None);

        Assert.Null(daemon.GetSession(session.Id));
        Assert.Equal(1, client.LogoutCallCount); // mode-1 fallback still erases
        Assert.Equal(PrefillSessionStatus.Terminated, (await sessionService.GetSessionAsync(session.Id))!.Status);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("config unavailable"));
    }

    // === Mode-aware fresh-login guard: FullPersistence preserves a saved volume login ONLY across a
    // VERIFIED involuntary death; an explicit admin stop (Terminated row) or no evidence still erases ===

    [Fact]
    public async Task ApplyFreshPersistentLoginGuardAsync_FullPersistence_InvoluntaryRecreateFlag_SkipsLogout()
    {
        var client = new RecordingDaemonClient();
        var (daemon, session, _, _) =
            CreateModeAwareDaemon(client, isPersistent: true, CreateConfigStateService(PersistenceMode.FullPersistence));

        await daemon.ApplyFreshPersistentLoginGuardAsync(session, isPersistent: true, isReconnect: false, involuntaryRecreate: true);

        Assert.Equal(0, client.LogoutCallCount); // explicit involuntary recreate preserves the volume login
    }

    [Theory]
    [InlineData(PrefillSessionStatus.Active)]
    [InlineData(PrefillSessionStatus.Orphaned)]
    public async Task ApplyFreshPersistentLoginGuardAsync_FullPersistence_PriorRowNotTerminated_SkipsLogout(PrefillSessionStatus priorStatus)
    {
        var client = new RecordingDaemonClient();
        var (daemon, session, sessionService, _) =
            CreateModeAwareDaemon(client, isPersistent: true, CreateConfigStateService(PersistenceMode.FullPersistence));
        await SeedPriorPersistentRowAsync(sessionService, priorStatus);

        await daemon.ApplyFreshPersistentLoginGuardAsync(session, isPersistent: true, isReconnect: false);

        Assert.Equal(0, client.LogoutCallCount); // last life ended involuntarily -> preserve the login
    }

    [Fact]
    public async Task ApplyFreshPersistentLoginGuardAsync_FullPersistence_PriorRowTerminated_SendsLogout()
    {
        // An explicit admin stop flips the row to Terminated; a later FullPersistence Start must NOT
        // resurrect the login, so the erase backstop runs.
        var client = new RecordingDaemonClient();
        var (daemon, session, sessionService, _) =
            CreateModeAwareDaemon(client, isPersistent: true, CreateConfigStateService(PersistenceMode.FullPersistence));
        await SeedPriorPersistentRowAsync(sessionService, PrefillSessionStatus.Terminated);

        await daemon.ApplyFreshPersistentLoginGuardAsync(session, isPersistent: true, isReconnect: false);

        Assert.Equal(1, client.LogoutCallCount);
    }

    [Fact]
    public async Task ApplyFreshPersistentLoginGuardAsync_FullPersistence_NoPriorRow_SendsLogout()
    {
        var client = new RecordingDaemonClient();
        var (daemon, session, _, _) =
            CreateModeAwareDaemon(client, isPersistent: true, CreateConfigStateService(PersistenceMode.FullPersistence));

        await daemon.ApplyFreshPersistentLoginGuardAsync(session, isPersistent: true, isReconnect: false);

        Assert.Equal(1, client.LogoutCallCount); // no evidence of involuntary death -> safe-default erase
    }

    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    public async Task ApplyFreshPersistentLoginGuardAsync_NonFullPersistenceMode_SendsLogout(PersistenceMode mode)
    {
        var client = new RecordingDaemonClient();
        var (daemon, session, sessionService, _) =
            CreateModeAwareDaemon(client, isPersistent: true, CreateConfigStateService(mode));
        await SeedPriorPersistentRowAsync(sessionService, PrefillSessionStatus.Orphaned);

        await daemon.ApplyFreshPersistentLoginGuardAsync(session, isPersistent: true, isReconnect: false);

        Assert.Equal(1, client.LogoutCallCount); // only FullPersistence ever preserves; others always erase
    }

    // === Per-service override wins over the global mode, both directions ===

    [Fact]
    public async Task StopAsync_GlobalKill_SteamOverrideKeep_Detaches()
    {
        var client = new RecordingDaemonClient();
        var (daemon, session, sessionService, notifications) = CreateModeAwareDaemon(
            client, isPersistent: true, CreateConfigStateService(ConfigWithModes(PersistenceMode.KillOnRestart, PersistenceMode.KeepAcrossRestart)));
        await SeedSessionRowAsync(sessionService, session);

        await daemon.StopAsync(CancellationToken.None);

        Assert.Null(daemon.GetSession(session.Id));
        Assert.Equal(0, client.LogoutCallCount); // effective = Keep (override wins) -> detach
        Assert.Equal(PrefillSessionStatus.Active, (await sessionService.GetSessionAsync(session.Id))!.Status);
        Assert.Equal(0, notifications.InvocationCount);
    }

    [Fact]
    public async Task StopAsync_GlobalKeep_SteamOverrideKill_Terminates()
    {
        var client = new RecordingDaemonClient();
        var (daemon, session, sessionService, notifications) = CreateModeAwareDaemon(
            client, isPersistent: true, CreateConfigStateService(ConfigWithModes(PersistenceMode.KeepAcrossRestart, PersistenceMode.KillOnRestart)));
        await SeedSessionRowAsync(sessionService, session);

        await daemon.StopAsync(CancellationToken.None);

        Assert.Null(daemon.GetSession(session.Id));
        Assert.Equal(1, client.LogoutCallCount); // effective = Kill (override wins) -> terminate + erase
        Assert.Equal(PrefillSessionStatus.Terminated, (await sessionService.GetSessionAsync(session.Id))!.Status);
        Assert.True(notifications.InvocationCount > 0);
    }

    // === Post-detach event guard: a fire-and-forget status event landing after detach fires no broadcast ===

    [Fact]
    public async Task OnStatusChangeAsync_AfterDetach_IsIgnored_FiresNoBroadcast()
    {
        var client = new RecordingDaemonClient();
        var (daemon, session, _, notifications) =
            CreateModeAwareDaemon(client, isPersistent: true, CreateConfigStateService(PersistenceMode.KeepAcrossRestart));

        await daemon.StopAsync(CancellationToken.None); // detaches: the session is removed from _sessions
        Assert.Null(daemon.GetSession(session.Id));

        // Without the guard this authenticated-status event would broadcast an auth-state change.
        await InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session, new DaemonStatus { Status = "logged-in" });

        Assert.Equal(0, notifications.InvocationCount);
    }

    // === Shutdown creation-gate teardown: a create rejected at the gate leaves nothing running behind ===

    [Fact]
    public async Task TearDownRejectedCreateAsync_RemovesSessionDirectory_AndDisposesClientAndCts()
    {
        var (daemon, _, _, _) =
            CreateModeAwareDaemon(new RecordingDaemonClient(), isPersistent: true, CreateConfigStateService(PersistenceMode.KeepAcrossRestart));

        // Build the rejected create's session with the real command/response bind-mount directory the
        // create would have made (CommandsDir is init-only). It is deliberately NOT in _sessions - the
        // gate rejects before registration, and the teardown operates on the passed session directly.
        var sessionDir = Path.Combine(Path.GetTempPath(), $"prefill_rejected_{Guid.NewGuid():N}");
        var commandsDir = Path.Combine(sessionDir, "commands");
        Directory.CreateDirectory(commandsDir);
        var client = new RecordingDaemonClient();
        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
            Status = DaemonSessionStatus.Active,
            IsPersistent = true,
            AuthState = DaemonAuthState.NotAuthenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            CommandsDir = commandsDir,
            Client = client
        };
        var cts = session.CancellationTokenSource;

        await daemon.TearDownRejectedCreateAsync(session);

        Assert.False(Directory.Exists(sessionDir)); // bind-mount directory removed
        Assert.True(client.Disposed);               // client disposed
        Assert.Throws<ObjectDisposedException>(() => _ = cts.Token); // CTS disposed
        // No DB row exists at the rejection point (the create inserts it only after registration), so
        // there is nothing to flip to Terminated. This case runs with no container gateway; the
        // container-remove leg is exercised by TearDownRejectedCreateAsync_WithContainer_* below.
    }

    // === Docker-spy assertions: detach performs zero container operations; terminate + rejected-create
    //     teardown remove the container through the gateway (proving the detach zero-count is meaningful) ===

    [Fact]
    public async Task StopAsync_PersistentDetachMode_PerformsNoContainerDockerOps()
    {
        var gateway = new RecordingContainerGateway(); // available: a destructive op WOULD be recorded
        var client = new RecordingDaemonClient();
        var (daemon, session, sessionService, _) =
            CreateModeAwareDaemon(client, isPersistent: true, CreateConfigStateService(PersistenceMode.KeepAcrossRestart), containerGateway: gateway);
        session.ContainerId = "detach-container-id";
        await SeedSessionRowAsync(sessionService, session);

        await daemon.StopAsync(CancellationToken.None); // detach

        Assert.Empty(gateway.Calls);             // detach touches the container gateway not at all
        Assert.Equal(0, gateway.DestructiveCallCount); // in particular no stop/kill/remove/volume-remove
        Assert.Equal(0, client.LogoutCallCount); // and no logout
    }

    [Fact]
    public async Task StopAsync_PersistentKillOnRestart_RemovesContainerThroughGateway()
    {
        const string containerId = "kill-container-id";
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(new FakeContainer { Id = containerId, Name = "steam-daemon-persistent", Running = true });
        var client = new RecordingDaemonClient();
        var (daemon, session, sessionService, _) =
            CreateModeAwareDaemon(client, isPersistent: true, CreateConfigStateService(PersistenceMode.KillOnRestart), containerGateway: gateway);
        session.ContainerId = containerId;
        await SeedSessionRowAsync(sessionService, session);

        await daemon.StopAsync(CancellationToken.None); // KillOnRestart -> terminate + container teardown

        // The spy DOES observe destructive ops, so the detach test's zero-count above is not vacuous.
        Assert.True(gateway.DestructiveCallCount > 0);
        Assert.Contains(gateway.Calls, c => c == $"Remove:{containerId}");
    }

    [Fact]
    public async Task TearDownRejectedCreateAsync_WithContainer_RemovesContainerThroughGateway()
    {
        const string containerId = "rejected-container-id";
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(new FakeContainer { Id = containerId, Name = "steam-daemon-persistent", Running = true });
        var (daemon, _, _, _) =
            CreateModeAwareDaemon(new RecordingDaemonClient(), isPersistent: true, CreateConfigStateService(PersistenceMode.FullPersistence), containerGateway: gateway);

        var sessionDir = Path.Combine(Path.GetTempPath(), $"prefill_rejected_{Guid.NewGuid():N}");
        var commandsDir = Path.Combine(sessionDir, "commands");
        Directory.CreateDirectory(commandsDir);
        var client = new RecordingDaemonClient();
        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
            Status = DaemonSessionStatus.Active,
            IsPersistent = true,
            AuthState = DaemonAuthState.NotAuthenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            ContainerId = containerId,
            CommandsDir = commandsDir,
            Client = client
        };

        await daemon.TearDownRejectedCreateAsync(session);

        // The container-remove leg (previously integration-only) now has unit coverage through the seam.
        Assert.Contains(gateway.Calls, c => c == $"Remove:{containerId}");
        Assert.True(client.Disposed);
        Assert.False(Directory.Exists(sessionDir));
    }

    // === Teardown drains in-flight daemon events before disposing the client (detach + terminate) ===

    [Fact]
    public async Task StopAsync_DetachMode_DrainsInFlightEvents_BeforeReturning()
    {
        var client = new DrainRecordingDaemonClient();
        var (daemon, session, sessionService, _) =
            CreateModeAwareDaemon(client, isPersistent: true, CreateConfigStateService(PersistenceMode.KeepAcrossRestart));
        await SeedSessionRowAsync(sessionService, session);

        await daemon.StopAsync(CancellationToken.None); // detach path

        Assert.True(client.DrainCalled);            // detach consumed the drain
        Assert.True(client.InFlightWriteCompleted); // and awaited it before returning
    }

    [Fact]
    public async Task StopAsync_KillOnRestart_DrainsInFlightEvents_BeforeDisposingClient()
    {
        var client = new DrainRecordingDaemonClient();
        var (daemon, session, sessionService, _) =
            CreateModeAwareDaemon(client, isPersistent: true, CreateConfigStateService(PersistenceMode.KillOnRestart));
        await SeedSessionRowAsync(sessionService, session);

        await daemon.StopAsync(CancellationToken.None); // KillOnRestart -> TerminateSessionAsync

        Assert.True(client.DrainCalled);            // terminate consumed the drain
        Assert.True(client.InFlightWriteCompleted); // and awaited it before disposing the client
    }

    // === D7: TerminateSessionAsync establishes quiescence (drain) BEFORE it flips the DB row ===

    [Fact]
    public async Task TerminateSessionAsync_DrainsBeforeFlippingTheDbRowToTerminated()
    {
        var client = new DbStatusProbeDrainClient();
        var (daemon, session, sessionService, _) =
            CreateModeAwareDaemon(client, isPersistent: true, CreateConfigStateService(PersistenceMode.KillOnRestart));
        await SeedSessionRowAsync(sessionService, session); // Active row
        client.Configure(async () => (await sessionService.GetSessionAsync(session.Id))?.Status);

        await daemon.TerminateSessionAsync(session.Id, "test stop");

        // The drain runs BEFORE the termination mutations, so the row was still Active while draining;
        // pre-reorder (drain last) the probe would have observed Terminated.
        Assert.Equal(PrefillSessionStatus.Active, client.StatusObservedDuringDrain);
        Assert.Equal(PrefillSessionStatus.Terminated, (await sessionService.GetSessionAsync(session.Id))!.Status);
    }

    // === D8: rejected-create removal is bounded + the bind-mount dir is deleted ONLY on confirmed removal ===

    [Fact]
    public async Task TearDownRejectedCreateAsync_RemovalFails_DoesNotDeleteBindMountDirectory()
    {
        const string containerId = "p2-remove-fails";
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(new FakeContainer { Id = containerId, Name = "steam-daemon-persistent", Running = true });
        gateway.FailNextRemoveContainer(new InvalidOperationException("simulated docker failure"));
        var (daemon, _, _, _) = CreateModeAwareDaemon(
            new RecordingDaemonClient(), isPersistent: true, CreateConfigStateService(PersistenceMode.FullPersistence), containerGateway: gateway);
        var (session, sessionDir, client) = BuildContainerSession(containerId);

        await daemon.TearDownRejectedCreateAsync(session, involuntaryRecreate: true);

        Assert.Contains(gateway.Calls, c => c == $"Remove:{containerId}"); // removal was attempted
        Assert.True(Directory.Exists(sessionDir)); // but NOT confirmed, so the bind-mount dir is left for reconcile
        Assert.True(client.Disposed);
        Directory.Delete(sessionDir, true);
    }

    [Fact]
    public async Task TearDownRejectedCreateAsync_ContainerAlreadyGone_DeletesBindMountDirectory()
    {
        const string containerId = "p2-already-gone";
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(new FakeContainer { Id = containerId, Name = "steam-daemon-persistent", Running = true });
        // "Already gone" (DockerContainerNotFoundException) is a CONFIRMED absence, unlike a genuine removal
        // failure - so the bind-mount dir is safe to delete instead of leaking forever (nothing reaps it).
        gateway.FailNextRemoveContainer(new Docker.DotNet.DockerContainerNotFoundException(System.Net.HttpStatusCode.NotFound, "container not found"));
        var (daemon, _, _, _) = CreateModeAwareDaemon(
            new RecordingDaemonClient(), isPersistent: true, CreateConfigStateService(PersistenceMode.FullPersistence), containerGateway: gateway);
        var (session, sessionDir, client) = BuildContainerSession(containerId);

        await daemon.TearDownRejectedCreateAsync(session, involuntaryRecreate: true);

        Assert.Contains(gateway.Calls, c => c == $"Remove:{containerId}"); // removal attempted
        Assert.False(Directory.Exists(sessionDir)); // already-gone == confirmed absent -> dir deleted, not leaked
        Assert.True(client.Disposed);
    }

    // === D9: rejected-create logout/preserve is policy-derived (same predicate as the fresh-login guard) ===

    [Fact]
    public async Task TearDownRejectedCreateAsync_NonFullPersistence_ErasesVolumeLoginBeforeRemoval()
    {
        const string containerId = "p2-kill-logout";
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(new FakeContainer { Id = containerId, Name = "steam-daemon-persistent", Running = true });
        var (daemon, _, _, _) = CreateModeAwareDaemon(
            new RecordingDaemonClient(), isPersistent: true, CreateConfigStateService(PersistenceMode.KillOnRestart), containerGateway: gateway);
        var (session, sessionDir, client) = BuildContainerSession(containerId);

        await daemon.TearDownRejectedCreateAsync(session);

        Assert.Equal(1, client.LogoutCallCount); // KillOnRestart -> erase the possibly-inherited volume login
        Assert.Contains(gateway.Calls, c => c == $"Remove:{containerId}");
        Assert.False(Directory.Exists(sessionDir)); // removal confirmed -> dir deleted
    }

    [Fact]
    public async Task TearDownRejectedCreateAsync_FullPersistenceInvoluntary_PreservesLogin_NoLogout()
    {
        const string containerId = "p2-full-nologout";
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(new FakeContainer { Id = containerId, Name = "steam-daemon-persistent", Running = true });
        var (daemon, _, _, _) = CreateModeAwareDaemon(
            new RecordingDaemonClient(), isPersistent: true, CreateConfigStateService(PersistenceMode.FullPersistence), containerGateway: gateway);
        var (session, sessionDir, client) = BuildContainerSession(containerId);

        await daemon.TearDownRejectedCreateAsync(session, involuntaryRecreate: true);

        Assert.Equal(0, client.LogoutCallCount); // verified FullPersistence involuntary -> preserve the volume login
        Assert.Contains(gateway.Calls, c => c == $"Remove:{containerId}");
        Assert.False(Directory.Exists(sessionDir));
    }

    // === D6: a create that raced shutdown is torn down BY MODE (detach leaves the container, kill removes it) ===

    [Fact]
    public async Task RejectEscapedCreateAsync_DetachMode_LeavesContainerRunning()
    {
        const string containerId = "p2-escape-detach";
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(new FakeContainer { Id = containerId, Name = "steam-daemon-persistent", Running = true });
        var (daemon, _, _, _) = CreateModeAwareDaemon(
            new RecordingDaemonClient(), isPersistent: true, CreateConfigStateService(PersistenceMode.KeepAcrossRestart), containerGateway: gateway);
        var (session, sessionDir, client) = BuildContainerSession(containerId);

        await daemon.RejectEscapedCreateAsync(session, involuntaryRecreate: false, wasRegistered: false);

        Assert.Equal(0, gateway.DestructiveCallCount); // KeepAcrossRestart leaves the container for re-adopt
        Assert.True(gateway.ContainsContainer(containerId));
        Assert.True(client.Disposed);              // only the local handle is released
        Assert.True(Directory.Exists(sessionDir)); // detach never deletes the bind-mount dir
        Directory.Delete(sessionDir, true);
    }

    [Fact]
    public async Task RejectEscapedCreateAsync_KillMode_RemovesContainer()
    {
        const string containerId = "p2-escape-kill";
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(new FakeContainer { Id = containerId, Name = "steam-daemon-persistent", Running = true });
        var (daemon, _, _, _) = CreateModeAwareDaemon(
            new RecordingDaemonClient(), isPersistent: true, CreateConfigStateService(PersistenceMode.KillOnRestart), containerGateway: gateway);
        var (session, sessionDir, client) = BuildContainerSession(containerId);

        await daemon.RejectEscapedCreateAsync(session, involuntaryRecreate: false, wasRegistered: false);

        Assert.Contains(gateway.Calls, c => c == $"Remove:{containerId}"); // KillOnRestart removes the escaped container
        Assert.False(gateway.ContainsContainer(containerId));
    }

    // === Single-owner handoff: a post-registration reject whose session shutdown already claimed must NOT
    //     double-tear-down ===

    [Fact]
    public async Task RejectEscapedCreateAsync_PostRegistration_ShutdownAlreadyClaimed_SkipsTeardown()
    {
        const string containerId = "p2-escape-owned-by-shutdown";
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(new FakeContainer { Id = containerId, Name = "steam-daemon-persistent", Running = true });
        var (daemon, _, _, _) = CreateModeAwareDaemon(
            new RecordingDaemonClient(), isPersistent: true, CreateConfigStateService(PersistenceMode.KillOnRestart), containerGateway: gateway);
        // The session is deliberately NOT in _sessions: shutdown's Detach/Terminate already won the
        // single-owner TryRemove and is tearing this exact instance down. A post-registration reject
        // (wasRegistered: true) must defer to that owner instead of removing the container / disposing the
        // client a second time (which would double-dispose and throw on the already-disposed CTS).
        var (session, sessionDir, client) = BuildContainerSession(containerId);

        await daemon.RejectEscapedCreateAsync(session, involuntaryRecreate: false, wasRegistered: true);

        Assert.Equal(0, gateway.DestructiveCallCount);       // did NOT remove the container shutdown owns
        Assert.True(gateway.ContainsContainer(containerId));
        Assert.False(client.Disposed);                       // did NOT double-dispose the client
        Assert.True(Directory.Exists(sessionDir));           // did NOT delete the bind-mount dir
        Directory.Delete(sessionDir, true);
    }

    [Fact]
    public async Task TearDownRejectedCreateAsync_AlreadyDisposedCts_DoesNotThrow()
    {
        var (daemon, _, _, _) =
            CreateModeAwareDaemon(new RecordingDaemonClient(), isPersistent: true, CreateConfigStateService(PersistenceMode.KeepAcrossRestart));
        var sessionDir = Path.Combine(Path.GetTempPath(), $"prefill_cts_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(sessionDir, "commands"));
        var client = new RecordingDaemonClient();
        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
            Status = DaemonSessionStatus.Active,
            IsPersistent = true,
            AuthState = DaemonAuthState.NotAuthenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            CommandsDir = Path.Combine(sessionDir, "commands"),
            Client = client
        };
        // Simulate the winning teardown path having already cancelled + disposed the CTS.
        session.CancellationTokenSource.Cancel();
        session.CancellationTokenSource.Dispose();

        // Must not throw ObjectDisposedException from a second Cancel() on the already-disposed CTS.
        await daemon.TearDownRejectedCreateAsync(session);

        Assert.True(client.Disposed);
        Assert.False(Directory.Exists(sessionDir)); // no container -> removal is a no-op success -> dir cleaned
    }

    // === Post-DB-insert rollback: a create that finished its DB insert as shutdown began is rolled back
    //     (mode-aware) so no Active row / creation broadcast outlives shutdown ===

    [Fact]
    public async Task RollBackCreateIfShuttingDownAsync_TerminateMode_FlipsRowTerminated_AndRemovesContainer()
    {
        const string containerId = "rollback-kill";
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(new FakeContainer { Id = containerId, Name = "steam-daemon-persistent", Running = true });
        var client = new RecordingDaemonClient();
        var (daemon, session, sessionService, _) = CreateModeAwareDaemon(
            client, isPersistent: true, CreateConfigStateService(PersistenceMode.KillOnRestart), containerGateway: gateway);
        session.ContainerId = containerId;
        await SeedSessionRowAsync(sessionService, session); // the create's just-inserted Active row
        SetStopping(daemon, true);                          // shutdown began after the post-registration re-check

        var rejected = await daemon.RollBackCreateIfShuttingDownAsync(session, involuntaryRecreate: false, isReconnect: false, CancellationToken.None);

        Assert.True(rejected);
        Assert.Equal(PrefillSessionStatus.Terminated, (await sessionService.GetSessionAsync(session.Id))!.Status); // Active row rolled back
        Assert.Contains(gateway.Calls, c => c == $"Remove:{containerId}");                                          // KillOnRestart removes the escaped container
        Assert.Null(daemon.GetSession(session.Id));                                                                 // creator still owned it -> reject won the TryRemove
    }

    [Fact]
    public async Task RollBackCreateIfShuttingDownAsync_DetachMode_LeavesRowActive_AndContainerRunning()
    {
        const string containerId = "rollback-detach";
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(new FakeContainer { Id = containerId, Name = "steam-daemon-persistent", Running = true });
        var client = new RecordingDaemonClient();
        var (daemon, session, sessionService, _) = CreateModeAwareDaemon(
            client, isPersistent: true, CreateConfigStateService(PersistenceMode.KeepAcrossRestart), containerGateway: gateway);
        session.ContainerId = containerId;
        await SeedSessionRowAsync(sessionService, session);
        SetStopping(daemon, true);

        var rejected = await daemon.RollBackCreateIfShuttingDownAsync(session, involuntaryRecreate: false, isReconnect: false, CancellationToken.None);

        Assert.True(rejected);
        Assert.Equal(PrefillSessionStatus.Active, (await sessionService.GetSessionAsync(session.Id))!.Status); // detach leaves the row Active for re-adoption
        Assert.Equal(0, gateway.DestructiveCallCount);                                                         // container left running
        Assert.True(gateway.ContainsContainer(containerId));
        Assert.Equal(0, client.LogoutCallCount);                                                              // detach never logs out
        Assert.True(client.Disposed);                                                                          // only the local handle released
        Assert.Null(daemon.GetSession(session.Id));
    }

    [Fact]
    public async Task RollBackCreateIfShuttingDownAsync_NotStopping_ReturnsFalse_AndLeavesCreateIntact()
    {
        var client = new RecordingDaemonClient();
        var (daemon, session, sessionService, _) = CreateModeAwareDaemon(
            client, isPersistent: true, CreateConfigStateService(PersistenceMode.KillOnRestart));
        await SeedSessionRowAsync(sessionService, session);

        var rejected = await daemon.RollBackCreateIfShuttingDownAsync(session, involuntaryRecreate: false, isReconnect: false, CancellationToken.None);

        Assert.False(rejected);
        Assert.Equal(PrefillSessionStatus.Active, (await sessionService.GetSessionAsync(session.Id))!.Status); // normal create untouched
        Assert.NotNull(daemon.GetSession(session.Id));
    }

    // === An explicit admin stop during host shutdown fails loudly instead of silently no-op'ing ===

    [Fact]
    public async Task StopPersistentSessionAsync_DuringShutdown_FailsLoudly_PreservesLogin()
    {
        var client = new RecordingDaemonClient();
        var (daemon, session, sessionService, _) = CreateModeAwareDaemon(
            client, isPersistent: true, CreateConfigStateService(PersistenceMode.KeepAcrossRestart));
        await SeedSessionRowAsync(sessionService, session);
        SetStopping(daemon, true); // host shutdown in progress: StopAsync detach may already own this session

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => daemon.StopPersistentSessionAsync(session.Id, terminatedBy: "admin"));

        // Loud failure, not a silent no-op success: the login is untouched (row still Active, no logout) so
        // the next start re-adopts it rather than the admin believing it was erased.
        Assert.Equal(PrefillSessionStatus.Active, (await sessionService.GetSessionAsync(session.Id))!.Status);
        Assert.Equal(0, client.LogoutCallCount);
        Assert.NotNull(daemon.GetSession(session.Id));
    }

    // === Post-timeout liveness fences: a callback that escaped the bounded drain (session no longer the
    //     live registered instance) performs no durable write / broadcast after teardown completed ===

    [Fact]
    public async Task NotifyAuthStateChangeAsync_SessionTornDown_FiresNoHubBroadcasts()
    {
        var (daemon, _, _, notifications) = CreateModeAwareDaemon(
            new RecordingDaemonClient(), isPersistent: true, CreateConfigStateService(PersistenceMode.KeepAcrossRestart));
        var deadSession = BuildDeadSession();

        await InvokePrivateHandlerAsync(daemon, "NotifyAuthStateChangeAsync", deadSession);

        Assert.Equal(0, notifications.InvocationCount); // both DownloadHub mirrors fenced by the post-await re-checks
    }

    [Fact]
    public async Task NotifyCredentialChallengeAsync_SessionTornDown_FiresNoHubMirror()
    {
        var (daemon, _, _, notifications) = CreateModeAwareDaemon(
            new RecordingDaemonClient(), isPersistent: true, CreateConfigStateService(PersistenceMode.KeepAcrossRestart));
        var deadSession = BuildDeadSession();
        var challenge = new CredentialChallenge { ChallengeId = "c1", CredentialType = "username" };

        await InvokePrivateHandlerAsync(daemon, "NotifyCredentialChallengeAsync", deadSession, challenge);

        Assert.Equal(0, notifications.InvocationCount); // hub mirror fenced after the subscriber fan-out
    }

    [Fact]
    public async Task NotifyPrefillProgressAsync_TerminalState_SessionTornDown_FiresNoBroadcasts()
    {
        var (daemon, _, _, notifications) = CreateModeAwareDaemon(
            new RecordingDaemonClient(), isPersistent: true, CreateConfigStateService(PersistenceMode.KeepAcrossRestart));
        var deadSession = BuildDeadSession();
        deadSession.IsPrefilling = true;
        deadSession.CurrentAppId = "440";
        deadSession.CurrentAppName = "TF2"; // match progress so appInfoChanged is false (skip the accepted-unfenced app transition)
        deadSession.PrefillState = PrefillState.Downloading;
        var progress = new PrefillProgress { State = "completed", CurrentAppId = "440", CurrentAppName = "TF2" };

        await InvokePrivateHandlerAsync(daemon, "NotifyPrefillProgressAsync", deadSession, progress);

        Assert.Equal(0, notifications.InvocationCount);                     // history + terminal broadcasts fenced
        Assert.NotEqual(PrefillState.Completed, deadSession.PrefillState);  // terminal transition never ran
    }

    [Fact]
    public async Task NotifyPrefillProgressAsync_AppCompleted_SessionTornDown_FiresNoBroadcasts()
    {
        var (daemon, _, _, notifications) = CreateModeAwareDaemon(
            new RecordingDaemonClient(), isPersistent: true, CreateConfigStateService(PersistenceMode.KeepAcrossRestart));
        var deadSession = BuildDeadSession();
        deadSession.CurrentAppId = "440";
        deadSession.CurrentAppName = "TF2"; // match progress so appInfoChanged is false (skip the accepted-unfenced app transition)
        var progress = new PrefillProgress { State = "app_completed", CurrentAppId = "440", CurrentAppName = "TF2", Result = "Success" };

        await InvokePrivateHandlerAsync(daemon, "NotifyPrefillProgressAsync", deadSession, progress);

        Assert.Equal(0, notifications.InvocationCount); // app-completed history + progress broadcasts fenced
    }

    // A session that is NOT registered in _sessions - it has been torn down, or a fire-and-forget callback
    // escaped the bounded teardown drain. IsSessionLive returns false for it, so every post-await fence in the
    // notification funnels must short-circuit before any durable write / broadcast. No subscribers, so a
    // funnel that reaches its subscriber fan-out records nothing; only an unfenced DownloadHub mirror would.
    private static DaemonSession BuildDeadSession()
        => new()
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
            Status = DaemonSessionStatus.Active,
            IsPersistent = true,
            AuthState = DaemonAuthState.NotAuthenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Client = new RecordingDaemonClient()
        };

    private static (DaemonSession Session, string SessionDir, RecordingDaemonClient Client) BuildContainerSession(string containerId, bool isPersistent = true)
    {
        var sessionDir = Path.Combine(Path.GetTempPath(), $"prefill_p2_{Guid.NewGuid():N}");
        var commandsDir = Path.Combine(sessionDir, "commands");
        Directory.CreateDirectory(commandsDir);
        var client = new RecordingDaemonClient();
        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
            Status = DaemonSessionStatus.Active,
            IsPersistent = isPersistent,
            AuthState = DaemonAuthState.NotAuthenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            ContainerId = containerId,
            CommandsDir = commandsDir,
            Client = client
        };
        return (session, sessionDir, client);
    }

    private static (PrefillDaemonServiceBase Daemon, DaemonSession Session) CreateSessionWithClient(
        IDaemonClient client, bool isPersistent, ILogger<SteamDaemonService>? logger = null)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"persistent_erase_on_stop_{Guid.NewGuid():N}")
            .Options;
        var dbFactory = new InMemoryDbContextFactory(dbOptions);
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var configuration = new ConfigurationBuilder().Build();
        var pathResolver = (IPathResolver)DispatchProxy.Create<IPathResolver, NullReturningProxy>();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();
        var networkOptions = new StaticOptionsMonitor<PrefillNetworkOptions>(new PrefillNetworkOptions());

        var daemon = new TestableSteamDaemonService(
            logger ?? NullLogger<SteamDaemonService>.Instance, notifications, configuration, pathResolver,
            stateService, sessionService, cacheService, networkOptions);

        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
            Status = DaemonSessionStatus.Active,
            IsPersistent = isPersistent,
            AuthState = DaemonAuthState.NotAuthenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Client = client
        };
        daemon.InjectSession(session);

        return (daemon, session);
    }

    // Test-only seam: _sessions is protected on PrefillDaemonServiceBase, so production code never
    // exposes a way to inject a session without a real daemon round-trip. Mirrors the InjectSession
    // seam in PersistentLogoutTests.cs / PersistentLoginChallengeResumeTests.cs.
    private sealed class TestableSteamDaemonService : SteamDaemonService
    {
        public TestableSteamDaemonService(
            Microsoft.Extensions.Logging.ILogger<SteamDaemonService> logger,
            ISignalRNotificationService notifications,
            IConfiguration configuration,
            IPathResolver pathResolver,
            IStateService stateService,
            PrefillSessionService sessionService,
            PrefillCacheService cacheService,
            IOptionsMonitor<PrefillNetworkOptions> networkOptions,
            IPrefillContainerGateway? containerGateway = null)
            : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions, new TestLancacheServerLocator(),
                new SingleContainerGatewayFactory(containerGateway ?? new RecordingContainerGateway(available: false)))
        {
        }

        public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext() => new AppDbContext(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(_options));
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Minimal do-nothing proxy for interfaces whose members are not exercised by these tests' happy
    /// paths (mirrors NullReturningProxy in PersistentLogoutTests.cs).
    /// </summary>
    private class NullReturningProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod?.ReturnType;

            if (returnType is null || returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
            {
                return Activator.CreateInstance(returnType);
            }

            return null;
        }
    }

    /// <summary>
    /// Fake <see cref="IDaemonClient"/> exposing only the surface these scenarios touch; every other
    /// member throws <see cref="NotSupportedException"/> so an unexpected call fails loudly (mirrors
    /// TestDaemonClientBase in PersistentLogoutTests.cs).
    /// </summary>
    private abstract class TestDaemonClientBase : IDaemonClient
    {
        public event Func<CredentialChallenge, Task>? OnCredentialChallenge { add { } remove { } }
        public event Func<DaemonStatus, Task>? OnStatusUpdate { add { } remove { } }
        public event Func<SocketPrefillProgress, Task>? OnProgressUpdate { add { } remove { } }
        public event Func<string, Task>? OnError { add { } remove { } }
        public event Func<Task>? OnDisconnected { add { } remove { } }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Unexpected GetStatusAsync in this scenario.");

        public Task<CommandResponse> SendCommandAsync(
            string type, Dictionary<string, string>? parameters = null, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException($"Unexpected SendCommandAsync({type}) in this test.");

        public Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Unexpected StartLoginAsync in an erase-on-stop scenario.");

        public Task ProvideCredentialAsync(CredentialChallenge challenge, string credential, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CredentialChallenge?> GetAutoLoginChallengeAsync(string sessionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ProvideAutoLoginAsync(string sessionId, string username, string refreshToken, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ProvideEpicAutoLoginAsync(string sessionId, string refreshToken, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ProvideXboxAutoLoginAsync(string sessionId, string refreshToken, string deviceKeyPkcs8, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CredentialChallenge?> WaitForChallengeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelLoginAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public abstract Task<bool> LogoutAsync(CancellationToken cancellationToken = default);

        // Explicit (virtual, not DIM-inherited) so a subclass can override it the normal OOP way to
        // simulate the daemon's RequiresLogin signal - mirrors TestDaemonClientBase in PersistentLogoutTests.cs.
        // Fakes that don't override this just adapt whatever LogoutAsync returns, matching
        // IDaemonClient's own default implementation.
        public virtual async Task<LogoutOutcome> LogoutWithReasonAsync(CancellationToken cancellationToken = default)
        {
            var success = await LogoutAsync(cancellationToken);
            return new LogoutOutcome(success, RequiresLogin: false);
        }

        public Task CancelPrefillAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<List<OwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<List<CdnInfo>> GetCdnInfoAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetSelectedAppsAsync(List<string> appIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PrefillResult> PrefillAsync(
            bool all = false, bool recent = false, bool recentlyPurchased = false, int? top = null,
            bool force = false, List<string>? operatingSystems = null, int? maxConcurrency = null,
            List<CachedDepotInput>? cachedDepots = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ClearCacheResult> ClearCacheAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ClearCacheResult> GetCacheInfoAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(List<string>? operatingSystems = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CacheStatusResult> CheckCacheStatusAsync(List<CachedDepotInput> cachedDepots, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        // TerminateSessionAsync calls ShutdownAsync in its graceful (non-force) branch, but only when
        // _dockerClient and session.ContainerId are both set - neither is true in this harness (no
        // Docker started, ContainerId left unset), so this is never actually invoked here. Completed
        // rather than throwing purely as a defensive default, matching TestDaemonClientBase's style.
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual void ClearPendingChallenges() { }

        // Declared virtual (not left to IDaemonClient's default method) so a fake can override it - a
        // default interface method cannot be overridden through the class hierarchy. Mirrors how
        // LogoutWithReasonAsync is exposed above.
        public virtual Task DrainEventsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual void Dispose() { }
    }

    /// <summary>
    /// Scenario: the daemon responds to the <c>logout</c> command without a transport error, either
    /// succeeding or reporting failure - both must be swallowed by the best-effort guards under test.
    /// </summary>
    private sealed class ScriptedLogoutDaemonClient : TestDaemonClientBase
    {
        private readonly bool _succeeds;

        public ScriptedLogoutDaemonClient(bool succeeds)
        {
            _succeeds = succeeds;
        }

        public int LogoutCallCount { get; private set; }

        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
        {
            LogoutCallCount++;
            return Task.FromResult(_succeeds);
        }
    }

    /// <summary>
    /// Scenario: the daemon round-trip itself throws (dead/hung socket) - must be swallowed, not
    /// propagated, by both best-effort guards under test.
    /// </summary>
    private sealed class ThrowingLogoutDaemonClient : TestDaemonClientBase
    {
        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated daemon logout round-trip failure.");
    }

    /// <summary>
    /// Scenario: an older daemon image's pre-login command gate rejects "logout" outright because the
    /// session hasn't finished authenticating - a real daemon response (not a transport error), just
    /// one that carries <c>RequiresLogin: true</c> alongside <c>Success: false</c>.
    /// </summary>
    private sealed class PreLoginRejectionDaemonClient : TestDaemonClientBase
    {
        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public override Task<LogoutOutcome> LogoutWithReasonAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LogoutOutcome(false, RequiresLogin: true));
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    /// <summary>Captures log entries so a test can assert on the exact text a scenario produces -
    /// mirrors ScheduledPrefillServiceTests.cs's CapturingLogger.</summary>
    private sealed class CapturingLogger : ILogger<SteamDaemonService>
    {
        private readonly object _sync = new();
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_sync) return _entries.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }

    private static async Task SeedSessionRowAsync(PrefillSessionService sessionService, DaemonSession session)
    {
        // Real in-memory DB row so a detach can be proven to LEAVE it Active while a terminate flips it
        // to Terminated. Container id/name are placeholders (no Docker in this harness).
        await sessionService.CreateSessionAsync(
            session.Id, session.UserId, $"container-{session.Id}", $"name-{session.Id}", session.ExpiresAt, "Steam");
    }

    private static IStateService CreateNullStateService()
        => (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();

    private static IStateService CreateConfigStateService(PersistenceMode globalMode)
        => CreateConfigStateService(ConfigWithGlobalMode(globalMode));

    private static IStateService CreateConfigStateService(ScheduledPrefillConfigDto config)
    {
        var proxy = (IStateService)DispatchProxy.Create<IStateService, ConfigStateServiceProxy>();
        ((ConfigStateServiceProxy)proxy).Config = config;
        return proxy;
    }

    // Seeds a PRIOR persistent session row for Steam in the given end state, so the fresh-login guard's
    // "was the last life terminated by an admin, or did it die involuntarily?" heuristic has evidence to
    // read. The row uses the deterministic persistent container name so IsPersistent is derived true.
    private static async Task SeedPriorPersistentRowAsync(PrefillSessionService sessionService, PrefillSessionStatus status)
    {
        var priorId = Guid.NewGuid().ToString("N")[..16];
        await sessionService.CreateSessionAsync(
            priorId, ScheduledPrefillConstants.DeriveSystemUserId(), $"container-{priorId}", "steam-daemon-persistent",
            DateTime.UtcNow.AddDays(30), "Steam");

        switch (status)
        {
            case PrefillSessionStatus.Terminated:
                await sessionService.TerminateSessionAsync(priorId, "admin stop");
                break;
            case PrefillSessionStatus.Orphaned:
                await sessionService.MarkOrphansAsync(PrefillPlatform.Steam);
                break;
            // Active: leave exactly as created.
        }
    }

    // Builds a config where Steam carries a per-service override that disagrees with the global mode, to
    // prove override-wins in both directions. ScheduledPrefillServiceConfigDto is a sealed init-only class,
    // so the Steam entry is rebuilt field-by-field from CreateDefault() with only the override changed.
    private static ScheduledPrefillConfigDto ConfigWithModes(PersistenceMode globalMode, PersistenceMode? steamOverride)
    {
        var baseConfig = ScheduledPrefillConfigFactory.CreateDefault();
        var steam = baseConfig.Steam;
        var steamWithOverride = new ScheduledPrefillServiceConfigDto
        {
            ServiceId = steam.ServiceId,
            Enabled = steam.Enabled,
            ShowNotification = steam.ShowNotification,
            IntervalHours = steam.IntervalHours,
            Preset = steam.Preset,
            TopCount = steam.TopCount,
            SelectedAppIds = steam.SelectedAppIds,
            OperatingSystems = steam.OperatingSystems,
            Force = steam.Force,
            MaxConcurrency = steam.MaxConcurrency,
            PersistenceMode = steamOverride
        };
        return new ScheduledPrefillConfigDto
        {
            Version = baseConfig.Version,
            MaxServiceRuntime = baseConfig.MaxServiceRuntime,
            StallTimeout = baseConfig.StallTimeout,
            Steam = steamWithOverride,
            Epic = baseConfig.Epic,
            Xbox = baseConfig.Xbox,
            BattleNet = baseConfig.BattleNet,
            Riot = baseConfig.Riot,
            PersistenceMode = globalMode
        };
    }

    // Invokes a private daemon event handler (OnStatusChangeAsync / OnProgressChangeAsync) directly, so a
    // test can prove the post-detach guard early-returns for a session no longer in _sessions. Production
    // wires these off the socket read loop, which a session-injection harness bypasses.
    private static async Task InvokePrivateHandlerAsync(PrefillDaemonServiceBase daemon, string methodName, params object[] args)
    {
        var method = typeof(PrefillDaemonServiceBase).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{methodName} not found on {nameof(PrefillDaemonServiceBase)}");
        await (Task)method.Invoke(daemon, args)!;
    }

    // Flips the private volatile _stopping flag so a test can pin "host shutdown has begun" deterministically
    // without driving StopAsync (which would also tear the target session down). Production only ever sets
    // this at the top of StopAsync.
    private static void SetStopping(PrefillDaemonServiceBase daemon, bool value)
    {
        var field = typeof(PrefillDaemonServiceBase).GetField("_stopping", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"_stopping field not found on {nameof(PrefillDaemonServiceBase)}");
        field.SetValue(daemon, value);
    }

    // ScheduledPrefillConfigDto is a sealed class (not a record), so a fixture with a specific global
    // mode is built by copying CreateDefault()'s fields. The daemon under test is Steam with no
    // per-service override, so the global mode IS its effective mode.
    private static ScheduledPrefillConfigDto ConfigWithGlobalMode(PersistenceMode globalMode)
    {
        var baseConfig = ScheduledPrefillConfigFactory.CreateDefault();
        return new ScheduledPrefillConfigDto
        {
            Version = baseConfig.Version,
            MaxServiceRuntime = baseConfig.MaxServiceRuntime,
            StallTimeout = baseConfig.StallTimeout,
            Steam = baseConfig.Steam,
            Epic = baseConfig.Epic,
            Xbox = baseConfig.Xbox,
            BattleNet = baseConfig.BattleNet,
            Riot = baseConfig.Riot,
            PersistenceMode = globalMode
        };
    }

    private static (PrefillDaemonServiceBase Daemon, DaemonSession Session, PrefillSessionService SessionService, RecordingNotificationsProxy Notifications)
        CreateModeAwareDaemon(IDaemonClient client, bool isPersistent, IStateService stateService, ILogger<SteamDaemonService>? logger = null, IPrefillContainerGateway? containerGateway = null)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"persistent_mode_aware_{Guid.NewGuid():N}")
            .Options;
        var dbFactory = new InMemoryDbContextFactory(dbOptions);
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationsProxy>();
        var recorder = (RecordingNotificationsProxy)notifications;
        var configuration = new ConfigurationBuilder().Build();
        var pathResolver = (IPathResolver)DispatchProxy.Create<IPathResolver, NullReturningProxy>();
        var networkOptions = new StaticOptionsMonitor<PrefillNetworkOptions>(new PrefillNetworkOptions());

        var daemon = new TestableSteamDaemonService(
            logger ?? NullLogger<SteamDaemonService>.Instance, notifications, configuration, pathResolver,
            stateService, sessionService, cacheService, networkOptions, containerGateway);

        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
            Status = DaemonSessionStatus.Active,
            IsPersistent = isPersistent,
            AuthState = DaemonAuthState.NotAuthenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Client = client
        };
        daemon.InjectSession(session);

        return (daemon, session, sessionService, recorder);
    }

    private static object? DefaultReturnValue(MethodInfo? targetMethod)
    {
        var returnType = targetMethod?.ReturnType;

        if (returnType is null || returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
        {
            return Activator.CreateInstance(returnType);
        }

        return null;
    }

    /// <summary>
    /// IStateService stub whose GetScheduledPrefillConfig returns a configurable config (so StopAsync and
    /// the fresh-login guard can read a real effective mode); every other member returns its type default.
    /// Mirrors the PrefillConfigStateServiceProxy precedent in ScheduledPrefillServiceTests.cs, but with
    /// a settable Config so each test can pin the mode under test. Not sealed: DispatchProxy.Create
    /// derives a runtime subclass that an interface reference is cast back to.
    /// </summary>
    private class ConfigStateServiceProxy : DispatchProxy
    {
        public ScheduledPrefillConfigDto? Config { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillConfig))
            {
                return Config;
            }

            return DefaultReturnValue(targetMethod);
        }
    }

    /// <summary>
    /// ISignalRNotificationService stub that counts every invocation, so a test can assert the detach
    /// path fires ZERO broadcasts while a terminate fires at least one. Not sealed for the same reason as
    /// <see cref="ConfigStateServiceProxy"/>.
    /// </summary>
    private class RecordingNotificationsProxy : DispatchProxy
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Interlocked.Increment(ref _invocationCount);
            return DefaultReturnValue(targetMethod);
        }
    }

    /// <summary>
    /// Fake client that records both logout attempts (to assert erase vs detach) and disposal (to prove
    /// the detach path releases the manager's local handle).
    /// </summary>
    private sealed class RecordingDaemonClient : TestDaemonClientBase
    {
        public int LogoutCallCount { get; private set; }
        public bool Disposed { get; private set; }

        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
        {
            LogoutCallCount++;
            return Task.FromResult(true);
        }

        public override void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Fake client that records the teardown drain call and simulates an in-flight event whose "write"
    /// only finishes while the drain awaits it, so a test can prove detach/terminate await the drain.
    /// </summary>
    private sealed class DrainRecordingDaemonClient : TestDaemonClientBase
    {
        public bool DrainCalled { get; private set; }
        public bool InFlightWriteCompleted { get; private set; }

        public override async Task DrainEventsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            DrainCalled = true;
            await Task.Delay(30);
            InFlightWriteCompleted = true;
        }

        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    /// <summary>
    /// Fake client whose DrainEventsAsync runs a caller-supplied probe (e.g. reading the session's DB row
    /// status) at drain time, so a test can prove a teardown drains BEFORE the mutations it protects.
    /// </summary>
    private sealed class DbStatusProbeDrainClient : TestDaemonClientBase
    {
        private Func<Task<PrefillSessionStatus?>>? _probe;
        public PrefillSessionStatus? StatusObservedDuringDrain { get; private set; }

        public void Configure(Func<Task<PrefillSessionStatus?>> probe) => _probe = probe;

        public override async Task DrainEventsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (_probe != null)
            {
                StatusObservedDuringDrain = await _probe();
            }
        }

        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
