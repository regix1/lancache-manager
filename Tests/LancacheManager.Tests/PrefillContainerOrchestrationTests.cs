using System.Collections.Concurrent;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
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
/// End-to-end startup-reconcile orchestration for the persistent prefill container lifecycle, driven
/// against a <see cref="RecordingContainerGateway"/> (the docker seam) plus a fake reconnect client, so
/// the cleanup -&gt; re-adopt -&gt; recreate flow can be exercised without a live Docker daemon or daemon
/// socket. Complements the pure-helper coverage (<c>PersistentSingletonGateTests</c>,
/// <c>PersistentLoginValidityClockTests</c>) by proving the whole <see cref="PrefillDaemonServiceBase.StartAsync"/>
/// path wires those decisions to the right container operations and DB row transitions.
/// </summary>
public sealed class PrefillContainerOrchestrationTests : IDisposable
{
    private const string SteamPersistentContainerName = "steam-daemon-persistent";

    private readonly List<string> _tempRoots = new();

    // ============================ C-a ============================
    // Mode-2 (KeepAcrossRestart): a persistent container left running by a graceful shutdown detach is
    // re-adopted on the next start with its login intact - no stop/kill/remove/logout, DB row back to Active.
    [Fact]
    public async Task StartAsync_RunningPersistentContainer_ReadoptedRowActive_NoDestructiveDockerOps()
    {
        var (dbOptions, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);

        // A persistent Steam session from the previous life: Active DB row + a still-running container that
        // the detach on shutdown left behind, carrying the labels/secret the re-adopt path recovers.
        var sessionId = await SeedActivePersistentRowAsync(sessionService, expiresAt: DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));

        var daemon = new TestSteamDaemon(MakeDeps(dbFactory, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)), gateway);

        await daemon.StartAsync(CancellationToken.None);

        // Re-adopted: registered in memory and reactivated in the DB.
        var session = SessionsOf(daemon).Values.SingleOrDefault(s => s.IsPersistent);
        Assert.NotNull(session);
        Assert.Equal(sessionId, session!.Id);
        Assert.Equal(PrefillSessionStatus.Active, (await GetRowAsync(dbOptions, sessionId))!.Status);

        // Login intact: the container is still present + running, and nothing tore it down or logged it out.
        Assert.True(gateway.ContainsContainer(session.ContainerId));
        Assert.Equal(0, gateway.DestructiveCallCount);
        Assert.Equal(0, ((FakeReconnectDaemonClient)session.Client).LogoutCount);

        daemon.Dispose();
    }

    // ============================ C-e ============================
    // Five services, ONE shared DB: each daemon re-adopts its own running container, and no daemon's
    // platform-scoped orphan sweep re-orphans a row another daemon already reactivated. Every row ends Active.
    [Fact]
    public async Task StartAsync_FiveServicesSharedDatabase_EveryReadoptedRowActive()
    {
        var (dbOptions, dbFactory) = NewDatabase();
        var config = Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true);

        var specs = new (PrefillPlatform Platform, string Prefix)[]
        {
            (PrefillPlatform.Steam, "steam-daemon-"),
            (PrefillPlatform.Epic, "epic-daemon-"),
            (PrefillPlatform.Xbox, "xbox-daemon-"),
            (PrefillPlatform.BattleNet, "battlenet-prefill-"),
            (PrefillPlatform.Riot, "riot-prefill-")
        };

        var sessionIds = new Dictionary<PrefillPlatform, string>();
        var daemons = new List<PrefillDaemonServiceBase>();

        foreach (var (platform, prefix) in specs)
        {
            var svc = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
            var containerName = $"{prefix}persistent";
            var sessionId = await SeedActivePersistentRowAsync(svc, expiresAt: DateTime.UtcNow.AddDays(30), platform: platform, containerName: containerName);
            sessionIds[platform] = sessionId;

            var gateway = new RecordingContainerGateway();
            gateway.AddContainer(RunningPersistentContainer(sessionId, containerName, platform));
            daemons.Add(BuildDaemon(platform, MakeDeps(dbFactory, svc, config), gateway));
        }

        // Start them in order against the shared DB - the order a real host boots the hosted services.
        foreach (var daemon in daemons)
        {
            await daemon.StartAsync(CancellationToken.None);
        }

        // Every platform's row is Active at the end: no cross-platform re-orphaning.
        foreach (var (platform, _) in specs)
        {
            var row = await GetRowAsync(dbOptions, sessionIds[platform]);
            Assert.Equal(PrefillSessionStatus.Active, row!.Status);
        }

        foreach (var daemon in daemons)
        {
            daemon.Dispose();
        }
    }

    // ============================ C-b ============================
    // Mode-3 (FullPersistence) recreate after an outage: the vanished container is recreated and its new
    // session inherits the prior life's still-future validity window (does not silently extend it).
    [Fact]
    public async Task StartAsync_FullPersistenceRecreate_InheritsFutureExpiryAnchor()
    {
        var priorExpiry = DateTime.UtcNow.AddDays(45);
        var (dbOptions, daemon, gateway, priorSessionId) = SetupRecreateScenario(priorExpiry);

        await daemon.StartAsync(CancellationToken.None);

        var active = await GetActivePersistentRowAsync(dbOptions, PrefillPlatform.Steam);
        Assert.NotNull(active);
        Assert.NotEqual(priorSessionId, active!.SessionId);                       // a FRESH row, not the reactivated old one
        Assert.Equal(priorExpiry, active.ExpiresAtUtc, TimeSpan.FromSeconds(5));   // inherited the future window
        Assert.Contains(gateway.Calls, c => c.StartsWith("Create:", StringComparison.Ordinal));

        daemon.Dispose();
    }

    [Fact]
    public async Task StartAsync_FullPersistenceRecreate_UsesNowPlusValidityWhenPriorExpiryPast()
    {
        var priorExpiry = DateTime.UtcNow.AddDays(-5); // already elapsed
        const int validityDays = 90;
        var (dbOptions, daemon, _, _) = SetupRecreateScenario(priorExpiry, validityDays);

        await daemon.StartAsync(CancellationToken.None);

        var active = await GetActivePersistentRowAsync(dbOptions, PrefillPlatform.Steam);
        Assert.NotNull(active);
        Assert.Equal(DateTime.UtcNow.AddDays(validityDays), active!.ExpiresAtUtc, TimeSpan.FromMinutes(2));

        daemon.Dispose();
    }

    // C-b (gate leg): driving the re-adopt directly with a STOPPED container present exercises the gate's
    // Recreate action (remove the dead target, then create), the branch cleanup would otherwise pre-empt.
    [Fact]
    public async Task ReadoptPersistentContainers_StoppedContainerPresent_RemovesTargetThenRecreates()
    {
        var (dbOptions, daemon, gateway, priorSessionId) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30));
        var stopped = gateway.AddContainer(StoppedPersistentContainer(priorSessionId));

        // The prior row was seeded Active; the gate's shouldRecreate reads it as a non-Terminated life.
        await InvokePrivateAsync(daemon, "ReadoptPersistentContainersAsync", CancellationToken.None);

        Assert.Contains(gateway.Calls, c => c == $"Remove:{stopped.Id}");                     // dead target removed first
        Assert.Contains(gateway.Calls, c => c.StartsWith("Create:", StringComparison.Ordinal)); // then recreated
        Assert.NotNull(await GetActivePersistentRowAsync(dbOptions, PrefillPlatform.Steam));

        daemon.Dispose();
    }

    // ============================ C-c ============================
    // Negatives: startup must NOT fabricate a persistent container.
    [Fact]
    public async Task StartAsync_DisabledService_DoesNotRecreate()
    {
        await AssertNoRecreateAsync(Config(PersistenceMode.FullPersistence, steamEnabled: false), priorStatus: PrefillSessionStatus.Orphaned);
    }

    [Fact]
    public async Task StartAsync_TerminatedPriorRow_DoesNotRecreate()
    {
        await AssertNoRecreateAsync(Config(PersistenceMode.FullPersistence, steamEnabled: true), priorStatus: PrefillSessionStatus.Terminated);
    }

    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    public async Task StartAsync_NonFullPersistenceMode_DoesNotRecreateVanishedContainer(PersistenceMode mode)
    {
        await AssertNoRecreateAsync(Config(mode, steamEnabled: true), priorStatus: PrefillSessionStatus.Orphaned);
    }

    private async Task AssertNoRecreateAsync(ScheduledPrefillConfigDto config, PrefillSessionStatus priorStatus)
    {
        var (dbOptions, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        await SeedPriorPersistentRowAsync(sessionService, priorStatus, expiresAt: DateTime.UtcNow.AddDays(30));

        var gateway = new RecordingContainerGateway(); // empty inventory: the container vanished during the outage
        var daemon = new TestSteamDaemon(MakeDeps(dbFactory, sessionService, config), gateway);

        await daemon.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(gateway.Calls, c => c.StartsWith("Create:", StringComparison.Ordinal));
        Assert.Null(await GetActivePersistentRowAsync(dbOptions, PrefillPlatform.Steam));

        daemon.Dispose();
    }

    // ============================ C-d ============================
    // A docker create failure mid-recreate is swallowed (startup never aborts) and the dead session's DB
    // row stays non-Terminated, so the NEXT startup's zero-container retry arm recreates successfully.
    [Fact]
    public async Task StartAsync_RecreateDockerCreateFailure_RetriesAndSucceedsOnNextStartup()
    {
        var (dbOptions, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        await SeedPriorPersistentRowAsync(sessionService, PrefillSessionStatus.Orphaned, expiresAt: DateTime.UtcNow.AddDays(30));

        var gateway = new RecordingContainerGateway();
        var config = Config(PersistenceMode.FullPersistence, steamEnabled: true);

        // First startup: the create fails; recreate swallows it, no Active row appears.
        var first = new TestSteamDaemon(MakeDeps(dbFactory, sessionService, config), gateway);
        gateway.FailNextCreateContainer(new InvalidOperationException("simulated docker create failure"));
        await first.StartAsync(CancellationToken.None);
        first.Dispose();

        Assert.Contains(gateway.Calls, c => c.StartsWith("Create:", StringComparison.Ordinal));
        Assert.Null(await GetActivePersistentRowAsync(dbOptions, PrefillPlatform.Steam));

        // Second startup (retry arm): create now succeeds and the recreate produces an Active row.
        var second = new TestSteamDaemon(MakeDeps(dbFactory, sessionService, config), gateway);
        await second.StartAsync(CancellationToken.None);

        Assert.NotNull(await GetActivePersistentRowAsync(dbOptions, PrefillPlatform.Steam));

        second.Dispose();
    }

    // ============================ Headless self-auth after involuntary recreate ============================
    // A FullPersistence outage-recreate preserves the daemon's stored volume login, but the daemon only
    // self-authenticates when it receives a login command. The manager now issues that command itself,
    // headlessly, so the session comes back logged-in with zero user interaction.

    [Fact]
    public async Task StartAsync_FullPersistenceRecreate_HeadlessSelfAuthRestoresLoginWithoutUserAction()
    {
        var client = new ScriptedLoginDaemonClient(); // stored volume login intact: self-auths on the login command
        var (_, daemon, _, _) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30), clientFactory: () => client);

        await daemon.StartAsync(CancellationToken.None);

        var attempt = daemon.LastHeadlessSelfAuthAttempt;
        Assert.NotNull(attempt);
        await attempt!;

        var session = SessionsOf(daemon).Values.Single(s => s.IsPersistent);
        Assert.Equal(1, client.StartLoginCallCount);                        // exactly one login command issued
        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);     // logged-in with zero user interaction
        Assert.Null(session.PendingLoginChallenge);                         // no challenge state left behind

        daemon.Dispose();
    }

    [Fact]
    public async Task StartAsync_FullPersistenceRecreate_StoredLoginUnusable_CancelsHeadlessAttemptSilently()
    {
        var challenge = new CredentialChallenge
        {
            ChallengeId = "headless-chal-1",
            CredentialType = "username",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: challenge);
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)notifications;
        var logger = new CapturingLogger<SteamDaemonService>();
        var (_, daemon, _, _) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30), clientFactory: () => client, notifications: notifications, logger: logger);

        await daemon.StartAsync(CancellationToken.None);

        var attempt = daemon.LastHeadlessSelfAuthAttempt;
        Assert.NotNull(attempt);
        await attempt!;

        var session = SessionsOf(daemon).Values.Single(s => s.IsPersistent);
        Assert.Equal(1, client.StartLoginCallCount);
        Assert.Equal(1, client.CancelLoginCallCount);                        // cancelled daemon-side (acknowledged)
        Assert.Null(session.PendingLoginChallenge);                          // resume cache left empty
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);   // ordinary needs-login state

        // The daemon delivered the challenge over the real event channel too (production-shaped
        // fake); publication suppression must keep it away from every hub - no credential-challenge
        // event may reach a connected UI for an attempt nobody initiated.
        Assert.DoesNotContain(SignalREvents.CredentialChallenge, recorder.EventNames);

        // Single-warning contract: exactly one warning announces the unusable stored login.
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no usable stored login"));

        daemon.Dispose();
    }

    [Fact]
    public async Task StartAsync_FullPersistenceRecreate_DaemonReportsLoggedIn_IssuesNoLoginCommand()
    {
        // Anonymous daemons (Battle.net/Riot) - and any already-authenticated one - answer the single
        // status poll with logged-in, so no login command is ever issued.
        var client = new ScriptedLoginDaemonClient(alreadyLoggedIn: true);
        var (_, daemon, _, _) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30), clientFactory: () => client);

        await daemon.StartAsync(CancellationToken.None);

        var attempt = daemon.LastHeadlessSelfAuthAttempt;
        Assert.NotNull(attempt);
        await attempt!;

        Assert.Equal(0, client.StartLoginCallCount);
        Assert.Equal(0, client.CancelLoginCallCount);

        // The preflight status is applied through the ordinary status-reconciliation handler, so the
        // already-authenticated daemon is VISIBLY authenticated instead of sitting at needs-login
        // until it happens to push a status event.
        var session = SessionsOf(daemon).Values.Single(s => s.IsPersistent);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);

        daemon.Dispose();
    }

    [Theory]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task StartAsync_ReadoptedRunningContainer_NeverFiresHeadlessSelfAuth(PersistenceMode mode)
    {
        // A re-adopted RUNNING container's daemon process never died - its login (or lack of one) is its
        // current live state and needs no login command. The FakeReconnectDaemonClient throws on
        // StartLoginAsync, so an attempt here would also surface as an unexpected-call failure.
        var (_, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, expiresAt: DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        var daemon = new TestSteamDaemon(MakeDeps(dbFactory, sessionService, Config(mode, steamEnabled: true)), gateway);

        await daemon.StartAsync(CancellationToken.None);

        Assert.NotNull(SessionsOf(daemon).Values.SingleOrDefault(s => s.IsPersistent)); // re-adopted, not recreated
        Assert.Null(daemon.LastHeadlessSelfAuthAttempt);

        daemon.Dispose();
    }

    // The trigger is exactly "the fresh-login guard preserved the volume login": a MANUAL fresh Start
    // whose prior FullPersistence life was not explicitly stopped also fires - the admin should never
    // be asked to click Log in while a valid stored login sits on the volume.
    [Fact]
    public async Task CreateSession_ManualFreshStart_PreservedVolumeLogin_FiresHeadlessSelfAuth()
    {
        var client = new ScriptedLoginDaemonClient();
        var (_, daemon, _, _) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30), clientFactory: () => client);

        // Manual start path (the persistent Start endpoint), not the startup reconcile.
        await daemon.CreateSessionAsync(ScheduledPrefillConstants.DeriveSystemUserId(), isPersistent: true);

        var attempt = daemon.LastHeadlessSelfAuthAttempt;
        Assert.NotNull(attempt);
        await attempt!;

        var session = SessionsOf(daemon).Values.Single(s => s.IsPersistent);
        Assert.Equal(0, client.LogoutCount);                             // guard preserved, never erased
        Assert.Equal(1, client.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);

        daemon.Dispose();
    }

    // Explicit admin stop marks the row Terminated, so the guard ERASES the volume login - the
    // headless attempt must never fire and resurrect a login the admin deliberately removed.
    [Fact]
    public async Task CreateSession_ManualStartAfterExplicitStop_ErasesAndNeverFiresHeadlessSelfAuth()
    {
        var (_, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        await SeedPriorPersistentRowAsync(sessionService, PrefillSessionStatus.Terminated, expiresAt: DateTime.UtcNow.AddDays(30));

        var client = new ScriptedLoginDaemonClient();
        var daemon = new TestSteamDaemon(
            MakeDeps(dbFactory, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway(),
            () => client);

        await daemon.CreateSessionAsync(ScheduledPrefillConstants.DeriveSystemUserId(), isPersistent: true);

        Assert.Null(daemon.LastHeadlessSelfAuthAttempt);
        Assert.Equal(1, client.LogoutCount);        // fresh-login guard erased the inherited login
        Assert.Equal(0, client.StartLoginCallCount);

        daemon.Dispose();
    }

    // A manual Start that replaces an ERRORED persistent session is a verified-involuntary path (the
    // socket died, not the admin): under FullPersistence the guard preserves the volume login and the
    // headless attempt fires; under KillOnRestart/KeepAcrossRestart the guard erases and it must not.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, false)]
    [InlineData(PersistenceMode.KeepAcrossRestart, false)]
    [InlineData(PersistenceMode.FullPersistence, true)]
    public async Task CreateSession_ManualErroredReplacement_FiresHeadlessSelfAuthOnlyUnderFullPersistence(PersistenceMode mode, bool expectAttempt)
    {
        var (_, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        ScriptedLoginDaemonClient? createdClient = null;
        var daemon = new TestSteamDaemon(
            MakeDeps(dbFactory, sessionService, Config(mode, steamEnabled: true)),
            new RecordingContainerGateway(),
            () => createdClient = new ScriptedLoginDaemonClient());

        // An errored persistent predecessor (socket-death shape); the manual Start replaces it in place.
        var errored = InjectedPersistentSession(daemon, new ScriptedLoginDaemonClient());
        errored.Status = DaemonSessionStatus.Error;
        errored.ErrorMessage = "socket lost";

        await daemon.CreateSessionAsync(ScheduledPrefillConstants.DeriveSystemUserId(), isPersistent: true);

        Assert.NotNull(createdClient);
        if (expectAttempt)
        {
            var attempt = daemon.LastHeadlessSelfAuthAttempt;
            Assert.NotNull(attempt);
            await attempt!;
            Assert.Equal(0, createdClient!.LogoutCount);   // preserved (verified involuntary)
            Assert.Equal(1, createdClient.StartLoginCallCount);
        }
        else
        {
            Assert.Null(daemon.LastHeadlessSelfAuthAttempt);
            Assert.Equal(1, createdClient!.LogoutCount);   // erased (non-FullPersistence)
            Assert.Equal(0, createdClient.StartLoginCallCount);
        }

        daemon.Dispose();
    }

    [Fact]
    public async Task CreateSession_Guest_NeverFiresHeadlessSelfAuth()
    {
        var (_, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        ScriptedLoginDaemonClient? client = null;
        var daemon = new TestSteamDaemon(
            MakeDeps(dbFactory, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway(),
            () => client = new ScriptedLoginDaemonClient());

        await daemon.CreateSessionAsync(Guid.NewGuid(), sessionType: SessionType.Guest, isPersistent: false);

        Assert.Null(daemon.LastHeadlessSelfAuthAttempt);
        Assert.NotNull(client);
        Assert.Equal(0, client!.StartLoginCallCount);

        daemon.Dispose();
    }

    // The status-poll -> login -> cancel transaction owns LoginLock for its WHOLE duration: a
    // concurrent manual login either wins before the attempt begins (try-acquire) or starts only
    // after its cleanup completed - it can never observe or inherit the headless challenge.
    [Fact]
    public async Task AttemptHeadlessSelfAuth_OwnsLoginLockForWholeTransaction_ManualLoginNeverSeesItsChallenge()
    {
        var challenge = new CredentialChallenge
        {
            ChallengeId = "headless-race-1",
            CredentialType = "username",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: challenge)
        {
            HoldStartLogin = true,
            HoldCancelLogin = true,
            ChallengeOnlyOnFirstLogin = true
        };
        var (_, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var daemon = new TestSteamDaemon(
            MakeDeps(dbFactory, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway());
        var session = InjectedPersistentSession(daemon, client);

        var headless = daemon.AttemptHeadlessPersistentSelfAuthAsync(session);

        // Barrier 1: the attempt is inside the daemon login command and owns LoginLock.
        await client.StartLoginEntered.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => daemon.StartLoginAsync(session.Id));

        // Barrier 2: the login returned a challenge and the attempt is now inside the daemon cancel
        // round-trip. The lock must STILL be held - this is exactly the window where a manual login
        // could previously resume a challenge that was being cancelled out from under it.
        client.ReleaseStartLogin.SetResult();
        await client.CancelLoginEntered.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => daemon.StartLoginAsync(session.Id));
        Assert.Null(session.PendingLoginChallenge); // the headless challenge was never cached for a resume

        client.ReleaseCancelLogin.SetResult();
        await headless;

        // Cleanup finished: a manual login now starts a genuinely FRESH daemon attempt.
        var manual = await daemon.StartLoginAsync(session.Id);
        Assert.Null(manual);                            // not the headless attempt's challenge
        Assert.Equal(2, client.StartLoginCallCount);    // a new login command, not a resume
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);

        daemon.Dispose();
    }

    // Only a cancel the daemon ACKNOWLEDGED may present needs-login. An unconfirmed cancel means the
    // daemon may still hold the login in flight (it answers a repeat login with "already in progress"
    // WITHOUT re-emitting a challenge), so the honest state is an errored session the next start
    // replaces - never a needs-login that would wedge the admin's next interactive attempt.
    [Fact]
    public async Task AttemptHeadlessSelfAuth_CancelNotAcknowledged_MarksSessionErroredInsteadOfNeedsLogin()
    {
        var challenge = new CredentialChallenge
        {
            ChallengeId = "headless-cancelfail-1",
            CredentialType = "username",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: challenge) { CancelAcknowledged = false };
        var logger = new CapturingLogger<SteamDaemonService>();
        var (_, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var daemon = new TestSteamDaemon(
            MakeDeps(dbFactory, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway(),
            clientFactory: null,
            logger: logger);
        var session = InjectedPersistentSession(daemon, client);

        await daemon.AttemptHeadlessPersistentSelfAuthAsync(session);

        Assert.Equal(1, client.CancelLoginCallCount);
        Assert.Equal(DaemonSessionStatus.Error, session.Status);
        Assert.NotNull(session.ErrorMessage);
        Assert.NotEqual(DaemonAuthState.NotAuthenticated, session.AuthState); // needs-login never claimed
        Assert.Null(session.PendingLoginChallenge);
        Assert.Equal(1, logger.Entries.Count(e => e.Level == LogLevel.Error && e.Message.Contains("could not confirm daemon-side login cancellation")));

        daemon.Dispose();
    }

    // A daemon that neither authenticates nor challenges must not leave the session visibly stuck
    // LoggingIn: the attempt cancels (confirmed) and resets through the ordinary auth-state flow.
    [Fact]
    public async Task AttemptHeadlessSelfAuth_NoResponseFromDaemon_ConfirmedCancelResetsToNeedsLogin()
    {
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        var logger = new CapturingLogger<SteamDaemonService>();
        var (_, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var daemon = new TestSteamDaemon(
            MakeDeps(dbFactory, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway(),
            clientFactory: null,
            logger: logger);
        var session = InjectedPersistentSession(daemon, client);

        await daemon.AttemptHeadlessPersistentSelfAuthAsync(session);

        Assert.Equal(1, client.StartLoginCallCount);
        Assert.Equal(1, client.CancelLoginCallCount);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState); // never left stuck LoggingIn
        Assert.Equal(1, logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("got no response from the daemon")));

        daemon.Dispose();
    }

    // A session torn down (shutdown/terminate) between the trigger and the background task running:
    // the attempt must issue NO daemon commands at all.
    [Fact]
    public async Task AttemptHeadlessSelfAuth_SessionTornDown_IssuesNoCommands()
    {
        var client = new ScriptedLoginDaemonClient();
        var (_, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var daemon = new TestSteamDaemon(
            MakeDeps(dbFactory, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway());

        // Built but never registered - the shape of a session teardown won.
        var tornDown = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
            Status = DaemonSessionStatus.Active,
            IsPersistent = true,
            AuthState = DaemonAuthState.NotAuthenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Client = client
        };

        await daemon.AttemptHeadlessPersistentSelfAuthAsync(tornDown);

        Assert.Equal(0, client.GetStatusCallCount);
        Assert.Equal(0, client.StartLoginCallCount);
        Assert.Equal(0, client.CancelLoginCallCount);

        daemon.Dispose();
    }

    // The challenge GET/reconcile poll is a THIRD challenge-delivery channel (besides the event and
    // the login command's return value). The transports share ONE waiter slot between the login
    // command and the poll, so a poll reaching the transport mid-attempt would take over the slot
    // and receive the exact challenge the attempt is about to cancel. The service-level gate must
    // answer "no challenge" without ever touching the client. The fake deliberately keeps the
    // theft-capable pre-guard transport shape, so this pins the service gate on its own.
    [Fact]
    public async Task WaitForChallenge_PollDuringHeadlessAttempt_NeverReceivesTheHeadlessChallenge()
    {
        var challenge = new CredentialChallenge
        {
            ChallengeId = "headless-poll-1",
            CredentialType = "username",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: challenge) { HoldChallengeDispatch = true };
        var (_, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var daemon = new TestSteamDaemon(
            MakeDeps(dbFactory, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway());
        var session = InjectedPersistentSession(daemon, client);

        var headless = daemon.AttemptHeadlessPersistentSelfAuthAsync(session);

        // The attempt's login command has installed its waiter; the daemon's challenge is held back.
        await client.ChallengeDispatchReached.Task;

        // A poll arrives mid-attempt (the UI/reconcile GET surface).
        var pollTask = daemon.WaitForChallengeAsync(session.Id, TimeSpan.FromSeconds(2));

        client.ReleaseChallengeDispatch.SetResult();
        var poll = await pollTask;
        await headless;

        Assert.Null(poll);                               // the poll never receives the headless challenge
        Assert.Equal(0, client.WaitForChallengeCallCount); // ...and never reached the transport at all
        Assert.Equal(1, client.CancelLoginCallCount);    // the coordinator received and cancelled it
        Assert.Null(session.PendingLoginChallenge);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);

        daemon.Dispose();
    }

    // A poll that was ALREADY waiting inside the transport when the headless attempt began: the
    // login command's own waiter install supersedes the poll's, so the poll starves harmlessly to
    // its timeout while the coordinator receives and cancels the challenge.
    [Fact]
    public async Task WaitForChallenge_PollAlreadyWaitingWhenHeadlessAttemptStarts_StarvesWithoutAChallenge()
    {
        var challenge = new CredentialChallenge
        {
            ChallengeId = "headless-poll-2",
            CredentialType = "username",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: challenge);
        var (_, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var daemon = new TestSteamDaemon(
            MakeDeps(dbFactory, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway());
        var session = InjectedPersistentSession(daemon, client);

        // The poll enters FIRST (suppression not yet set) and installs its waiter in the transport.
        var pollTask = daemon.WaitForChallengeAsync(session.Id, TimeSpan.FromMilliseconds(600));
        await client.PollWaiterInstalled.Task;

        await daemon.AttemptHeadlessPersistentSelfAuthAsync(session);

        var poll = await pollTask;
        Assert.Null(poll);                             // starved to its timeout, no challenge
        Assert.Equal(1, client.CancelLoginCallCount);  // the coordinator, not the poll, got it and cancelled
        Assert.Null(session.PendingLoginChallenge);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);

        daemon.Dispose();
    }

    [Fact]
    public async Task CancelPrefillAsync_DaemonRejects_PreservesActiveSessionAndHistoryAsync()
    {
        var (_, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(
            dbFactory,
            NullLogger<PrefillSessionService>.Instance);
        var client = new FakeReconnectDaemonClient
        {
            CancelPrefillHandler = _ => Task.FromException(
                new InvalidOperationException("Daemon rejected cancellation"))
        };
        var daemon = new TestSteamDaemon(
            MakeDeps(
                dbFactory,
                sessionService,
                Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway());
        var session = InjectedPersistentSession(daemon, client);
        session.IsPrefilling = true;
        session.PrefillState = PrefillState.Downloading;
        session.PrefillStartedAt = DateTime.UtcNow.AddSeconds(-5);
        session.CurrentAppId = "730";
        session.CurrentAppName = "Counter-Strike 2";

        await sessionService.CreateSessionAsync(
            session.Id,
            session.UserId,
            $"container-{session.Id}",
            SteamPersistentContainerName,
            session.ExpiresAt);
        await sessionService.StartEntryAsync(session.Id, session.CurrentAppId, session.CurrentAppName);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => daemon.CancelPrefillAsync(session.Id));

        Assert.Equal(1, client.CancelPrefillCount);
        Assert.True(session.IsPrefilling);
        Assert.Equal(PrefillState.Downloading, session.PrefillState);
        Assert.Equal(0, session.TerminalCompletedFlag);
        Assert.Null(session.LastPrefillCompletedAt);
        Assert.Null(session.LastPrefillStatus);

        var history = Assert.Single(await sessionService.GetHistoryAsync(session.Id));
        Assert.Equal(PrefillHistoryEntryStatus.InProgress, history.Status);
        Assert.Null(history.CompletedAtUtc);

        daemon.Dispose();
    }

    [Fact]
    public async Task CancelPrefillAsync_WaitsForDaemonAcknowledgementBeforeTerminalMutationAsync()
    {
        var cancelEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancel = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var (_, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(
            dbFactory,
            NullLogger<PrefillSessionService>.Instance);
        var client = new FakeReconnectDaemonClient
        {
            CancelPrefillHandler = async cancellationToken =>
            {
                cancelEntered.TrySetResult();
                await releaseCancel.Task.WaitAsync(cancellationToken);
            }
        };
        var daemon = new TestSteamDaemon(
            MakeDeps(
                dbFactory,
                sessionService,
                Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway());
        var session = InjectedPersistentSession(daemon, client);
        session.IsPrefilling = true;
        session.PrefillState = PrefillState.Downloading;
        session.PrefillStartedAt = DateTime.UtcNow.AddSeconds(-5);
        session.CurrentAppId = "730";
        session.CurrentAppName = "Counter-Strike 2";

        await sessionService.CreateSessionAsync(
            session.Id,
            session.UserId,
            $"container-{session.Id}",
            SteamPersistentContainerName,
            session.ExpiresAt);
        await sessionService.StartEntryAsync(session.Id, session.CurrentAppId, session.CurrentAppName);

        var cancellation = daemon.CancelPrefillAsync(session.Id);
        await cancelEntered.Task;

        Assert.False(cancellation.IsCompleted);
        Assert.True(session.IsPrefilling);
        Assert.Equal(PrefillState.Downloading, session.PrefillState);
        Assert.Equal(0, session.TerminalCompletedFlag);
        var inProgressHistory = Assert.Single(await sessionService.GetHistoryAsync(session.Id));
        Assert.Equal(PrefillHistoryEntryStatus.InProgress, inProgressHistory.Status);

        releaseCancel.TrySetResult();
        await cancellation;

        Assert.Equal(1, client.CancelPrefillCount);
        Assert.False(session.IsPrefilling);
        Assert.Equal(PrefillState.Cancelled, session.PrefillState);
        Assert.Equal(1, session.TerminalCompletedFlag);
        Assert.Equal(PrefillProgressState.Cancelled.ToWireString(), session.LastPrefillStatus);
        Assert.NotNull(session.LastPrefillCompletedAt);

        var cancelledHistory = Assert.Single(await sessionService.GetHistoryAsync(session.Id));
        Assert.Equal(PrefillHistoryEntryStatus.Cancelled, cancelledHistory.Status);
        Assert.NotNull(cancelledHistory.CompletedAtUtc);

        daemon.Dispose();
    }

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    // ==================================================================================================
    // Helpers
    // ==================================================================================================

    private static (DbContextOptions<AppDbContext> Options, IDbContextFactory<AppDbContext> Factory) NewDatabase()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"prefill_orchestration_{Guid.NewGuid():N}")
            .Options;
        return (options, new PooledDbFactory(options));
    }

    private static async Task<PrefillSession?> GetRowAsync(DbContextOptions<AppDbContext> options, string sessionId)
    {
        await using var context = new AppDbContext(options);
        return await context.PrefillSessions.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == sessionId);
    }

    private static async Task<PrefillSession?> GetActivePersistentRowAsync(DbContextOptions<AppDbContext> options, PrefillPlatform platform)
    {
        await using var context = new AppDbContext(options);
        return await context.PrefillSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Platform == platform && s.IsPersistent && s.Status == PrefillSessionStatus.Active);
    }

    private static async Task<string> SeedActivePersistentRowAsync(
        PrefillSessionService sessionService, DateTime expiresAt, PrefillPlatform platform = PrefillPlatform.Steam, string containerName = SteamPersistentContainerName)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..16];
        await sessionService.CreateSessionAsync(
            sessionId, ScheduledPrefillConstants.DeriveSystemUserId(), $"container-{sessionId}", containerName, expiresAt, platform.ToString());
        return sessionId;
    }

    private static async Task SeedPriorPersistentRowAsync(PrefillSessionService sessionService, PrefillSessionStatus status, DateTime expiresAt)
    {
        var priorId = await SeedActivePersistentRowAsync(sessionService, expiresAt);
        switch (status)
        {
            case PrefillSessionStatus.Terminated:
                await sessionService.TerminateSessionAsync(priorId, "admin stop");
                break;
            case PrefillSessionStatus.Orphaned:
                await sessionService.MarkOrphansAsync(PrefillPlatform.Steam);
                break;
        }
    }

    private static FakeContainer RunningPersistentContainer(string sessionId, string containerName = SteamPersistentContainerName, PrefillPlatform platform = PrefillPlatform.Steam)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = containerName,
            Running = true,
            Labels =
            {
                ["lancache.prefill.persistent"] = "true",
                ["lancache.prefill.service"] = platform.ToString(),
                ["lancache.prefill.sessionId"] = sessionId,
                ["lancache.prefill.userId"] = ScheduledPrefillConstants.DeriveSystemUserId().ToString()
            },
            Env = { "PREFILL_SOCKET_SECRET=deadbeefsecret", "PREFILL_USE_SOCKET=true" }
        };

    private static FakeContainer StoppedPersistentContainer(string sessionId)
    {
        var c = RunningPersistentContainer(sessionId);
        c.Running = false;
        return c;
    }

    // Builds the shared per-recreate-test scenario: FullPersistence + Steam enabled, one prior persistent
    // Active row (with the given expiry) that cleanup will orphan, and an empty container inventory.
    private (DbContextOptions<AppDbContext> Options, TestSteamDaemon Daemon, RecordingContainerGateway Gateway, string PriorSessionId)
        SetupRecreateScenario(DateTime priorExpiry, int validityDays = 90, Func<IDaemonClient>? clientFactory = null, ISignalRNotificationService? notifications = null, ILogger<SteamDaemonService>? logger = null)
    {
        var (options, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var priorSessionId = SeedActivePersistentRowAsync(sessionService, priorExpiry).GetAwaiter().GetResult();

        var gateway = new RecordingContainerGateway();
        var config = Config(PersistenceMode.FullPersistence, steamEnabled: true);
        var daemon = new TestSteamDaemon(MakeDeps(dbFactory, sessionService, config, validityDays, notifications), gateway, clientFactory, logger);
        return (options, daemon, gateway, priorSessionId);
    }

    private static PrefillDaemonServiceBase BuildDaemon(PrefillPlatform platform, DaemonDeps deps, IPrefillContainerGateway gateway)
        => platform switch
        {
            PrefillPlatform.Steam => new TestSteamDaemon(deps, gateway),
            PrefillPlatform.Epic => new TestEpicDaemon(deps, gateway),
            PrefillPlatform.Xbox => new TestXboxDaemon(deps, gateway),
            PrefillPlatform.BattleNet => new TestBattleNetDaemon(deps, gateway),
            PrefillPlatform.Riot => new TestRiotDaemon(deps, gateway),
            _ => throw new ArgumentOutOfRangeException(nameof(platform))
        };

    private DaemonDeps MakeDeps(
        IDbContextFactory<AppDbContext> dbFactory, PrefillSessionService sessionService, ScheduledPrefillConfigDto config, int validityDays = 90,
        ISignalRNotificationService? notifications = null)
    {
        notifications ??= FakeInterface<ISignalRNotificationService>();
        var stateService = OrchestrationStateService.New(config, validityDays);
        var pathResolver = PathResolverProxy.New(NewTempRoot());
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        // A non-"auto" NetworkMode keeps the create path from probing Docker for lancache-dns.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Prefill:NetworkMode"] = "bridge" })
            .Build();
        var networkOptions = new StaticOptionsMonitor(new PrefillNetworkOptions { NetworkMode = "bridge" });
        return new DaemonDeps(notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions, new TestLancacheServerLocator());
    }

    private static ScheduledPrefillConfigDto Config(PersistenceMode global, bool steamEnabled)
    {
        var b = ScheduledPrefillConfigFactory.CreateDefault();
        var steam = new ScheduledPrefillServiceConfigDto
        {
            ServiceId = b.Steam.ServiceId,
            Enabled = steamEnabled,
            NotificationMode = b.Steam.NotificationMode,
            IntervalHours = b.Steam.IntervalHours,
            Preset = b.Steam.Preset,
            TopCount = b.Steam.TopCount,
            SelectedAppIds = b.Steam.SelectedAppIds,
            OperatingSystems = b.Steam.OperatingSystems,
            Force = b.Steam.Force,
            MaxConcurrency = b.Steam.MaxConcurrency,
            PersistenceMode = null
        };
        return new ScheduledPrefillConfigDto
        {
            Version = b.Version,
            MaxServiceRuntime = b.MaxServiceRuntime,
            StallTimeout = b.StallTimeout,
            Steam = steam,
            Epic = b.Epic,
            Xbox = b.Xbox,
            BattleNet = b.BattleNet,
            Riot = b.Riot,
            PersistenceMode = global
        };
    }

    private static ConcurrentDictionary<string, DaemonSession> SessionsOf(PrefillDaemonServiceBase daemon)
        => (ConcurrentDictionary<string, DaemonSession>)typeof(PrefillDaemonServiceBase)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(daemon)!;

    private static async Task InvokePrivateAsync(PrefillDaemonServiceBase daemon, string methodName, params object[] args)
    {
        var method = typeof(PrefillDaemonServiceBase).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{methodName} not found on {nameof(PrefillDaemonServiceBase)}");
        await (Task)method.Invoke(daemon, args)!;
    }

    private static T FakeInterface<T>() where T : class
        => (T)DispatchProxy.Create<T, NullReturningProxy>();

    private string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "lancache-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }

    // ---------- Test doubles ----------

    private sealed record DaemonDeps(
        ISignalRNotificationService Notifications,
        IConfiguration Configuration,
        IPathResolver PathResolver,
        IStateService StateService,
        PrefillSessionService SessionService,
        PrefillCacheService CacheService,
        IOptionsMonitor<PrefillNetworkOptions> NetworkOptions,
        ILancacheServerLocator Locator);

    private sealed class TestSteamDaemon : SteamDaemonService
    {
        private readonly Func<IDaemonClient>? _clientFactory;

        // The optional clientFactory lets the headless self-auth scenarios substitute a scripted
        // login client for the session the recreate path builds; everything else keeps the default
        // reconnect fake. The optional logger lets a test capture the warning/error contract.
        public TestSteamDaemon(DaemonDeps d, IPrefillContainerGateway g, Func<IDaemonClient>? clientFactory = null, ILogger<SteamDaemonService>? logger = null)
            : base(logger ?? NullLogger<SteamDaemonService>.Instance, d.Notifications, d.Configuration, d.PathResolver, d.StateService, d.SessionService, d.CacheService, d.NetworkOptions, d.Locator, new SingleContainerGatewayFactory(g))
            => _clientFactory = clientFactory;

        protected override IDaemonClient CreateDaemonClient(bool useTcpMode, int? tcpHostPort, string socketPath, string socketSecret)
            => _clientFactory?.Invoke() ?? new FakeReconnectDaemonClient();

        // Direct-injection seam for the concurrency/cancel scenarios that need a session without a
        // full create round-trip (mirrors the InjectSession seam in PersistentEraseOnStopTests.cs).
        public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;
    }

    // The Epic/Xbox mapping service is a game-catalog dependency the startup re-adopt path never touches
    // (the fake reconnect client reports null live status, so no auth hooks that would use it ever fire),
    // so it is passed null here rather than standing up its heavy dependency graph.
    private sealed class TestEpicDaemon : EpicPrefillDaemonService
    {
        public TestEpicDaemon(DaemonDeps d, IPrefillContainerGateway g)
            : base(NullLogger<EpicPrefillDaemonService>.Instance, d.Notifications, d.Configuration, d.PathResolver, d.StateService, d.SessionService, d.CacheService, null!, d.NetworkOptions, d.Locator, new SingleContainerGatewayFactory(g)) { }
        protected override IDaemonClient CreateDaemonClient(bool useTcpMode, int? tcpHostPort, string socketPath, string socketSecret)
            => new FakeReconnectDaemonClient();
    }

    private sealed class TestXboxDaemon : XboxPrefillDaemonService
    {
        public TestXboxDaemon(DaemonDeps d, IPrefillContainerGateway g)
            : base(NullLogger<XboxPrefillDaemonService>.Instance, d.Notifications, d.Configuration, d.PathResolver, d.StateService, d.SessionService, d.CacheService, null!, d.NetworkOptions, d.Locator, new SingleContainerGatewayFactory(g)) { }
        protected override IDaemonClient CreateDaemonClient(bool useTcpMode, int? tcpHostPort, string socketPath, string socketSecret)
            => new FakeReconnectDaemonClient();
    }

    private sealed class TestBattleNetDaemon : BattleNetDaemonService
    {
        public TestBattleNetDaemon(DaemonDeps d, IPrefillContainerGateway g)
            : base(NullLogger<BattleNetDaemonService>.Instance, d.Notifications, d.Configuration, d.PathResolver, d.StateService, d.SessionService, d.CacheService, d.NetworkOptions, d.Locator, new SingleContainerGatewayFactory(g)) { }
        protected override IDaemonClient CreateDaemonClient(bool useTcpMode, int? tcpHostPort, string socketPath, string socketSecret)
            => new FakeReconnectDaemonClient();
    }

    private sealed class TestRiotDaemon : RiotDaemonService
    {
        public TestRiotDaemon(DaemonDeps d, IPrefillContainerGateway g)
            : base(NullLogger<RiotDaemonService>.Instance, d.Notifications, d.Configuration, d.PathResolver, d.StateService, d.SessionService, d.CacheService, d.NetworkOptions, d.Locator, new SingleContainerGatewayFactory(g)) { }
        protected override IDaemonClient CreateDaemonClient(bool useTcpMode, int? tcpHostPort, string socketPath, string socketSecret)
            => new FakeReconnectDaemonClient();
    }

    private sealed class PooledDbFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public PooledDbFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppDbContext(_options));
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<PrefillNetworkOptions>
    {
        public StaticOptionsMonitor(PrefillNetworkOptions value) => CurrentValue = value;
        public PrefillNetworkOptions CurrentValue { get; }
        public PrefillNetworkOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<PrefillNetworkOptions, string?> listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    // IStateService stub returning a configurable scheduled-prefill config + persistent validity window,
    // and default/null for every other member (mirrors the DispatchProxy pattern in the sibling tests).
    private class OrchestrationStateService : DispatchProxy
    {
        private ScheduledPrefillConfigDto? _config;
        private int _validityDays;

        public static IStateService New(ScheduledPrefillConfigDto config, int validityDays)
        {
            var proxy = (IStateService)Create<IStateService, OrchestrationStateService>();
            var self = (OrchestrationStateService)(object)proxy;
            self._config = config;
            self._validityDays = validityDays;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillConfig))
            {
                return _config;
            }

            if (targetMethod?.Name == nameof(IStateService.GetAdminPersistentLoginValidityDays))
            {
                return _validityDays;
            }

            return DefaultReturn(targetMethod?.ReturnType);
        }

        internal static object? DefaultReturn(Type? returnType)
        {
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

    private class NullReturningProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => OrchestrationStateService.DefaultReturn(targetMethod?.ReturnType);
    }

    // Builds and registers a persistent session directly (no create round-trip), for the concurrency
    // and cancel scenarios that drive AttemptHeadlessPersistentSelfAuthAsync by hand.
    private static DaemonSession InjectedPersistentSession(TestSteamDaemon daemon, IDaemonClient client)
    {
        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
            Status = DaemonSessionStatus.Active,
            IsPersistent = true,
            AuthState = DaemonAuthState.NotAuthenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Client = client
        };
        daemon.InjectSession(session);
        return session;
    }

    // Minimal log capture so tests can assert the single-warning / loud-error contracts.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    // ISignalRNotificationService stub recording the event-name first argument of every hub push, so a
    // test can assert a specific event (e.g. a credential challenge) was never broadcast.
    private class EventRecordingNotificationsProxy : DispatchProxy
    {
        private readonly List<string> _eventNames = new();

        public IReadOnlyList<string> EventNames
        {
            get
            {
                lock (_eventNames)
                {
                    return _eventNames.ToList();
                }
            }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is [string eventName, ..])
            {
                lock (_eventNames)
                {
                    _eventNames.Add(eventName);
                }
            }

            return OrchestrationStateService.DefaultReturn(targetMethod?.ReturnType);
        }
    }

    // IPathResolver stub returning temp-dir-rooted paths, so the create path can make real bind-mount dirs.
    private class PathResolverProxy : DispatchProxy
    {
        private string _root = string.Empty;

        public static IPathResolver New(string root)
        {
            var proxy = (IPathResolver)Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)proxy)._root = root;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IPathResolver.ResolvePath):
                {
                    var relative = (string)args![0]!;
                    return Path.IsPathRooted(relative) ? relative : Path.Combine(_root, relative);
                }
                case nameof(IPathResolver.NormalizePath):
                    return (string)args![0]!;
            }

            if (targetMethod?.ReturnType == typeof(string))
            {
                return _root;
            }

            return OrchestrationStateService.DefaultReturn(targetMethod?.ReturnType);
        }
    }
}
