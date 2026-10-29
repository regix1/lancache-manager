using System.Collections.Concurrent;
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
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
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
public sealed partial class PrefillContainerOrchestrationTests : IDisposable
{
    private const string SteamPersistentContainerName = "steam-daemon-persistent";

    private readonly List<string> _tempRoots = new();

    // =============================================================
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

    // =============================================================
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

    // =============================================================
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

    // The recreate keeps a re-login date that already passed, with the sign-in time it belongs to, keeps the saved
    // login without using it, and the expiry pass then asks for a new sign-in.
    [Fact]
    public async Task StartAsync_FullPersistenceRecreate_KeepsAPastDateAndAsksForANewSignIn()
    {
        var priorExpiry = DateTime.UtcNow.AddDays(-5);
        var client = new ScriptedLoginDaemonClient();
        var (dbOptions, daemon, _, priorSessionId) = SetupRecreateScenario(priorExpiry, validityDays: 90, clientFactory: () => client);
        var prior = (await GetRowAsync(dbOptions, priorSessionId))!;

        await daemon.StartAsync(CancellationToken.None);
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt) await attempt.WaitAsync(TimeSpan.FromSeconds(15));
        await daemon.ProcessSessionExpiryAsync(DateTime.UtcNow);

        var active = await GetActivePersistentRowAsync(dbOptions, PrefillPlatform.Steam);
        Assert.NotNull(active);
        Assert.NotEqual(priorSessionId, active!.SessionId);
        Assert.Equal(priorExpiry, active.ExpiresAtUtc, TimeSpan.FromSeconds(5));
        Assert.Equal(prior.LastLoginAtUtc, active.LastLoginAtUtc);
        var session = daemon.GetActivePersistentSession()!;
        Assert.True(session.NeedsRelogin);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
        Assert.Equal(0, client.StartLoginCallCount);
        Assert.Equal(0, client.LogoutCount);

        daemon.Dispose();
    }

    // A recreate past its date still stores the log-off mark of the life it replaces on the new row.
    [Fact]
    public async Task StartAsync_FullPersistenceRecreate_PastItsDateKeepsTheLogOffMark()
    {
        var client = new ScriptedLoginDaemonClient();
        var (dbOptions, daemon, _, priorSessionId) = SetupRecreateScenario(DateTime.UtcNow.AddDays(-5), clientFactory: () => client);
        await using (var context = new AppDbContext(dbOptions))
        {
            var prior = await context.PrefillSessions.SingleAsync(row => row.SessionId == priorSessionId);
            prior.SignedOut = true;
            await context.SaveChangesAsync();
        }

        await daemon.StartAsync(CancellationToken.None);
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt) await attempt.WaitAsync(TimeSpan.FromSeconds(15));

        var session = daemon.GetActivePersistentSession()!;
        Assert.NotEqual(priorSessionId, session.Id);
        Assert.True(session.SignedOut);
        Assert.True((await GetRowAsync(dbOptions, session.Id))!.SignedOut);
        Assert.Equal(0, client.StartLoginCallCount);

        daemon.Dispose();
    }

    // The startup recreate reads the persistence mode once for the volume decision and the log-off mark.
    // A later read that would see another mode must not drop the mark or send a sign-in.
    [Fact]
    public async Task StartAsync_FullPersistenceRecreate_LaterModeChangeStillKeepsTheLogOffMark()
    {
        var client = new ScriptedLoginDaemonClient();
        var (dbOptions, daemon, _, priorSessionId) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30), clientFactory: () => client);
        await using (var context = new AppDbContext(dbOptions))
        {
            var prior = await context.PrefillSessions.SingleAsync(row => row.SessionId == priorSessionId);
            prior.SignedOut = true;
            await context.SaveChangesAsync();
        }

        var modes = ModeReadState.New(
            Config(PersistenceMode.FullPersistence, steamEnabled: true),
            Config(PersistenceMode.KillOnRestart, steamEnabled: true),
            fullReads: 2,
            validityDays: 90);
        typeof(PrefillDaemonServiceBase)
            .GetField("_stateService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(daemon, modes.Service);

        await daemon.StartAsync(CancellationToken.None);
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt) await attempt.WaitAsync(TimeSpan.FromSeconds(15));

        var session = daemon.GetActivePersistentSession()!;
        var row = await GetRowAsync(dbOptions, session.Id);
        Assert.Equal(0, client.LogoutCount);
        Assert.True(
            session.SignedOut && row!.SignedOut && client.StartLoginCallCount == 0 && modes.Reads == 2,
            $"signedOut={session.SignedOut} rowSignedOut={row?.SignedOut} startLogin={client.StartLoginCallCount} logout={client.LogoutCount} reads={modes.Reads}");

        daemon.Dispose();
    }

    // Gate leg: driving the re-adopt directly with a STOPPED container present exercises the gate's
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

    // =============================================================
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

    // =============================================================
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
    public async Task StartAsync_ReadoptedSignedInContainer_SendsNoLoginCommand(PersistenceMode mode)
    {
        // A re-adopted RUNNING container whose daemon still reports signed in needs no login command: the
        // startup sign-in reads its status and stops there.
        var (_, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, expiresAt: DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        var client = new ScriptedLoginDaemonClient(alreadyLoggedIn: true);
        var daemon = new TestSteamDaemon(MakeDeps(dbFactory, sessionService, Config(mode, steamEnabled: true)), gateway, () => client);

        await daemon.StartAsync(CancellationToken.None);
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt)
        {
            await attempt.WaitAsync(TimeSpan.FromSeconds(15));
        }

        var session = SessionsOf(daemon).Values.Single(s => s.IsPersistent); // re-adopted, not recreated
        Assert.Equal(sessionId, session.Id);
        Assert.Equal(0, client.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);

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

    // A Start that is canceled or cannot reach the daemon after its container is running removes that
    // container, so the next Start creates a fresh one and the saved login signs it in.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartPersistentSession_EndedBeforeRegistering_RemovesItsContainerSoTheNextStartSignsIn(bool canceled)
    {
        var client = new ScriptedLoginDaemonClient();
        var clients = new Queue<IDaemonClient>();
        if (!canceled)
        {
            clients.Enqueue(new FakeReconnectDaemonClient
            {
                ConnectHandler = _ => throw new FileNotFoundException("The daemon socket is missing.", "daemon.sock")
            });
        }
        clients.Enqueue(client);
        var (_, daemon, gateway, _) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30), clientFactory: clients.Dequeue);
        using var cts = new CancellationTokenSource();

        var first = daemon.StartPersistentSessionAsync(
            PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId(), cancellationToken: cts.Token);
        if (canceled)
        {
            await gateway.StartContainerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }
        else
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() => first);
        }

        var firstContainerId = Assert.Single(gateway.Removals).Id;
        Assert.False(gateway.ContainsContainer(firstContainerId));
        Assert.Empty(SessionsOf(daemon));

        var second = await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());
        var attempt = daemon.LastHeadlessSelfAuthAttempt;
        Assert.NotNull(attempt);
        await attempt!;

        Assert.Equal(2, gateway.CountOf("Create:"));
        Assert.NotEqual(firstContainerId, second.ContainerId);
        Assert.Single(gateway.Removals);
        Assert.Equal(1, client.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, second.AuthState);

        daemon.Dispose();
    }

    // Replacing an errored session is not a person's stop, so a Start that replaces it and then ends
    // before registering still leaves the saved login for the next Start to sign in with.
    [Fact]
    public async Task StartPersistentSession_ErroredReplacementEndedBeforeRegistering_NextStartKeepsTheSavedLogin()
    {
        var client = new ScriptedLoginDaemonClient();
        var (dbOptions, daemon, gateway, priorSessionId) = SetupRecreateScenario(
            DateTime.UtcNow.AddDays(30), clientFactory: () => client);
        daemon.InjectSession(new DaemonSession
        {
            Id = priorSessionId,
            UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
            Status = DaemonSessionStatus.Error,
            ErrorMessage = "socket lost",
            IsPersistent = true,
            AuthState = DaemonAuthState.Authenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Client = new ScriptedLoginDaemonClient()
        });
        using var cts = new CancellationTokenSource();

        var first = daemon.StartPersistentSessionAsync(
            PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId(), cancellationToken: cts.Token);
        await gateway.StartContainerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Empty(SessionsOf(daemon));

        var second = await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());
        Assert.Equal(0, client.LogoutCount);
        var attempt = daemon.LastHeadlessSelfAuthAttempt;
        Assert.NotNull(attempt);
        await attempt!;

        Assert.Equal(2, gateway.CountOf("Create:"));
        Assert.Equal(1, client.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, second.AuthState);
        Assert.Equal(PrefillSessionStatus.Orphaned, (await GetRowAsync(dbOptions, priorSessionId))?.Status);

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

    // A manual Start that replaces an ERRORED persistent session is not a person's stop (a lost socket,
    // or a sign-in or download cancel the daemon never confirmed): the errored daemon is never logged
    // out, under FullPersistence the guard preserves the volume login and the headless attempt fires;
    // under KillOnRestart/KeepAcrossRestart the guard erases and it must not.
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

        // An errored persistent predecessor whose daemon still answers; the manual Start replaces it in place.
        var erroredClient = new ScriptedLoginDaemonClient();
        var errored = InjectedPersistentSession(daemon, erroredClient);
        errored.Status = DaemonSessionStatus.Error;
        errored.ErrorMessage = "socket lost";

        await daemon.CreateSessionAsync(ScheduledPrefillConstants.DeriveSystemUserId(), isPersistent: true);

        Assert.Equal(0, erroredClient.LogoutCount);   // the replaced daemon keeps the saved login on the volume
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

    // Stop takes the session off every list first, so when Docker then fails the removal, nothing else
    // can end the downloads it was serving: Stop ends them itself and still reports the failure.
    [Fact]
    public async Task StopPersistentSession_RemovalFails_StillEndsTheRunningDownloads()
    {
        // Ending a run saves it inside a transaction, which the in-memory store only warns about.
        var database = new PooledDbFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"prefill_orchestration_{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var activity = DispatchProxy.Create<IActivityRegistry, ActivityReportRecorder>();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            gateway, activity: activity);
        var session = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        session.ContainerId = "container-a";
        gateway.AddContainer(new FakeContainer { Id = session.ContainerId, Name = SteamPersistentContainerName, Running = true });
        await sessionService.CreateSessionAsync(
            session.Id, session.UserId, session.ContainerId, SteamPersistentContainerName, session.ExpiresAt, DateTime.UtcNow);
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var run = new DaemonRun
        {
            PrefillRunId = runId,
            SessionId = session.Id,
            DaemonInstanceId = "instance",
            Options = new DaemonRunOptions(),
            Snapshot = new DaemonRunSnapshot
            {
                OperationId = runId.ToString(),
                DaemonInstanceId = "instance",
                StartedAt = now,
                UpdatedAt = now,
                State = "running"
            }
        };
        await sessionService.CreateRunAsync(run, "manual", CancellationToken.None);
        session.Runs[runId] = run;
        await sessionService.SaveRunAsync(run, run.Snapshot with { Sequence = 1, UpdatedAt = now },
            [new DaemonRunItem { AppId = "10", Sequence = 1, State = "downloading" }], false, CancellationToken.None);
        gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));

        var failure = await Assert.ThrowsAsync<ServiceUnavailableException>(() => daemon.StopPersistentSessionAsync(session.Id, "admin"));

        Assert.Equal("errors.prefill.containerRemovalFailed", failure.StageKey);
        Assert.True(run.Completion.Task.IsCompleted);
        Assert.Equal("cancelled", run.Snapshot.State);
        Assert.Equal("signalr.prefillSession.stopped", run.Snapshot.Reason);
        Assert.True(gateway.ContainsContainer(session.ContainerId));
        await using (var context = await database.CreateDbContextAsync())
        {
            var row = await context.PrefillHistoryEntries.SingleAsync(entry => entry.RunId == runId);
            Assert.Equal(PrefillHistoryEntryStatus.Cancelled, row.Status);
            Assert.Equal("signalr.prefillSession.stopped", row.Reason);
        }
        // The session stays listed with the cleanup error, so its downloading mark has to clear.
        var downloadingMark = $"{ActivityDomains.PrefillSession}/{session.Id}/{ActivityAspects.Downloading}=";
        Assert.Equal(downloadingMark + "False",
            ((ActivityReportRecorder)(object)activity).Reports.LastOrDefault(report => report.StartsWith(downloadingMark, StringComparison.Ordinal)));
    }

    // A run in its terminal publish is no longer downloading, so the session's mark turns off as the run ends.
    [Fact]
    public async Task TheDownloadingMarkCountsOnlyRunsStillDownloading()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var activity = DispatchProxy.Create<IActivityRegistry, ActivityReportRecorder>();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway(), activity: activity);
        var session = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var run = new DaemonRun
        {
            PrefillRunId = runId,
            SessionId = session.Id,
            DaemonInstanceId = "instance",
            Options = new DaemonRunOptions(),
            Snapshot = new DaemonRunSnapshot
            {
                OperationId = runId.ToString(),
                DaemonInstanceId = "instance",
                StartedAt = now,
                UpdatedAt = now,
                State = "running"
            },
            TerminalCompletedFlag = 1
        };
        session.Runs[runId] = run;
        var downloadingMark = $"{ActivityDomains.PrefillSession}/{session.Id}/{ActivityAspects.Downloading}=";

        await InvokePrivateAsync(daemon, "ReportSessionActivityAsync", session, true);
        Assert.Equal(downloadingMark + "False",
            ((ActivityReportRecorder)(object)activity).Reports.LastOrDefault(report => report.StartsWith(downloadingMark, StringComparison.Ordinal)));

        run.TerminalCompletedFlag = 0;
        await InvokePrivateAsync(daemon, "ReportSessionActivityAsync", session, true);
        Assert.Equal(downloadingMark + "True",
            ((ActivityReportRecorder)(object)activity).Reports.LastOrDefault(report => report.StartsWith(downloadingMark, StringComparison.Ordinal)));
    }

    // Docker refusing, dropping or timing out the removal leaves the container in place, so Stop answers
    // with the one key that says so instead of a canceled request or a generic server error.
    [Theory]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("unavailable")]
    public async Task StopRoute_RemovalFails_AnswersContainerRemovalFailed(string failure)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var deps = MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true));
        using var daemon = new TestSteamDaemon(deps, gateway);
        var session = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        session.ContainerId = "container-a";
        gateway.AddContainer(new FakeContainer { Id = session.ContainerId, Name = SteamPersistentContainerName, Running = true });
        switch (failure)
        {
            case "io": gateway.FailNextRemoveContainer(new IOException("simulated remove failure")); break;
            case "timeout": gateway.FailNextRemoveContainer(new TaskCanceledException()); break;
            case "unavailable":
                gateway.Reset();
                gateway.HoldGetVersion = true;
                break;
        }
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
        Assert.Equal("errors.prefill.containerRemovalFailed", answer.RootElement.GetProperty("stageKey").GetString());
        Assert.Equal("Steam", answer.RootElement.GetProperty("context").GetProperty("service").GetString());
        Assert.True(gateway.ContainsContainer(session.ContainerId));
    }

    [Fact]
    public async Task StartPersistentSession_ErroredReplacementRemovalFails_ThrowsContainerRemovalFailed()
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            gateway);
        var errored = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        errored.Status = DaemonSessionStatus.Error;
        errored.ContainerId = "container-a";
        gateway.AddContainer(new FakeContainer { Id = errored.ContainerId, Name = SteamPersistentContainerName, Running = true });
        await sessionService.CreateSessionAsync(
            errored.Id, errored.UserId, errored.ContainerId, SteamPersistentContainerName, errored.ExpiresAt, DateTime.UtcNow);
        gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));

        var failure = await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId()));

        Assert.Equal("errors.prefill.containerRemovalFailed", failure.StageKey);
        Assert.True(gateway.ContainsContainer(errored.ContainerId));
        // The session errored without a person's stop, so its row says so even though the removal failed.
        Assert.Equal(PrefillSessionStatus.Orphaned, (await GetRowAsync(options, errored.Id))!.Status);
    }

    // A Stop whose container removal failed can be pressed again: the second Stop runs the rest of the
    // teardown, and only then is the session gone.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopPersistentSession_AfterFailedRemoval_SecondStopFinishesTheTeardown(bool throughRoute)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var deps = MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true));
        using var daemon = new TestSteamDaemon(deps, gateway);
        var client = new FakeReconnectDaemonClient();
        var session = InjectedPersistentSession(daemon, client);
        session.ContainerId = "container-a";
        gateway.AddContainer(new FakeContainer { Id = session.ContainerId, Name = SteamPersistentContainerName, Running = true });
        await sessionService.CreateSessionAsync(
            session.Id, session.UserId, session.ContainerId, SteamPersistentContainerName, session.ExpiresAt, DateTime.UtcNow);
        var controller = new PersistentPrefillController(
            new SingleDaemonServiceProvider(daemon), deps.StateService, deps.CacheService,
            NullLogger<PersistentPrefillController>.Instance);
        gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));

        Assert.NotNull(await Record.ExceptionAsync(() => daemon.StopPersistentSessionAsync(session.Id, "admin")));
        if (throughRoute)
            Assert.IsType<OkResult>(await controller.StopAsync(new StopPersistentSessionRequest { SessionId = session.Id }));
        else
            await daemon.StopPersistentSessionAsync(session.Id, "admin");

        Assert.False(gateway.ContainsContainer(session.ContainerId));
        Assert.Equal(PrefillSessionStatus.Terminated, (await sessionService.GetSessionAsync(session.Id))!.Status);
        Assert.Equal(DaemonSessionStatus.Terminated, session.Status);
        Assert.Equal(2, client.LogoutCount);
        Assert.IsType<NotFoundObjectResult>(await controller.StopAsync(new StopPersistentSessionRequest { SessionId = session.Id }));
    }

    private static string? SessionEndedReasonKey(EventRecordingNotificationsProxy recorder)
    {
        var sent = Assert.Single(recorder.Arguments, call => call is [_, SignalREvents.SessionEnded, ..]);
        var property = sent[2]!.GetType().GetProperty("reasonKey");
        Assert.NotNull(property);
        return (string?)property.GetValue(sent[2]);
    }

    // The session-ended event names the stop by key beside its sentence, so the owner's log reads it in their language.
    [Fact]
    public async Task GuestSession_Stopped_SessionEndedCarriesTheStopKey()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true), notifications: notifications),
            new RecordingContainerGateway());
        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = Guid.NewGuid(),
            Status = DaemonSessionStatus.Active,
            IsTemporary = true,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Client = new FakeReconnectDaemonClient()
        };
        daemon.InjectSession(session);
        daemon.AddSubscriber(session.Id, "connection-1");
        await sessionService.CreateSessionAsync(
            session.Id, session.UserId, "guest-container", "guest", session.ExpiresAt, DateTime.UtcNow);

        await daemon.TerminateSessionAsync(session.Id);

        Assert.Equal("signalr.prefillSession.stopped", SessionEndedReasonKey((EventRecordingNotificationsProxy)(object)notifications));
    }

    // An image update retires the old container's session with its own key on the session-ended event.
    [Fact]
    public async Task ImageUpdate_RetiringTheOldSession_SessionEndedCarriesTheImageUpdatedKey()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var status = IdleStatus(Guid.NewGuid().ToString());
        var client = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true), notifications: notifications),
            gateway, () => client);
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
            Id = session.ContainerId, Name = session.ContainerName, Running = true, ImageId = session.ImageId
        });
        await sessions.CreateSessionAsync(
            session.Id, session.UserId, session.ContainerId, SteamPersistentContainerName, session.ExpiresAt, DateTime.UtcNow);
        daemon.AddSubscriber(session.Id, "connection-1");

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.Equal("signalr.prefillSession.imageUpdated", SessionEndedReasonKey((EventRecordingNotificationsProxy)(object)notifications));
    }

    // A person's Stop whose container removal failed still records the stop, so a manager restart under
    // FullPersistence with an enabled schedule does not bring the container back.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopPersistentSession_RemovalFailsThenRestart_DoesNotRecreateTheContainer(bool stoppedContainerListed)
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var config = Config(PersistenceMode.FullPersistence, steamEnabled: true);
        var gateway = new RecordingContainerGateway();
        var first = new TestSteamDaemon(MakeDeps(database, sessionService, config), gateway);
        var session = InjectedPersistentSession(first, new FakeReconnectDaemonClient());
        var container = gateway.AddContainer(RunningPersistentContainer(session.Id));
        session.ContainerId = container.Id;
        session.ContainerName = SteamPersistentContainerName;
        await sessionService.CreateSessionAsync(
            session.Id, session.UserId, container.Id, SteamPersistentContainerName, session.ExpiresAt, DateTime.UtcNow);
        gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => first.StopPersistentSessionAsync(session.Id, "admin"));
        await first.StopAsync(CancellationToken.None);
        first.Dispose();
        var restarted = stoppedContainerListed ? gateway : new RecordingContainerGateway();
        using var second = new TestSteamDaemon(
            MakeDeps(database, sessionService, config), restarted, () => new ScriptedLoginDaemonClient());

        await second.StartAsync(CancellationToken.None);

        Assert.Equal(0, restarted.CountOf("Create:"));
        Assert.Null(second.GetActivePersistentSession());
        Assert.Equal(PrefillSessionStatus.Terminated, (await GetRowAsync(options, session.Id))!.Status);
    }

    // A Stop whose graceful stop, kill and removal all fail leaves the container running; the next manager
    // start removes it instead of signing the stopped session back in, in every persistence mode.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task StopPersistentSession_ContainerLeftRunningThenRestart_RemovesItAndKeepsTheStop(PersistenceMode mode)
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var config = Config(mode, steamEnabled: true);
        var gateway = new RecordingContainerGateway();
        var first = new TestSteamDaemon(MakeDeps(database, sessionService, config), gateway);
        var session = InjectedPersistentSession(first, new FakeReconnectDaemonClient());
        var container = gateway.AddContainer(RunningPersistentContainer(session.Id));
        session.ContainerId = container.Id;
        session.ContainerName = SteamPersistentContainerName;
        await sessionService.CreateSessionAsync(
            session.Id, session.UserId, container.Id, SteamPersistentContainerName, session.ExpiresAt, DateTime.UtcNow);
        gateway.FailNextKillContainer(new DockerApiException(HttpStatusCode.InternalServerError, "cannot kill container"));
        gateway.FailNextRemoveContainer(new DockerApiException(HttpStatusCode.InternalServerError, "cannot remove a running container"));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => first.StopPersistentSessionAsync(session.Id, "admin"));
        // The recording gateway's stop always succeeds; Docker refused that one too.
        container.Running = true;
        await first.StopAsync(CancellationToken.None);
        first.Dispose();
        using var second = new TestSteamDaemon(MakeDeps(database, sessionService, config), gateway);

        await second.StartAsync(CancellationToken.None);

        Assert.False(gateway.ContainsContainer(container.Id));
        Assert.Null(second.GetActivePersistentSession());
        Assert.Equal(0, gateway.CountOf("Create:"));
        Assert.Equal(PrefillSessionStatus.Terminated, (await GetRowAsync(options, session.Id))!.Status);
    }

    // A Start after a Stop that left the container running, and a restart whose removal of it also failed,
    // removes that container and creates a fresh session instead of signing the stopped one back in.
    [Fact]
    public async Task StartPersistentSession_StoppedSessionContainerStillRunning_RemovesItAndCreatesFresh()
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var config = Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true);
        var gateway = new RecordingContainerGateway();
        var first = new TestSteamDaemon(MakeDeps(database, sessionService, config), gateway);
        var session = InjectedPersistentSession(first, new FakeReconnectDaemonClient());
        var container = gateway.AddContainer(RunningPersistentContainer(session.Id));
        session.ContainerId = container.Id;
        session.ContainerName = SteamPersistentContainerName;
        await sessionService.CreateSessionAsync(
            session.Id, session.UserId, container.Id, SteamPersistentContainerName, session.ExpiresAt, DateTime.UtcNow);
        gateway.FailNextKillContainer(new DockerApiException(HttpStatusCode.InternalServerError, "cannot kill container"));
        gateway.FailNextRemoveContainer(new DockerApiException(HttpStatusCode.InternalServerError, "cannot remove a running container"));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => first.StopPersistentSessionAsync(session.Id, "admin"));
        // The recording gateway's stop always succeeds; Docker refused that one too.
        container.Running = true;
        await first.StopAsync(CancellationToken.None);
        first.Dispose();
        var newClient = new ScriptedLoginDaemonClient();
        using var second = new TestSteamDaemon(MakeDeps(database, sessionService, config), gateway, () => newClient);
        gateway.FailNextRemoveContainer(new DockerApiException(HttpStatusCode.InternalServerError, "cannot remove a running container"));
        await second.StartAsync(CancellationToken.None);
        Assert.True(gateway.ContainsContainer(container.Id));

        var started = await second.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());

        Assert.NotEqual(session.Id, started.Id);
        Assert.False(gateway.ContainsContainer(container.Id));
        Assert.Equal(PrefillSessionStatus.Terminated, (await GetRowAsync(options, session.Id))!.Status);
        // The stopped session's saved login is erased from the new container.
        Assert.Equal(1, newClient.LogoutCount);
    }

    // A persistent create that finds the stopped session's container still running says so in the log,
    // with the session id, instead of reporting an adopt that was never tried.
    [Fact]
    public async Task CreatePersistent_StoppedSessionContainerStillRunning_LogsTheStopNotAFailedAdopt()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(30));
        await sessionService.TerminateSessionAsync(sessionId, "admin stop");
        var gateway = new RecordingContainerGateway();
        var container = gateway.AddContainer(RunningPersistentContainer(sessionId));
        var logger = new CapturingLogger<SteamDaemonService>();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)),
            gateway, () => new ScriptedLoginDaemonClient(), logger);

        await daemon.CreateSessionAsync(ScheduledPrefillConstants.DeriveSystemUserId(), isPersistent: true);

        Assert.False(gateway.ContainsContainer(container.Id));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains($"its session {sessionId} was stopped", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("did not produce an active session", StringComparison.Ordinal));
    }

    // A Start after an errored session's replacement whose removal Docker did not finish runs that stored
    // teardown again before it creates, so the old session's client is released and its row keeps the login.
    [Fact]
    public async Task StartPersistentSession_AfterFailedErroredReplacement_FinishesThatTeardown()
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)), gateway,
            () => new ScriptedLoginDaemonClient());
        var erroredClient = new FakeReconnectDaemonClient();
        var errored = InjectedPersistentSession(daemon, erroredClient);
        var container = gateway.AddContainer(RunningPersistentContainer(errored.Id));
        errored.ContainerId = container.Id;
        errored.ContainerName = SteamPersistentContainerName;
        errored.Status = DaemonSessionStatus.Error;
        await sessionService.CreateSessionAsync(
            errored.Id, errored.UserId, errored.ContainerId, SteamPersistentContainerName, errored.ExpiresAt, DateTime.UtcNow, PrefillPlatform.Steam.ToString());
        gateway.FailNextKillContainer(RecordingContainerGateway.NoAnswer());
        gateway.HoldRemoveContainer = true;
        Assert.NotNull(await Record.ExceptionAsync(() =>
            daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId())));
        gateway.HoldRemoveContainer = false;

        var started = await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());

        Assert.NotEqual(errored.Id, started.Id);
        Assert.Null(daemon.GetFailedTeardownSession(errored.Id));
        Assert.True(erroredClient.Disposed);
        Assert.False(gateway.ContainsContainer(container.Id));
        Assert.Equal(PrefillSessionStatus.Orphaned, (await GetRowAsync(options, errored.Id))!.Status);
    }

    // An own session whose container create Docker answers too late, after it made the container, removes
    // that container with its volumes and the session's folder in the same failing call.
    [Fact]
    public async Task OwnSessionCreateTimesOut_RemovesTheContainerItMadeAndItsFolder()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        gateway.FailCreatedContainer(RecordingContainerGateway.NoAnswer());
        var deps = MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true));
        using var daemon = new TestSteamDaemon(deps, gateway);

        await Assert.ThrowsAsync<DockerTimeoutException>(() => daemon.CreateSessionAsync(Guid.NewGuid()));

        Assert.Equal(0, gateway.ContainerCount);
        Assert.True(Assert.Single(gateway.Removals).Parameters.RemoveVolumes);
        Assert.Empty(Directory.GetDirectories(Path.Combine(deps.PathResolver.GetPrefillDirectory(), "sessions")));
    }

    // A persistent create whose container does not start removes the container and the session's folder.
    [Fact]
    public async Task PersistentCreateStartFails_RemovesItsSessionFolder()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        gateway.FailNextStartContainer(new IOException("simulated start failure"));
        var deps = MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true));
        using var daemon = new TestSteamDaemon(deps, gateway);

        await Assert.ThrowsAsync<IOException>(() =>
            daemon.CreateSessionAsync(ScheduledPrefillConstants.DeriveSystemUserId(), isPersistent: true));

        Assert.Equal(0, gateway.ContainerCount);
        Assert.Empty(Directory.GetDirectories(Path.Combine(deps.PathResolver.GetPrefillDirectory(), "sessions")));
    }

    // A Stop whose removal fails reports the platform's container presence again, so the status dot stops
    // reading a running, signed-in container for a session that left every list.
    [Fact]
    public async Task StopPersistentSession_RemovalFails_ReportsThePlatformAgain()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var activity = DispatchProxy.Create<IActivityRegistry, ActivityReportRecorder>();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)),
            gateway, activity: activity);
        var session = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        var container = gateway.AddContainer(RunningPersistentContainer(session.Id));
        session.ContainerId = container.Id;
        session.ContainerName = SteamPersistentContainerName;
        session.AuthState = DaemonAuthState.Authenticated;
        await sessionService.CreateSessionAsync(
            session.Id, session.UserId, container.Id, SteamPersistentContainerName, session.ExpiresAt, DateTime.UtcNow);
        gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => daemon.StopPersistentSessionAsync(session.Id, "admin"));

        var reports = ((ActivityReportRecorder)(object)activity).Reports;
        Assert.Contains($"{ActivityDomains.PersistentContainer}/steam/{ActivityAspects.Running}=False", reports);
        Assert.Contains($"{ActivityDomains.PersistentContainer}/steam/{ActivityAspects.Authenticated}=False", reports);
    }

    // A scheduler tick with Docker not answering adopts each due platform once: the run reuses the tick's
    // adopt, so it waits on Docker once and still reports Docker as unavailable on its card.
    [Fact]
    public async Task ScheduledTick_DockerNeverAnswers_AdoptsOncePerPlatform()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway { HoldListContainers = true };
        var config = Config(PersistenceMode.KeepAcrossRestart, true);
        var due = new ScheduledPrefillSchedule
        {
            Id = config.Steam.Schedules[0].Id,
            Name = "Daily",
            Enabled = true,
            IntervalHours = -1,
            NotificationMode = NotificationMode.Silent,
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Auto }
        };
        config.Steam.Schedules.Clear();
        config.Steam.Schedules.Add(due);
        var deps = MakeDeps(database, sessions, config);
        using var daemon = new TestSteamDaemon(deps, gateway);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, StageKeyRecorder>();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);
        using var services = new ServiceCollection()
            .AddSingleton<SteamDaemonService>(daemon)
            .AddSingleton<IUnifiedOperationTracker>(tracker)
            .AddSingleton(notifications)
            .BuildServiceProvider();
        using var scheduler = new ScheduledPrefillService(NullLogger<ScheduledPrefillService>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(), deps.StateService);
        var execute = typeof(ScheduledPrefillService).GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await ((Task)execute.Invoke(scheduler, [CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(10));
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(1, gateway.CountOf("List"));
        Assert.Contains("errors.prefill.dockerUnavailable", ((StageKeyRecorder)(object)notifications).StageKeys);
    }

    // A Start after a failed Stop first finishes that Stop's teardown, so the old session's history,
    // client and event are closed before a new container comes up; when Docker fails again, the Start
    // answers the removal failure and creates nothing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartPersistentSession_AfterFailedStop_FinishesTheOldTeardownFirst(bool removalFailsAgain)
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)(object)notifications;
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true), notifications: notifications),
            gateway,
            () => new ScriptedLoginDaemonClient());
        var client = new FakeReconnectDaemonClient();
        var session = InjectedPersistentSession(daemon, client);
        var container = gateway.AddContainer(RunningPersistentContainer(session.Id));
        session.ContainerId = container.Id;
        session.ContainerName = SteamPersistentContainerName;
        await sessionService.CreateSessionAsync(
            session.Id, session.UserId, container.Id, SteamPersistentContainerName, session.ExpiresAt, DateTime.UtcNow);
        gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => daemon.StopPersistentSessionAsync(session.Id, "admin"));

        if (removalFailsAgain)
        {
            gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));
            var failure = await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
                daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId()));
            Assert.Equal("errors.prefill.containerRemovalFailed", failure.StageKey);
            Assert.Equal(0, gateway.CountOf("Create:"));
            Assert.Equal(2, client.LogoutCount);
        }
        else
        {
            await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());
            Assert.Equal(PrefillSessionStatus.Terminated, (await GetRowAsync(options, session.Id))!.Status);
            Assert.True(client.Disposed);
            Assert.Null(daemon.GetFailedTeardownSession(session.Id));
            Assert.Single(recorder.EventNames, name => name == SignalREvents.DaemonSessionTerminated);
        }
    }

    // A container left from an earlier run that Docker refuses to remove, or never answers for, fails the
    // Start with the key that says the container was not removed.
    [Theory]
    [InlineData("refused")]
    [InlineData("stalled")]
    public async Task StartRoute_LeftoverContainerRemovalFails_AnswersContainerRemovalFailed(string failure)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var deps = MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true));
        using var daemon = new TestSteamDaemon(deps, gateway);
        var leftover = RunningPersistentContainer(Guid.NewGuid().ToString("N")[..16]);
        leftover.Running = false;
        gateway.AddContainer(leftover);
        if (failure == "refused")
            gateway.FailNextRemoveContainer(new DockerApiException(HttpStatusCode.InternalServerError, "driver failed"));
        else
            gateway.HoldRemoveContainer = true;
        var controller = new PersistentPrefillController(
            new SingleDaemonServiceProvider(daemon), deps.StateService, deps.CacheService,
            NullLogger<PersistentPrefillController>.Instance);
        var middleware = new GlobalExceptionMiddleware(
            async _ => await controller.StartAsync(
                new StartPersistentSessionRequest { Service = PrefillPlatform.Steam }, CancellationToken.None),
            NullLogger<GlobalExceptionMiddleware>.Instance,
            DispatchProxy.Create<IHostEnvironment, NullReturningProxy>());
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        using var answer = JsonDocument.Parse(body.ToArray());
        Assert.Equal("errors.prefill.containerRemovalFailed", answer.RootElement.GetProperty("stageKey").GetString());
        Assert.True(gateway.ContainsContainer(leftover.Id));
        Assert.Equal(0, gateway.CountOf("Create:"));
    }

    // Clear stored logins keeps going after one service's container removal fails, and reports that
    // service's login as not cleared.
    [Fact]
    public async Task ClearAllPersistentLogins_OneRemovalFails_ReportsEveryService()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var config = Config(PersistenceMode.FullPersistence, steamEnabled: true);
        var steamGateway = new RecordingContainerGateway();
        var epicGateway = new RecordingContainerGateway();
        using var steam = new TestSteamDaemon(MakeDeps(database, sessionService, config), steamGateway);
        using var epic = new TestEpicDaemon(MakeDeps(database, sessionService, config), epicGateway);
        var steamSession = InjectedPersistentSession(steam, new ScriptedLogoutDaemonClient(succeeds: false));
        steamSession.ContainerId = "steam-container";
        steamGateway.AddContainer(new FakeContainer { Id = "steam-container", Name = SteamPersistentContainerName, Running = true });
        var epicSession = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
            Status = DaemonSessionStatus.Active,
            IsPersistent = true,
            ContainerId = "epic-container",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Client = new ScriptedLogoutDaemonClient(succeeds: false)
        };
        SessionsOf(epic)[epicSession.Id] = epicSession;
        epicGateway.AddContainer(new FakeContainer { Id = "epic-container", Name = "epic-container", Running = true });
        steamGateway.FailNextRemoveContainer(new IOException("simulated remove failure"));
        var services = new ServiceCollection()
            .AddSingleton<SteamDaemonService>(steam)
            .AddSingleton<EpicPrefillDaemonService>(epic)
            .BuildServiceProvider();

        var results = await PrefillDaemonServiceBase.ClearAllPersistentLoginsAsync(services, CancellationToken.None);

        Assert.Equal(new[] { PrefillPlatform.Steam, PrefillPlatform.Epic }, results.Select(row => row.Service));
        Assert.False(results[0].Success);
        Assert.Equal("hard-remove-failed", results[0].Detail);
        Assert.True(results[1].Success);
        Assert.Equal("hard-removed", results[1].Detail);
        Assert.False(epicGateway.ContainsContainer("epic-container"));
    }

    // A stopped service whose leftover container or login volume Docker refuses, or never answers for,
    // still gets its row, and the next service's login is cleared.
    [Theory]
    [InlineData("refused")]
    [InlineData("stalled")]
    [InlineData("volume")]
    public async Task ClearAllPersistentLogins_StoppedServiceRemovalFails_ClearsTheNextService(string failure)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var config = Config(PersistenceMode.FullPersistence, steamEnabled: true);
        var steamGateway = new RecordingContainerGateway();
        var epicGateway = new RecordingContainerGateway();
        using var steam = new TestSteamDaemon(MakeDeps(database, sessionService, config), steamGateway);
        using var epic = new TestEpicDaemon(MakeDeps(database, sessionService, config), epicGateway);
        steamGateway.AddContainer(new FakeContainer { Id = "steam-stopped", Name = SteamPersistentContainerName, Running = false });
        switch (failure)
        {
            case "refused": steamGateway.FailNextRemoveContainer(new IOException("simulated remove failure")); break;
            case "stalled": steamGateway.HoldRemoveContainer = true; break;
            case "volume": steamGateway.FailNextRemoveVolume(new DockerApiException(HttpStatusCode.InternalServerError, "volume driver failed")); break;
        }
        if (failure is "refused" or "stalled")
        {
            // The container that stayed still mounts the login volume, so Docker refuses to remove it.
            steamGateway.FailNextRemoveVolume(new DockerApiException(HttpStatusCode.Conflict, "volume is in use"));
        }
        var services = new ServiceCollection()
            .AddSingleton<SteamDaemonService>(steam)
            .AddSingleton<EpicPrefillDaemonService>(epic)
            .BuildServiceProvider();

        var results = await PrefillDaemonServiceBase.ClearAllPersistentLoginsAsync(services, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(25));

        Assert.Equal(new[] { PrefillPlatform.Steam, PrefillPlatform.Epic }, results.Select(row => row.Service));
        Assert.False(results[0].WasRunning);
        Assert.False(results[0].Success);
        Assert.Equal(failure == "volume" ? "remove-failed" : "InUse", results[0].Detail);
        Assert.True(results[1].Success);
        Assert.Equal(1, epicGateway.CountOf("RemoveVolume:"));
    }

    // Clear stored logins does not wait on Docker when Docker accepts a stopped service's container list or
    // volume removal and never answers. A list that times out is logged and the volume removal still runs;
    // a volume removal that times out reports Docker as unavailable for that service. The next service runs.
    [Theory]
    [InlineData("list")]
    [InlineData("volume")]
    public async Task ClearAllPersistentLogins_DockerCallNeverAnswers_ClearsTheNext(string call)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var config = Config(PersistenceMode.FullPersistence, steamEnabled: true);
        var steamGateway = new RecordingContainerGateway { HoldListContainers = call == "list", HoldRemoveVolume = call == "volume" };
        var epicGateway = new RecordingContainerGateway();
        using var steam = new TestSteamDaemon(MakeDeps(database, sessionService, config), steamGateway);
        using var epic = new TestEpicDaemon(MakeDeps(database, sessionService, config), epicGateway);
        var services = new ServiceCollection()
            .AddSingleton<SteamDaemonService>(steam)
            .AddSingleton<EpicPrefillDaemonService>(epic)
            .BuildServiceProvider();

        var results = await PrefillDaemonServiceBase.ClearAllPersistentLoginsAsync(services, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { PrefillPlatform.Steam, PrefillPlatform.Epic }, results.Select(row => row.Service));
        Assert.Equal(call == "list", results[0].Success);
        Assert.Equal(call == "list" ? "Removed" : "DockerUnavailable", results[0].Detail);
        Assert.True(results[1].Success);
        Assert.Equal(1, epicGateway.CountOf("RemoveVolume:"));
    }

    // Clear stored logins after a person's Stop whose removal failed finishes that Stop's teardown, so the
    // card that kept the Stop's error hears the session end.
    [Fact]
    public async Task ClearAllPersistentLogins_AfterFailedStop_FinishesTheStopsTeardown()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)(object)notifications;
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true), notifications: notifications),
            gateway);
        var session = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        var container = gateway.AddContainer(RunningPersistentContainer(session.Id));
        session.ContainerId = container.Id;
        session.ContainerName = SteamPersistentContainerName;
        await sessionService.CreateSessionAsync(
            session.Id, session.UserId, container.Id, SteamPersistentContainerName, session.ExpiresAt, DateTime.UtcNow);
        gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => daemon.StopPersistentSessionAsync(session.Id, "admin"));
        var services = new ServiceCollection().AddSingleton<SteamDaemonService>(daemon).BuildServiceProvider();

        var results = await PrefillDaemonServiceBase.ClearAllPersistentLoginsAsync(services, CancellationToken.None);

        Assert.True(Assert.Single(results).Success);
        Assert.False(gateway.ContainsContainer(container.Id));
        Assert.Null(daemon.GetFailedTeardownSession(session.Id));
        Assert.Single(recorder.EventNames, name => name == SignalREvents.DaemonSessionTerminated);
    }

    // A persistent Start whose container fails to start answers that start failure even when Docker refuses
    // or never answers the cleanup removal, and frees the service for a Stop.
    [Theory]
    [InlineData("refused")]
    [InlineData("stalled")]
    public async Task StartPersistentSession_ContainerFailsToStartAndRemovalFails_AnswersTheStartFailure(string failure)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)), gateway);
        gateway.FailNextStartContainer(new DockerApiException(HttpStatusCode.InternalServerError, "start failed"));
        if (failure == "refused")
            gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));
        else
            gateway.HoldRemoveContainer = true;

        var error = await Record.ExceptionAsync(() => daemon.StartPersistentSessionAsync(
            PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId()).WaitAsync(TimeSpan.FromSeconds(25)));

        var startFailure = Assert.IsType<DockerApiException>(error);
        Assert.Contains("start failed", startFailure.Message);
        await daemon.StopPersistentSessionAsync("not-listed", "admin").WaitAsync(TimeSpan.FromSeconds(5));
    }

    // A create whose name stays taken after the retries, and whose by-name removal Docker refuses or reports
    // as already in progress, answers that the container was not removed instead of a generic error.
    [Theory]
    [InlineData("refused")]
    [InlineData("inprogress")]
    public async Task StartRoute_NameConflictRemovalFails_AnswersContainerRemovalFailed(string failure)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway { CreateConflicts = 4 };
        var deps = MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true));
        using var daemon = new TestSteamDaemon(deps, gateway);
        gateway.AddContainer(new FakeContainer { Id = "name-holder", Name = SteamPersistentContainerName, Running = false });
        gateway.FailNextRemoveContainer(failure == "refused"
            ? new DockerApiException(HttpStatusCode.InternalServerError, "driver failed")
            : new DockerApiException(HttpStatusCode.Conflict, "removal of container name-holder is already in progress"));
        var controller = new PersistentPrefillController(
            new SingleDaemonServiceProvider(daemon), deps.StateService, deps.CacheService,
            NullLogger<PersistentPrefillController>.Instance);
        var middleware = new GlobalExceptionMiddleware(
            async _ => await controller.StartAsync(
                new StartPersistentSessionRequest { Service = PrefillPlatform.Steam }, CancellationToken.None),
            NullLogger<GlobalExceptionMiddleware>.Instance,
            DispatchProxy.Create<IHostEnvironment, NullReturningProxy>());
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        using var answer = JsonDocument.Parse(body.ToArray());
        Assert.Equal("errors.prefill.containerRemovalFailed", answer.RootElement.GetProperty("stageKey").GetString());
        Assert.Equal(0, gateway.CountOf("Create:"));
    }

    // Manager startup does not wait on Docker for a leftover container Docker never removes.
    [Fact]
    public async Task StartAsync_LeftoverContainerRemovalStalls_Finishes()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway { HoldRemoveContainer = true };
        var leftover = gateway.AddContainer(StoppedPersistentContainer(sessionId));
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.KillOnRestart, steamEnabled: true)), gateway);

        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(35));

        Assert.True(gateway.ContainsContainer(leftover.Id));
        Assert.Null(daemon.GetActivePersistentSession());
    }

    // Manager startup does not wait on Docker for a leftover running container whose stop never answers:
    // the stop gives up, says so, and the forced removal still runs.
    [Fact]
    public async Task StartAsync_LeftoverContainerStopStalls_LogsItAndRemovesTheContainer()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway { HoldStopContainer = true };
        var leftover = gateway.AddContainer(new FakeContainer { Id = "leftover-guest", Name = "steam-daemon-guest", Running = true });
        var logger = new CapturingLogger<SteamDaemonService>();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.KillOnRestart, steamEnabled: true)), gateway, logger: logger);

        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(25));

        Assert.False(gateway.ContainsContainer(leftover.Id));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains(leftover.Id[..12]));
    }

    // A Docker that does not answer the startup version check in time but answers later does not leave
    // prefill off until the next manager restart: the next persistent Start connects again and starts.
    [Fact]
    public async Task StartAsync_VersionCheckTimesOut_NextPersistentStartConnectsAndStarts()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway(available: false) { HoldGetVersion = true };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)), gateway,
            () => new ScriptedLoginDaemonClient());
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gateway.IsAvailable);
        gateway.HoldGetVersion = false;

        var started = await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());

        Assert.True(gateway.IsAvailable);
        Assert.Equal(started.Id, daemon.GetActivePersistentSession()?.Id);
        Assert.Equal(1, gateway.CountOf("Create:"));
    }

    // The same late Docker does not refuse a guest's create either.
    [Fact]
    public async Task StartAsync_VersionCheckTimesOut_NextGuestCreateConnectsAndStarts()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway(available: false) { HoldGetVersion = true };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)), gateway,
            () => new ScriptedLoginDaemonClient());
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        gateway.HoldGetVersion = false;

        var created = await daemon.CreateSessionAsync(await SeedGuestAsync(database), sessionType: SessionType.Guest, isPersistent: false);

        Assert.True(gateway.IsAvailable);
        Assert.False(created.IsPersistent);
        Assert.Equal(1, gateway.CountOf("Create:"));
    }

    // A Docker still silent at the Start answers that Docker is unavailable after the second try.
    [Fact]
    public async Task StartPersistentSession_DockerStillSilent_AnswersDockerUnavailable()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway(available: false) { HoldGetVersion = true };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)), gateway);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId()));

        Assert.Equal("errors.prefill.dockerUnavailable", error.StageKey);
        Assert.False(gateway.IsAvailable);
        Assert.Equal(0, gateway.CountOf("Create:"));
    }

    // A startup container list that times out leaves the running persistent container alone, and the next
    // Start adopts it as the same session without creating a container.
    [Fact]
    public async Task StartAsync_ContainerListTimesOut_NextStartAdoptsTheRunningContainer()
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway { HoldListContainers = true };
        var container = gateway.AddContainer(RunningPersistentContainer(sessionId));
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)), gateway);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(daemon.GetActivePersistentSession());
        Assert.True(gateway.IsAvailable);
        gateway.HoldListContainers = false;

        var started = await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());

        Assert.Equal(sessionId, started.Id);
        Assert.True(gateway.ContainsContainer(container.Id));
        Assert.Equal(0, gateway.CountOf("Create:"));
        Assert.Equal(PrefillSessionStatus.Active, (await GetRowAsync(options, sessionId))!.Status);
    }

    // A startup re-adopt inspect that times out does not remove the running, signed-in persistent container;
    // the next Start adopts it as the same session.
    [Fact]
    public async Task StartAsync_ReadoptInspectTimesOut_KeepsTheContainerAndNextStartAdoptsIt()
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway { HoldInspectContainer = true };
        var container = gateway.AddContainer(RunningPersistentContainer(sessionId));
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)), gateway);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gateway.ContainsContainer(container.Id));
        Assert.Equal(0, gateway.CountOf("Remove:"));
        gateway.HoldInspectContainer = false;

        var started = await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());

        Assert.Equal(sessionId, started.Id);
        Assert.True(gateway.ContainsContainer(container.Id));
        Assert.Equal(0, gateway.CountOf("Create:"));
        Assert.Equal(PrefillSessionStatus.Active, (await GetRowAsync(options, sessionId))!.Status);
    }

    // A startup image inspect that times out ends startup at once, and the next Start succeeds.
    [Fact]
    public async Task StartAsync_ImageInspectTimesOut_FinishesAndNextStartStarts()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway { OnImageInspect = () => throw new TimeoutException("Docker did not answer within 10 seconds.") };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)), gateway,
            () => new ScriptedLoginDaemonClient());
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gateway.IsAvailable);
        Assert.Equal(0, gateway.CountOf("List"));
        gateway.OnImageInspect = null;

        var started = await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());

        Assert.Equal(started.Id, daemon.GetActivePersistentSession()?.Id);
        Assert.Equal(1, gateway.CountOf("Create:"));
    }

    // A Start whose adopt inspect times out answers that Docker is unavailable and keeps the running,
    // signed-in container: nothing is removed, created or signed out.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task StartRoute_AdoptInspectTimesOut_AnswersDockerUnavailableAndKeepsTheContainer(PersistenceMode mode)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway { HoldInspectContainer = true };
        var container = gateway.AddContainer(RunningPersistentContainer(sessionId));
        var clients = new List<FakeReconnectDaemonClient>();
        var deps = MakeDeps(database, sessionService, Config(mode, steamEnabled: true));
        using var daemon = new TestSteamDaemon(deps, gateway, () =>
        {
            var client = new FakeReconnectDaemonClient();
            clients.Add(client);
            return client;
        });
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var (status, stageKey) = await StartRouteAsync(daemon, deps);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Equal("errors.prefill.dockerUnavailable", stageKey);
        Assert.True(gateway.ContainsContainer(container.Id));
        Assert.Equal(0, gateway.CountOf("Remove:"));
        Assert.Equal(0, gateway.CountOf("Create:"));
        Assert.All(clients, client => Assert.Equal(0, client.LogoutCount));
    }

    // A startup image check whose container inspect times out after the re-adopt keeps the Docker client,
    // so the person's Stop still removes the container and signs it out.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task StartAsync_ImageCheckInspectTimesOut_StopStillRemovesTheContainer(PersistenceMode mode)
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(17));
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        var container = RunningPersistentContainer(sessionId);
        container.ImageId = "sha256:image-a";
        gateway.AddContainer(container);
        TestSteamDaemon? readopting = null;
        var daemonInstanceId = Guid.NewGuid().ToString();
        var client = new FakeReconnectDaemonClient
        {
            // The image check asks the adopted session for its status right before it inspects the container.
            StatusHandler = _ =>
            {
                if (readopting?.GetActivePersistentSession() is not null)
                {
                    gateway.HoldInspectContainer = true;
                }
                return Task.FromResult<DaemonStatus?>(IdleStatus(daemonInstanceId));
            }
        };
        using var daemon = new TestSteamDaemon(MakeDeps(database, sessionService, Config(mode, steamEnabled: true)), gateway, () => client);
        readopting = daemon;
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        gateway.HoldInspectContainer = false;
        Assert.Equal(sessionId, daemon.GetActivePersistentSession()?.Id);
        Assert.True(gateway.IsAvailable);

        await daemon.StopPersistentSessionAsync(sessionId, "admin");

        Assert.False(gateway.ContainsContainer(container.Id));
        Assert.Equal(1, client.LogoutCount);
        Assert.Equal(PrefillSessionStatus.Terminated, (await GetRowAsync(options, sessionId))!.Status);
    }

    // A Start whose container create or start Docker does not answer says Docker is unavailable, and
    // removes only the container this Start created, never the other containers it found.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, "create")]
    [InlineData(PersistenceMode.KillOnRestart, "start")]
    [InlineData(PersistenceMode.KeepAcrossRestart, "create")]
    [InlineData(PersistenceMode.KeepAcrossRestart, "start")]
    [InlineData(PersistenceMode.FullPersistence, "create")]
    [InlineData(PersistenceMode.FullPersistence, "start")]
    public async Task StartRoute_CreateOrStartTimesOut_AnswersDockerUnavailable(PersistenceMode mode, string call)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var bystander = gateway.AddContainer(new FakeContainer { Id = "epic-guest-bystander", Name = "epic-daemon-guest", Running = true });
        var deps = MakeDeps(database, sessionService, Config(mode, steamEnabled: true));
        using var daemon = new TestSteamDaemon(deps, gateway);
        if (call == "create")
        {
            gateway.FailNextCreateContainer(RecordingContainerGateway.NoAnswer());
        }
        else
        {
            gateway.FailNextStartContainer(RecordingContainerGateway.NoAnswer());
        }

        var (status, stageKey) = await StartRouteAsync(daemon, deps);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Equal("errors.prefill.dockerUnavailable", stageKey);
        Assert.True(gateway.ContainsContainer(bystander.Id));
        var created = gateway.FindByName(SteamPersistentContainerName);
        Assert.All(gateway.Removals, removal => Assert.NotEqual(bystander.Id, removal.Id));
        Assert.Equal(call == "create" ? 0 : 1, gateway.CountOf("Remove:"));
        Assert.Null(created);
        Assert.Null(daemon.GetActivePersistentSession());
    }

    // A scheduled run after a startup whose container list timed out adopts the running persistent
    // container, as a Start does, and runs on it. Without a container, or for a stopped session's
    // container, the run still reports that no container is running and nothing is adopted or created;
    // a stopped session's container is removed, as the startup re-adopt removes it.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, "none")]
    [InlineData(PersistenceMode.KeepAcrossRestart, "running")]
    [InlineData(PersistenceMode.FullPersistence, "running")]
    [InlineData(PersistenceMode.KeepAcrossRestart, "terminated")]
    [InlineData(PersistenceMode.FullPersistence, "terminated")]
    public async Task ScheduledRun_AfterStartupListTimeout_AdoptsTheRunningContainer(PersistenceMode mode, string container)
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway { HoldListContainers = true };
        if (container != "none")
        {
            gateway.AddContainer(RunningPersistentContainer(sessionId));
        }
        if (container == "terminated")
        {
            await sessionService.TerminateSessionAsync(sessionId, "admin stop");
        }
        var client = new FakeReconnectDaemonClient
        {
            StatusHandler = _ => Task.FromResult<DaemonStatus?>(new DaemonStatus { Status = "logged-in" }),
            SelectionHandler = (_, _) => Task.CompletedTask
        };
        var deps = MakeDeps(database, sessionService, Config(mode, steamEnabled: true));
        using var daemon = new TestSteamDaemon(deps, gateway, () => client);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(daemon.GetActivePersistentSession());
        gateway.HoldListContainers = false;
        var serviceConfig = new ScheduledPrefillServiceConfigDto
        {
            ServiceId = PrefillPlatform.Steam,
            ScheduleId = ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam),
            ScheduleName = "Default",
            Enabled = true,
            NotificationMode = NotificationMode.Silent,
            IntervalHours = 24,
            Preset = ScheduledPrefillPreset.All,
            SelectedAppIds = new List<string>(),
            OperatingSystems = new List<ScheduledPrefillOperatingSystem> { ScheduledPrefillOperatingSystem.Windows },
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Auto }
        };
        var serviceRun = new ScheduledPrefillServiceRun(
            serviceConfig, Guid.Empty, "op-1", "run-1",
            new ScheduledPrefillServiceRunState(
                serviceConfig.ServiceId, serviceConfig.ScheduleId, serviceConfig.ScheduleName,
                new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled)),
            CancellationToken.None);
        using var schedulerServices = new ServiceCollection().BuildServiceProvider();
        using var scheduler = new ScheduledPrefillService(NullLogger<ScheduledPrefillService>.Instance,
            schedulerServices.GetRequiredService<IServiceScopeFactory>(), deps.StateService);
        var runService = typeof(ScheduledPrefillService).GetMethod("RunServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var run = (Task<ScheduledPrefillServiceRunResult>)runService.Invoke(scheduler,
            [serviceRun, new SingleDaemonServiceProvider(daemon), FakeInterface<ISignalRNotificationService>(), Config(mode, steamEnabled: true), null])!;
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, gateway.CountOf("Create:"));
        Assert.Equal(container == "terminated" ? 1 : 0, gateway.CountOf("Remove:"));
        Assert.Equal(container == "running" ? 1 : 0, gateway.ContainerCount);
        if (container == "running")
        {
            Assert.Equal(sessionId, daemon.GetActivePersistentSession()?.Id);
            // The fake daemon answers a status with no protocol version, so the adopted container is refused as outdated.
            Assert.Equal(ScheduledPrefillServiceRunResult.Unavailable, result);
        }
        else
        {
            Assert.Null(daemon.GetActivePersistentSession());
            Assert.Equal(ScheduledPrefillServiceRunResult.NeedsLogin, result);
        }
        if (container != "none")
        {
            Assert.Equal(container == "terminated" ? PrefillSessionStatus.Terminated : PrefillSessionStatus.Active,
                (await GetRowAsync(options, sessionId))!.Status);
        }
    }

    // A Clear after a startup whose version check timed out connects again and removes the stored login.
    [Fact]
    public async Task ClearLogins_AfterStartupVersionTimeout_ConnectsAndRemovesTheVolume()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway(available: false) { HoldGetVersion = true };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)), gateway);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        gateway.HoldGetVersion = false;

        var results = await PrefillDaemonServiceBase.ClearAllPersistentLoginsAsync(
            new SingleDaemonServiceProvider(daemon), CancellationToken.None);

        var steam = Assert.Single(results, result => result.Service == PrefillPlatform.Steam);
        Assert.True(steam.Success);
        Assert.Equal("Removed", steam.Detail);
        Assert.Equal(1, gateway.CountOf("RemoveVolume:"));
    }

    // A Start that finds Docker unconnected while another caller's connect is in flight makes no Docker call
    // until that connect ends, then answers from its result: Docker unavailable, or the adopted container.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, false)]
    [InlineData(PersistenceMode.KillOnRestart, true)]
    [InlineData(PersistenceMode.KeepAcrossRestart, false)]
    [InlineData(PersistenceMode.KeepAcrossRestart, true)]
    [InlineData(PersistenceMode.FullPersistence, false)]
    [InlineData(PersistenceMode.FullPersistence, true)]
    public async Task StartRoute_DuringAnotherConnect_WaitsForThatConnect(PersistenceMode mode, bool dockerAnswers)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway(available: false) { HoldGetVersion = true };
        var container = gateway.AddContainer(RunningPersistentContainer(sessionId));
        var clients = new List<FakeReconnectDaemonClient>();
        var deps = MakeDeps(database, sessionService, Config(mode, steamEnabled: true));
        using var daemon = new TestSteamDaemon(deps, gateway, () =>
        {
            var client = new FakeReconnectDaemonClient();
            clients.Add(client);
            return client;
        });
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        gateway.HoldVersionAnswer = true;
        var first = daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());
        await gateway.VersionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var callsBefore = gateway.CountOf("");

        var second = StartRouteAsync(daemon, deps);
        await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(500)));
        var callsDuringConnect = gateway.CountOf("");
        var finishedDuringConnect = second.IsCompleted;
        gateway.HoldGetVersion = !dockerAnswers;
        gateway.ReleaseVersion.TrySetResult();
        await Record.ExceptionAsync(() => first);
        var (status, stageKey) = await second;

        Assert.False(finishedDuringConnect);
        Assert.Equal(callsBefore, callsDuringConnect);
        Assert.Equal(2, gateway.CountOf("Version"));
        Assert.Equal(0, gateway.CountOf("Create:"));
        Assert.All(clients, client => Assert.Equal(0, client.LogoutCount));
        Assert.True(gateway.ContainsContainer(container.Id));
        if (dockerAnswers)
        {
            Assert.Equal(StatusCodes.Status200OK, status);
            Assert.Equal(sessionId, daemon.GetActivePersistentSession()?.Id);
        }
        else
        {
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
            Assert.Equal("errors.prefill.dockerUnavailable", stageKey);
        }
    }

    // A guest create during another caller's connect that times out says Docker is unavailable and creates
    // nothing.
    [Fact]
    public async Task GuestCreate_DuringAConnectThatTimesOut_SaysDockerIsUnavailable()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway(available: false) { HoldGetVersion = true };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)), gateway,
            () => new ScriptedLoginDaemonClient());
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var guestId = await SeedGuestAsync(database);
        gateway.HoldVersionAnswer = true;
        var first = daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());
        await gateway.VersionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var guest = daemon.CreateSessionAsync(guestId, sessionType: SessionType.Guest, isPersistent: false);
        await Task.WhenAny(guest, Task.Delay(TimeSpan.FromMilliseconds(500)));
        gateway.ReleaseVersion.TrySetResult();

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => first);
        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => guest);
        Assert.StartsWith("Docker is not running or not reachable.", error.Message);
        Assert.Equal("errors.prefill.dockerUnavailable", error.StageKey);
        Assert.Equal(0, gateway.ContainerCount);
    }

    // A manager self-inspect that times out fails that create and caches no host data path, so the next
    // create binds the real host mount. Linux only: on Windows the data path is the local data directory.
    [Fact]
    public async Task StartPersistentSession_OwnContainerInspectTimesOut_NextCreateBindsTheMountedHostFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)), gateway,
            () => new ScriptedLoginDaemonClient());
        typeof(PrefillDaemonServiceBase).GetField("_isRunningInContainer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(daemon, true);
        var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
        Environment.SetEnvironmentVariable("HOSTNAME", "manager-container-0001");
        try
        {
            var ownId = (string)typeof(PrefillDaemonServiceBase)
                .GetMethod("GetOwnContainerId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(daemon, null)!;
            gateway.AddContainer(new FakeContainer
            {
                Id = ownId,
                Name = "lancache-manager",
                Running = true,
                Mounts = [new Docker.DotNet.Models.MountPoint { Destination = "/data", Source = "/mnt/host/data" }]
            });
            gateway.HoldInspectContainer = true;
            await Assert.ThrowsAsync<DockerTimeoutException>(() =>
                daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId()));
            gateway.HoldInspectContainer = false;

            await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());

            var binds = Assert.Single(gateway.CreatedParameters).HostConfig.Binds;
            Assert.Contains(binds, bind => bind.StartsWith("/mnt/host/data/", StringComparison.Ordinal));
            Assert.DoesNotContain(binds, bind => bind.StartsWith("/data/", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOSTNAME", hostname);
        }
    }

    // A Start whose container list or image inspect times out answers that Docker is unavailable.
    [Theory]
    [InlineData("list")]
    [InlineData("image")]
    public async Task StartRoute_DockerCallTimesOut_AnswersDockerUnavailable(string call)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var deps = MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true));
        using var daemon = new TestSteamDaemon(deps, gateway);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        if (call == "list")
        {
            gateway.HoldListContainers = true;
        }
        else
        {
            gateway.OnImageInspect = () => throw RecordingContainerGateway.NoAnswer();
        }

        var (status, stageKey) = await StartRouteAsync(daemon, deps);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Equal("errors.prefill.dockerUnavailable", stageKey);
        Assert.Equal(0, gateway.CountOf("Create:"));
    }

    // A person's Stop whose removal failed lists no row for that session, so no page draws the stopped
    // container as running or its canceled runs as active.
    [Fact]
    public async Task ListRoute_AfterFailedStop_ListsNoRowForTheStoppedSession()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var config = Config(PersistenceMode.FullPersistence, steamEnabled: true);
        var deps = MakeDeps(database, sessionService, config);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(deps, gateway);
        var session = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        var container = gateway.AddContainer(RunningPersistentContainer(session.Id));
        session.ContainerId = container.Id;
        session.ContainerName = SteamPersistentContainerName;
        await sessionService.CreateSessionAsync(
            session.Id, session.UserId, container.Id, SteamPersistentContainerName, session.ExpiresAt, DateTime.UtcNow);
        gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => daemon.StopPersistentSessionAsync(session.Id, "admin"));

        var controller = new PersistentPrefillController(
            new SingleDaemonServiceProvider(daemon), deps.StateService, deps.CacheService,
            NullLogger<PersistentPrefillController>.Instance);
        var answer = Assert.IsType<OkObjectResult>((await controller.ListAsync(CancellationToken.None)).Result);
        Assert.Empty(Assert.IsType<List<PersistentPrefillSessionDto>>(answer.Value));
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

        await daemon.CreateSessionAsync(await SeedGuestAsync(dbFactory), sessionType: SessionType.Guest, isPersistent: false);

        Assert.Null(daemon.LastHeadlessSelfAuthAttempt);
        Assert.NotNull(client);
        Assert.Equal(0, client!.StartLoginCallCount);

        daemon.Dispose();
    }

    // The status-poll -> login -> cancel transaction owns LoginLock for its WHOLE duration: a
    // concurrent manual login either wins before the attempt begins or starts only after its cleanup
    // completed - it can never observe or inherit the headless challenge. A manual login that arrives
    // mid-transaction waits out its bounded lock wait and is then refused with ConflictException, the
    // type the middleware renders as 409 with the message intact.
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
        await Assert.ThrowsAsync<ConflictException>(() => daemon.StartLoginAsync(session.Id));

        // Barrier 2: the login returned a challenge and the attempt is inside the daemon cancel round-trip.
        // The lock must still be held here, or a manual login could resume a challenge being canceled under it.
        client.ReleaseStartLogin.SetResult();
        await client.CancelLoginEntered.Task;
        await Assert.ThrowsAsync<ConflictException>(() => daemon.StartLoginAsync(session.Id));
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
        Assert.Equal(1, client.CancelLoginCallCount);    // the coordinator received and canceled it
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
        Assert.Equal(1, client.CancelLoginCallCount);  // the coordinator, not the poll, got it and canceled
        Assert.Null(session.PendingLoginChallenge);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);

        daemon.Dispose();
    }

    [Fact]
    public async Task RunFailedGames_ReadsStoredRunOfStoppedSessionAndHidesOtherService()
    {
        var (dbOptions, contexts) = NewDatabase();
        var sessionService = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var deps = MakeDeps(contexts, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true));
        var runId = Guid.NewGuid();
        await using (var context = new AppDbContext(dbOptions))
        {
            context.PrefillSessions.Add(new PrefillSession
            {
                SessionId = "stopped-persistent", CreatedBySessionId = Guid.NewGuid(), IsPersistent = true,
                Platform = PrefillPlatform.Steam, Status = PrefillSessionStatus.Terminated,
                CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow, EndedAtUtc = DateTime.UtcNow
            });
            context.PrefillRuns.Add(new PrefillRun
            {
                Id = runId, SessionId = "stopped-persistent", DaemonInstanceId = "instance",
                OptionsJson = "{}", State = "failed", CompletedAtUtc = DateTime.UtcNow,
                SnapshotJson = System.Text.Json.JsonSerializer.Serialize(new DaemonRunSnapshot
                    { OperationId = runId.ToString(), DaemonInstanceId = "instance", State = "failed" })
            });
            context.PrefillHistoryEntries.Add(new PrefillHistoryEntry
            {
                RunId = runId, SessionId = "stopped-persistent", AppId = "10", AppName = "Failed game",
                Status = PrefillHistoryEntryStatus.Failed, Reason = "auth-lost", StartedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }
        var services = new ServiceCollection();
        services.AddSingleton(sessionService);
        using var container = services.BuildServiceProvider();
        var controller = new PersistentPrefillController(container, deps.StateService, deps.CacheService,
            NullLogger<PersistentPrefillController>.Instance);

        var found = await controller.GetRunFailedGamesAsync(runId, PrefillPlatform.Steam, CancellationToken.None);
        var missing = await controller.GetRunFailedGamesAsync(runId, PrefillPlatform.Epic, CancellationToken.None);

        var games = Assert.IsType<List<PrefillRunFailedGame>>(Assert.IsType<OkObjectResult>(found.Result).Value);
        var game = Assert.Single(games);
        Assert.Equal("Failed game", game.Name);
        Assert.Equal("errors.prefill.signInLost", game.ReasonKey);
        var notFound = Assert.IsType<NotFoundResponse>(Assert.IsType<NotFoundObjectResult>(missing.Result).Value);
        Assert.Equal("errors.prefill.runNotFound", notFound.StageKey);
    }

    [Fact]
    public async Task Start_BodyWithOldEditSessionId_StartsThePlainSessionAsync()
    {
        var (dbOptions, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var deps = MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true));
        var daemon = new TestSteamDaemon(deps, gateway);
        var controller = new PersistentPrefillController(
            new SingleDaemonServiceProvider(daemon),
            deps.StateService,
            deps.CacheService,
            NullLogger<PersistentPrefillController>.Instance);
        // Same settings as the MVC JSON options in Program.cs, so an unknown member is skipped as it is in production.
        var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var request = System.Text.Json.JsonSerializer.Deserialize<StartPersistentSessionRequest>(
            """{"service":"Steam","editSessionId":"old-edit"}""",
            jsonOptions)!;

        var result = await controller.StartAsync(request, CancellationToken.None);

        var started = Assert.IsType<DaemonSessionDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var session = SessionsOf(daemon).Values.Single(s => s.IsPersistent);
        Assert.Equal(session.Id, started.Id);
        Assert.Equal(1, gateway.ContainerCount);
        Assert.Equal(session.Id, (await GetActivePersistentRowAsync(dbOptions, PrefillPlatform.Steam))!.SessionId);

        daemon.Dispose();
    }

    // A scheduled run while an image update waits on its new container's daemon resumes that update, as a
    // person's Start does: the new container keeps the stored expiry and signs in from the stored login.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task ScheduledAdopt_DuringPendingImageUpdate_ResumesTheUpdate(PersistenceMode mode)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var status = IdleStatus(Guid.NewGuid().ToString());
        var oldClient = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) };
        var unreachableClient = new FakeReconnectDaemonClient
        {
            ConnectHandler = _ => Task.FromException(new IOException("The daemon socket did not answer."))
        };
        var successorClient = new ScriptedLoginDaemonClient();
        var clients = new Queue<IDaemonClient>([unreachableClient, successorClient]);
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        using var daemon = new TestSteamDaemon(MakeDeps(database, sessions, Config(mode, true)), gateway, clients.Dequeue);
        var session = InjectedPersistentSession(daemon, oldClient);
        session.ContainerId = "container-a";
        session.ContainerName = SteamPersistentContainerName;
        session.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        session.ImageId = "sha256:image-a";
        session.Capabilities = status;
        session.LoginSettled = true;
        session.AuthState = DaemonAuthState.Authenticated;
        gateway.AddContainer(new FakeContainer { Id = session.ContainerId, Name = session.ContainerName, Running = true, ImageId = session.ImageId });
        Assert.NotNull(await Record.ExceptionAsync(() => daemon.ReconcilePersistentImageAsync(CancellationToken.None)));
        Assert.Null(daemon.GetActivePersistentSession());

        var adopted = await daemon.AdoptRunningPersistentSessionAsync(CancellationToken.None);
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt)
        {
            await attempt.WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.NotNull(adopted);
        Assert.Equal(session.ExpiresAt, adopted!.ExpiresAt);
        Assert.Equal(1, successorClient.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, adopted.AuthState);
        Assert.Equal(1, gateway.CountOf("Create:"));
    }

    // A scheduled run after an image update whose old container Docker did not kill finishes that update:
    // the old container goes, one new container is created, and no session is left without its container.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task ScheduledAdopt_AfterImageUpdateKillFailed_FinishesTheUpdate(PersistenceMode mode)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var status = IdleStatus(Guid.NewGuid().ToString());
        var oldClient = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) };
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(mode, true)), gateway, () => new ScriptedLoginDaemonClient());
        var session = InjectedPersistentSession(daemon, oldClient);
        var old = RunningPersistentContainer(session.Id);
        old.ImageId = "sha256:image-a";
        gateway.AddContainer(old);
        session.ContainerId = old.Id;
        session.ContainerName = SteamPersistentContainerName;
        session.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        session.ImageId = old.ImageId;
        session.Capabilities = status;
        session.LoginSettled = true;
        session.AuthState = DaemonAuthState.Authenticated;
        gateway.FailNextKillContainer(RecordingContainerGateway.NoAnswer());
        gateway.HoldRemoveContainer = true;
        Assert.NotNull(await Record.ExceptionAsync(() => daemon.ReconcilePersistentImageAsync(CancellationToken.None)));
        gateway.HoldRemoveContainer = false;

        await daemon.AdoptRunningPersistentSessionAsync(CancellationToken.None);
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt)
        {
            await attempt.WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.Equal(1, gateway.CountOf("Create:"));
        Assert.False(gateway.ContainsContainer(old.Id));
        var survivor = Assert.Single(SessionsOf(daemon).Values, registered => registered.IsPersistent);
        Assert.NotEqual(session.Id, survivor.Id);
        Assert.True(gateway.ContainsContainer(survivor.ContainerId));
    }

    // A scheduled run whose adopt Docker does not answer, at the list, the inspect or the version call, ends
    // as Docker unavailable rather than a daemon failure or a missing container, and keeps the container.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, "list")]
    [InlineData(PersistenceMode.KeepAcrossRestart, "list")]
    [InlineData(PersistenceMode.FullPersistence, "list")]
    [InlineData(PersistenceMode.KillOnRestart, "inspect")]
    [InlineData(PersistenceMode.KeepAcrossRestart, "inspect")]
    [InlineData(PersistenceMode.FullPersistence, "inspect")]
    [InlineData(PersistenceMode.KillOnRestart, "version")]
    [InlineData(PersistenceMode.KeepAcrossRestart, "version")]
    [InlineData(PersistenceMode.FullPersistence, "version")]
    public async Task ScheduledRun_AdoptDockerCallTimesOut_EndsDockerUnavailable(PersistenceMode mode, string call)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(30));
        var gateway = call == "version"
            ? new RecordingContainerGateway(available: false) { HoldGetVersion = true }
            : new RecordingContainerGateway { HoldListContainers = true };
        var container = gateway.AddContainer(RunningPersistentContainer(sessionId));
        var deps = MakeDeps(database, sessionService, Config(mode, steamEnabled: true));
        using var daemon = new TestSteamDaemon(deps, gateway);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        if (call == "inspect")
        {
            gateway.HoldListContainers = false;
            gateway.HoldInspectContainer = true;
        }

        var (result, stageKeys) = await RunScheduledSteamAsync(daemon, deps, mode, CancellationToken.None);

        Assert.Equal(ScheduledPrefillServiceRunResult.Failed, result);
        Assert.Contains("errors.prefill.dockerUnavailable", stageKeys);
        Assert.DoesNotContain("errors.prefill.requestFailed", stageKeys);
        Assert.DoesNotContain("signalr.scheduledPrefill.needsPersistentContainer", stageKeys);
        Assert.True(gateway.ContainsContainer(container.Id));
        Assert.Equal(0, gateway.CountOf("Remove:"));
    }

    // An own (not persistent, not guest) session whose container start Docker does not answer, or whose
    // daemon never connects, removes its container with its volumes and registers no session.
    [Theory]
    [InlineData(SessionType.Admin, "start")]
    [InlineData(SessionType.User, "start")]
    [InlineData(SessionType.Admin, "connect")]
    public async Task CreateSession_OwnSessionEndsBeforeRegistering_RemovesItsContainer(SessionType type, string failure)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)), gateway,
            () => failure == "connect"
                ? new FakeReconnectDaemonClient { ConnectHandler = _ => Task.FromException(new IOException("The daemon socket did not answer.")) }
                : new FakeReconnectDaemonClient());
        if (failure == "start")
        {
            gateway.FailNextStartContainer(RecordingContainerGateway.NoAnswer());
        }

        Assert.NotNull(await Record.ExceptionAsync(() =>
            daemon.CreateSessionAsync(Guid.NewGuid(), sessionType: type).WaitAsync(TimeSpan.FromSeconds(60))));

        var removal = Assert.Single(gateway.Removals);
        Assert.True(removal.Parameters.RemoveVolumes);
        Assert.Equal(0, gateway.ContainerCount);
        Assert.Empty(SessionsOf(daemon));
    }

    // A person who ends their own session while its create waits on the daemon's first status answer gets
    // one removal of the container: the teardown that took the session removes it, and the create that fails
    // while that teardown is still running leaves the removal to it, so End finishes and the session does
    // not stay in error.
    [Fact]
    public async Task CreateSession_OwnSessionEndedDuringItsCreate_RemovesTheContainerOnce()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var statusEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)), gateway,
            () => new FakeReconnectDaemonClient
            {
                StatusHandler = async token =>
                {
                    statusEntered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    return null;
                }
            });
        using var createStop = new CancellationTokenSource();

        var create = daemon.CreateSessionAsync(Guid.NewGuid(), sessionType: SessionType.User, cancellationToken: createStop.Token);
        await statusEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var session = Assert.Single(SessionsOf(daemon).Values);
        gateway.HoldKillContainer = true;
        var end = daemon.TerminateSessionAsync(session.Id, "User ended session", force: true);
        await gateway.KillContainerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await createStop.CancelAsync();
        Assert.NotNull(await Record.ExceptionAsync(() => create.WaitAsync(TimeSpan.FromSeconds(30))));
        gateway.ReleaseKillContainer.TrySetResult();
        await end.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, gateway.CountOf("Remove:"));
        Assert.Equal(0, gateway.ContainerCount);
        Assert.Equal(DaemonSessionStatus.Terminated, session.Status);
    }

    // A persistent container with no session row never finished a create: a Start whose container start and
    // cleanup removal both timed out leaves one. Adopted later, or found at a restart, it gets a fresh
    // create's login rule (the saved login is erased, or under FullPersistence, after an earlier session
    // that ended without a Stop, signed in from), while a container whose row exists reconnects without a
    // logout and, outside KillOnRestart, signs a signed-out daemon in from the saved login.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task Adopt_ContainerWithoutASessionRow_RunsTheFreshLoginRule(PersistenceMode mode)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var client = new ScriptedLoginDaemonClient();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(mode, steamEnabled: true)), gateway, () => client);
        await SeedPriorPersistentRowAsync(sessionService, PrefillSessionStatus.Orphaned, DateTime.UtcNow.AddDays(30));
        gateway.FailNextStartContainer(RecordingContainerGateway.NoAnswer());
        gateway.HoldRemoveContainer = true;
        Assert.NotNull(await Record.ExceptionAsync(() =>
            daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId())));
        var leftover = gateway.FindByName(SteamPersistentContainerName)!;
        leftover.Running = true;
        gateway.HoldRemoveContainer = false;

        var adopted = await daemon.AdoptRunningPersistentSessionAsync(CancellationToken.None);
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt)
        {
            await attempt.WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.Equal(leftover.Id, adopted?.ContainerId);
        AssertFreshLoginRule(mode, client, adopted!);

        var (_, restartDatabase) = NewDatabase();
        var restartSessions = new PrefillSessionService(restartDatabase, NullLogger<PrefillSessionService>.Instance);
        await SeedPriorPersistentRowAsync(restartSessions, PrefillSessionStatus.Orphaned, DateTime.UtcNow.AddDays(30));
        var restartGateway = new RecordingContainerGateway();
        restartGateway.AddContainer(RunningPersistentContainer(Guid.NewGuid().ToString("N")[..16]));
        var restartClient = new ScriptedLoginDaemonClient();
        using var restarted = new TestSteamDaemon(
            MakeDeps(restartDatabase, restartSessions, Config(mode, steamEnabled: true)), restartGateway, () => restartClient);
        await restarted.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        if (restarted.LastHeadlessSelfAuthAttempt is { } restartAttempt)
        {
            await restartAttempt.WaitAsync(TimeSpan.FromSeconds(15));
        }
        AssertFreshLoginRule(mode, restartClient, restarted.GetActivePersistentSession()!);

        var (_, rowDatabase) = NewDatabase();
        var rowSessions = new PrefillSessionService(rowDatabase, NullLogger<PrefillSessionService>.Instance);
        var rowSessionId = await SeedActivePersistentRowAsync(rowSessions, DateTime.UtcNow.AddDays(30));
        var rowGateway = new RecordingContainerGateway();
        rowGateway.AddContainer(RunningPersistentContainer(rowSessionId));
        var rowClient = new ScriptedLoginDaemonClient();
        using var reconnected = new TestSteamDaemon(
            MakeDeps(rowDatabase, rowSessions, Config(mode, steamEnabled: true)), rowGateway, () => rowClient);
        await reconnected.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        if (reconnected.LastHeadlessSelfAuthAttempt is { } reconnectAttempt)
        {
            await reconnectAttempt.WaitAsync(TimeSpan.FromSeconds(15));
        }
        Assert.Equal(rowSessionId, reconnected.GetActivePersistentSession()?.Id);
        Assert.Equal(0, rowClient.LogoutCount);
        Assert.Equal(mode == PersistenceMode.KillOnRestart ? 0 : 1, rowClient.StartLoginCallCount);
    }

    // Clear stored logins after a startup whose container list timed out adopts the running container and
    // clears its login in place, without first signing a signed-out daemon in from that login. With Docker
    // unreachable it still says Docker is unavailable.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, true)]
    [InlineData(PersistenceMode.KeepAcrossRestart, true)]
    [InlineData(PersistenceMode.FullPersistence, true)]
    [InlineData(PersistenceMode.KillOnRestart, false)]
    [InlineData(PersistenceMode.KeepAcrossRestart, false)]
    [InlineData(PersistenceMode.FullPersistence, false)]
    public async Task ClearLogins_AfterStartupListTimeout_ClearsTheRunningContainersLogin(PersistenceMode mode, bool dockerAnswers)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(30));
        var gateway = dockerAnswers
            ? new RecordingContainerGateway { HoldListContainers = true }
            : new RecordingContainerGateway(available: false) { HoldGetVersion = true };
        var container = gateway.AddContainer(RunningPersistentContainer(sessionId));
        var client = new ScriptedLoginDaemonClient();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(mode, steamEnabled: true)), gateway, () => client);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        gateway.HoldListContainers = false;
        // Docker refuses to remove a volume that a running container still mounts.
        gateway.FailNextRemoveVolume(new DockerApiException(HttpStatusCode.Conflict, "remove steam-auth: volume is in use"));

        var results = await PrefillDaemonServiceBase.ClearAllPersistentLoginsAsync(
            new SingleDaemonServiceProvider(daemon), CancellationToken.None);

        var steam = Assert.Single(results, result => result.Service == PrefillPlatform.Steam);
        if (dockerAnswers)
        {
            Assert.True(steam.Success);
            Assert.True(steam.WasRunning);
            Assert.Equal(0, client.StartLoginCallCount);
        }
        else
        {
            Assert.False(steam.Success);
            Assert.Equal("DockerUnavailable", steam.Detail);
            Assert.True(gateway.ContainsContainer(container.Id));
        }
    }

    // A login-validity change while a running container is not yet adopted, after a startup whose container
    // list timed out (its row reads Orphaned) or a startup without Docker (its row stays Active), moves the
    // stored re-login date, and the later adopt reads it.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, true)]
    [InlineData(PersistenceMode.KeepAcrossRestart, true)]
    [InlineData(PersistenceMode.FullPersistence, true)]
    [InlineData(PersistenceMode.KillOnRestart, false)]
    [InlineData(PersistenceMode.KeepAcrossRestart, false)]
    [InlineData(PersistenceMode.FullPersistence, false)]
    public async Task ValidityChangeBeforeTheAdopt_MovesTheStoredReloginDate(PersistenceMode mode, bool dockerAnswers)
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(30));
        var gateway = dockerAnswers
            ? new RecordingContainerGateway { HoldListContainers = true }
            : new RecordingContainerGateway(available: false) { HoldGetVersion = true };
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(mode, steamEnabled: true)), gateway);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        gateway.HoldListContainers = false;
        gateway.HoldGetVersion = false;
        Assert.Null(daemon.GetActivePersistentSession());
        var row = (await GetRowAsync(options, sessionId))!;
        Assert.Equal(dockerAnswers ? PrefillSessionStatus.Orphaned : PrefillSessionStatus.Active, row.Status);

        StateOf(daemon).SetAdminPersistentLoginValidityDays(7);
        await daemon.UpdatePersistentSessionExpiryAsync();
        var adopted = await daemon.AdoptRunningPersistentSessionAsync(CancellationToken.None);

        var expected = row.LastLoginAtUtc.AddDays(7);
        Assert.Equal(sessionId, adopted?.Id);
        Assert.Equal(expected, adopted!.ExpiresAt);
        Assert.Equal(expected, (await GetRowAsync(options, sessionId))!.ExpiresAtUtc);
    }

    // A shorter validity that puts the stored re-login date in the past keeps that date at the next adopt,
    // both for a running container not adopted yet (a startup whose container list timed out) and for a
    // registered session that a manager restart re-adopts. The expiry pass then asks for a new sign-in.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, false)]
    [InlineData(PersistenceMode.KeepAcrossRestart, false)]
    [InlineData(PersistenceMode.FullPersistence, false)]
    [InlineData(PersistenceMode.KillOnRestart, true)]
    [InlineData(PersistenceMode.KeepAcrossRestart, true)]
    [InlineData(PersistenceMode.FullPersistence, true)]
    public async Task ValidityChangeIntoThePast_KeepsThePastDateAtTheAdopt(PersistenceMode mode, bool registered)
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(20));
        await using (var context = new AppDbContext(options))
        {
            var seeded = await context.PrefillSessions.SingleAsync(row => row.SessionId == sessionId);
            seeded.CreatedAtUtc = DateTime.UtcNow.AddDays(-10);
            seeded.LastLoginAtUtc = seeded.CreatedAtUtc;
            await context.SaveChangesAsync();
        }
        var expected = (await GetRowAsync(options, sessionId))!.LastLoginAtUtc.AddDays(7);
        var gateway = new RecordingContainerGateway { HoldListContainers = !registered };
        var container = gateway.AddContainer(RunningPersistentContainer(sessionId));
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(mode, steamEnabled: true), validityDays: 7), gateway);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        gateway.HoldListContainers = false;
        Assert.Equal(registered, daemon.GetActivePersistentSession() is not null);

        await daemon.UpdatePersistentSessionExpiryAsync();

        DaemonSession? adopted;
        if (registered)
        {
            var restartGateway = new RecordingContainerGateway();
            restartGateway.AddContainer(container);
            var restartClient = new ScriptedLoginDaemonClient();
            using var restarted = new TestSteamDaemon(
                MakeDeps(database, sessionService, Config(mode, steamEnabled: true), validityDays: 7), restartGateway, () => restartClient);
            await restarted.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            if (restarted.LastHeadlessSelfAuthAttempt is { } attempt)
            {
                await attempt.WaitAsync(TimeSpan.FromSeconds(15));
            }
            await restarted.ProcessSessionExpiryAsync(DateTime.UtcNow);
            adopted = restarted.GetActivePersistentSession();
            // A past re-login date means a person signs in again; the restart does not use the saved login.
            Assert.Equal(0, restartClient.StartLoginCallCount);
        }
        else
        {
            adopted = await daemon.AdoptRunningPersistentSessionAsync(CancellationToken.None);
            await daemon.ProcessSessionExpiryAsync(DateTime.UtcNow);
        }

        Assert.Equal(sessionId, adopted?.Id);
        Assert.Equal(expected, adopted!.ExpiresAt);
        Assert.True(adopted.NeedsRelogin);
        Assert.Equal(expected, (await GetRowAsync(options, sessionId))!.ExpiresAtUtc);
    }

    // A session that replaced a 10-day-old login with 20 days left (an image update in any mode, or a
    // FullPersistence startup recreate) carries that sign-in time, so it keeps that date when the same validity
    // is saved again, and a shorter validity moves it back by the difference in days, in memory and in its row.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, false)]
    [InlineData(PersistenceMode.KeepAcrossRestart, false)]
    [InlineData(PersistenceMode.FullPersistence, false)]
    [InlineData(PersistenceMode.FullPersistence, true)]
    public async Task ValidityChangeAfterAReplacement_MovesTheStoredDateByTheChange(PersistenceMode mode, bool recreate)
    {
        DbContextOptions<AppDbContext> options;
        TestSteamDaemon daemon;
        DateTime oldExpiresAt;
        if (recreate)
        {
            oldExpiresAt = DateTime.UtcNow.AddDays(20);
            var client = new ScriptedLoginDaemonClient();
            string priorId;
            (options, daemon, _, priorId) = SetupRecreateScenario(oldExpiresAt, validityDays: 30, clientFactory: () => client);
            await using (var context = new AppDbContext(options))
            {
                var prior = await context.PrefillSessions.SingleAsync(row => row.SessionId == priorId);
                prior.LastLoginAtUtc = oldExpiresAt.AddDays(-30);
                await context.SaveChangesAsync();
            }
            await daemon.StartAsync(CancellationToken.None);
        }
        else
        {
            IDbContextFactory<AppDbContext> database;
            (options, database) = NewDatabase();
            var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
            var status = IdleStatus(Guid.NewGuid().ToString());
            var newClient = new ScriptedLoginDaemonClient();
            var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
            daemon = new TestSteamDaemon(
                MakeDeps(database, sessions, Config(mode, true), validityDays: 30), gateway, () => newClient);
            var createdAt = DateTime.UtcNow.AddDays(-10);
            oldExpiresAt = createdAt.AddDays(30);
            var session = new DaemonSession
            {
                Id = Guid.NewGuid().ToString("N")[..16],
                UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
                Status = DaemonSessionStatus.Active,
                IsPersistent = true,
                AuthState = DaemonAuthState.Authenticated,
                CreatedAt = createdAt,
                ExpiresAt = oldExpiresAt,
                LastLoginAt = createdAt,
                Client = new FakeReconnectDaemonClient { StatusHandler = _ => Task.FromResult<DaemonStatus?>(status) },
                ContainerId = "container-a",
                ContainerName = SteamPersistentContainerName,
                ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest",
                ImageId = "sha256:image-a",
                Capabilities = status,
                LoginSettled = true
            };
            daemon.InjectSession(session);
            gateway.AddContainer(new FakeContainer
            {
                Id = session.ContainerId,
                Name = session.ContainerName,
                Running = true,
                ImageId = session.ImageId
            });
            await sessions.CreateSessionAsync(
                session.Id, session.UserId, session.ContainerId, SteamPersistentContainerName, session.ExpiresAt, session.LastLoginAt, PrefillPlatform.Steam.ToString());
            await daemon.ReconcilePersistentImageAsync(CancellationToken.None);
            Assert.NotEqual(session.Id, daemon.GetActivePersistentSession()?.Id);
        }
        using var owned = daemon;
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt)
        {
            await attempt.WaitAsync(TimeSpan.FromSeconds(15));
        }
        var successor = daemon.GetActivePersistentSession()!;
        Assert.Equal(oldExpiresAt, successor.ExpiresAt);

        StateOf(daemon).SetAdminPersistentLoginValidityDays(30);
        await daemon.UpdatePersistentSessionExpiryAsync();

        Assert.Equal(oldExpiresAt, successor.ExpiresAt);
        Assert.Equal(oldExpiresAt, (await GetRowAsync(options, successor.Id))!.ExpiresAtUtc);

        StateOf(daemon).SetAdminPersistentLoginValidityDays(7);
        await daemon.UpdatePersistentSessionExpiryAsync();

        Assert.Equal(oldExpiresAt.AddDays(-23), successor.ExpiresAt);
        Assert.Equal(oldExpiresAt.AddDays(-23), (await GetRowAsync(options, successor.Id))!.ExpiresAtUtc);
    }

    // A validity change never moves the re-login date of a session a person stopped.
    [Fact]
    public async Task ValidityChange_LeavesAStoppedSessionsRowAlone()
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var expiresAt = DateTime.UtcNow.AddDays(30);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, expiresAt);
        await sessionService.TerminateSessionAsync(sessionId, "Persistent session stopped", "admin");
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway());

        StateOf(daemon).SetAdminPersistentLoginValidityDays(7);
        await daemon.UpdatePersistentSessionExpiryAsync();

        var row = (await GetRowAsync(options, sessionId))!;
        Assert.Equal(PrefillSessionStatus.Terminated, row.Status);
        Assert.Equal(expiresAt, row.ExpiresAtUtc);
    }

    // An errored persistent session stays in memory. A validity save moves its re-login date there and on
    // its row, and a later login-state write copies that memory, so the row keeps the new date.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task ValidityChange_MovesAnErroredSessionsDate(PersistenceMode mode)
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(mode, steamEnabled: true), validityDays: 30),
            new RecordingContainerGateway());
        var session = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        var lastLoginAt = DateTime.UtcNow.AddDays(-1);
        var storedLoginAt = lastLoginAt.AddDays(-9);
        session.Status = DaemonSessionStatus.Error;
        session.ContainerId = "container-errored";
        session.LastLoginAt = lastLoginAt;
        session.ExpiresAt = lastLoginAt.AddDays(30);
        session.NeedsRelogin = true;
        await sessionService.CreateSessionAsync(
            session.Id, session.UserId, session.ContainerId, SteamPersistentContainerName, session.ExpiresAt, storedLoginAt);

        StateOf(daemon).SetAdminPersistentLoginValidityDays(7);
        await daemon.UpdatePersistentSessionExpiryAsync();

        var expected = lastLoginAt.AddDays(7);
        Assert.Equal(expected, session.ExpiresAt);
        Assert.False(session.NeedsRelogin);
        var row = (await GetRowAsync(options, session.Id))!;
        Assert.Equal(expected, row.ExpiresAtUtc);
        Assert.Equal(lastLoginAt, row.LastLoginAtUtc);
        await InvokePrivateAsync(daemon, "SaveLoginStateAsync", session, CancellationToken.None);
        var copied = (await GetRowAsync(options, session.Id))!;
        Assert.Equal(expected, copied.ExpiresAtUtc);
        Assert.Equal(lastLoginAt, copied.LastLoginAtUtc);
    }

    // A validity save sends the session update another open schedules tab reloads its list on, carrying the new date.
    [Fact]
    public async Task ValidityChange_SendsTheSessionUpdateWithTheNewDate()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)(object)notifications;
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true), validityDays: 30, notifications: notifications),
            new RecordingContainerGateway());
        var session = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        var lastLoginAt = DateTime.UtcNow.AddDays(-1);
        session.ContainerId = "container-validity";
        session.LastLoginAt = lastLoginAt;
        session.ExpiresAt = lastLoginAt.AddDays(30);
        await sessionService.CreateSessionAsync(
            session.Id, session.UserId, session.ContainerId, SteamPersistentContainerName, session.ExpiresAt, lastLoginAt);
        var sentDates = new List<DateTime>();
        recorder.SendHandler = eventName =>
        {
            if (eventName == SignalREvents.DaemonSessionUpdated) sentDates.Add(session.ExpiresAt);
            return Task.CompletedTask;
        };

        StateOf(daemon).SetAdminPersistentLoginValidityDays(7);
        await daemon.UpdatePersistentSessionExpiryAsync();

        Assert.Equal([lastLoginAt.AddDays(7)], sentDates);
    }

    // A person's Log in after the re-login date passed starts a new window from that sign-in, so the next expiry
    // pass does not show "Login expired" again, in memory and in the row a restart reads.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task ALogInAfterTheDatePassedClearsLoginExpiredForGoodAsync(PersistenceMode mode)
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(-1));
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        var client = new ScriptedLoginDaemonClient();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(mode, steamEnabled: true), validityDays: 30), gateway, () => client);
        UseOperationTracker(daemon);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt) await attempt.WaitAsync(TimeSpan.FromSeconds(15));
        var session = daemon.GetActivePersistentSession()!;
        await daemon.ProcessSessionExpiryAsync(DateTime.UtcNow);
        Assert.True(session.NeedsRelogin);

        var loginStarted = DateTime.UtcNow;
        await daemon.StartPersistentLoginAsync(session.Id, TimeSpan.FromSeconds(30), () => { }, Guid.NewGuid(), loginId: Guid.NewGuid())
            .WaitAsync(TimeSpan.FromSeconds(30));
        await daemon.GetSessionStatusAsync(session.Id);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        await daemon.ProcessSessionExpiryAsync(DateTime.UtcNow.AddMinutes(1));

        Assert.False(session.NeedsRelogin);
        var row = (await GetRowAsync(options, sessionId))!;
        Assert.Equal(loginStarted.AddDays(30), session.ExpiresAt, TimeSpan.FromMinutes(1));
        Assert.Equal(session.ExpiresAt, row.ExpiresAtUtc);
        Assert.InRange(row.LastLoginAtUtc, loginStarted, DateTime.UtcNow);
        Assert.Equal(row.LastLoginAtUtc.AddDays(30), row.ExpiresAtUtc);
    }

    // A validity save whose first date write fails answers an error with the new days already stored; saving the
    // same days again sets every date to its last sign-in plus the days, for registered sessions and for rows
    // whose running container is not adopted yet.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, true)]
    [InlineData(PersistenceMode.KeepAcrossRestart, true)]
    [InlineData(PersistenceMode.FullPersistence, true)]
    [InlineData(PersistenceMode.KillOnRestart, false)]
    [InlineData(PersistenceMode.KeepAcrossRestart, false)]
    [InlineData(PersistenceMode.FullPersistence, false)]
    public async Task AFailedValiditySaveIsFinishedBySavingAgainAsync(PersistenceMode mode, bool registered)
    {
        var failingWrite = new FailingRowSave(entry => entry.Property(row => row.ExpiresAtUtc).IsModified);
        var (options, database) = NewDatabase(failingWrite);
        var steamSessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var epicSessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var steamId = await SeedActivePersistentRowAsync(steamSessions, DateTime.UtcNow.AddDays(20));
        var epicId = await SeedActivePersistentRowAsync(
            epicSessions, DateTime.UtcNow.AddDays(20), PrefillPlatform.Epic, "epic-daemon-persistent");
        var steamGateway = new RecordingContainerGateway { HoldListContainers = !registered };
        steamGateway.AddContainer(RunningPersistentContainer(steamId));
        var epicGateway = new RecordingContainerGateway { HoldListContainers = !registered };
        epicGateway.AddContainer(RunningPersistentContainer(epicId, "epic-daemon-persistent", PrefillPlatform.Epic));
        var config = Config(mode, steamEnabled: true);
        var state = OrchestrationStateService.New(config, 30);
        var steamDeps = MakeDeps(database, steamSessions, config) with { StateService = state };
        using var steam = new TestSteamDaemon(steamDeps, steamGateway);
        using var epic = new TestEpicDaemon(MakeDeps(database, epicSessions, config) with { StateService = state }, epicGateway);
        await steam.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await epic.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        steamGateway.HoldListContainers = false;
        epicGateway.HoldListContainers = false;
        if (steam.LastHeadlessSelfAuthAttempt is { } steamAttempt) await steamAttempt.WaitAsync(TimeSpan.FromSeconds(15));
        if (epic.LastHeadlessSelfAuthAttempt is { } epicAttempt) await epicAttempt.WaitAsync(TimeSpan.FromSeconds(15));
        var steamSession = steam.GetActivePersistentSession();
        var epicSession = epic.GetActivePersistentSession();
        Assert.Equal(registered, steamSession is not null);
        Assert.Equal(registered, epicSession is not null);
        await using var daemons = new ServiceCollection()
            .AddSingleton<SteamDaemonService>(steam)
            .AddSingleton<EpicPrefillDaemonService>(epic)
            .BuildServiceProvider();
        var controller = new PersistentPrefillController(
            daemons, state, steamDeps.CacheService, NullLogger<PersistentPrefillController>.Instance);

        failingWrite.Armed = true;
        Assert.NotNull(await Record.ExceptionAsync(() => controller.SetValidityAsync(new PersistentLoginValidityDto { Days = 7 })));
        failingWrite.Armed = false;
        Assert.Equal(7, state.GetAdminPersistentLoginValidityDays());
        var second = await controller.SetValidityAsync(new PersistentLoginValidityDto { Days = 7 });

        Assert.IsType<OkObjectResult>(second.Result);
        foreach (var (sessionId, session) in new[] { (steamId, steamSession), (epicId, epicSession) })
        {
            var row = (await GetRowAsync(options, sessionId))!;
            Assert.Equal(row.CreatedAtUtc.AddDays(7), row.ExpiresAtUtc, TimeSpan.FromMinutes(1));
            Assert.Equal(row.LastLoginAtUtc.AddDays(7), row.ExpiresAtUtc);
            if (session is not null)
            {
                Assert.Equal(row.ExpiresAtUtc, session.ExpiresAt);
            }
        }
    }

    // A person's Log in, then an image update: the new container carries the sign-in time and the date, and
    // saving the same days again leaves the date where it is.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task AnImageUpdateAfterALogInKeepsTheSignInTimeAsync(PersistenceMode mode)
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(-1));
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        var first = new ScriptedLoginDaemonClient();
        var clients = new Queue<IDaemonClient>([first, new ScriptedLoginDaemonClient(alreadyLoggedIn: true)]);
        var deps = MakeDeps(database, sessionService, Config(mode, steamEnabled: true), validityDays: 30);
        using var daemon = new TestSteamDaemon(deps, gateway, () => clients.Dequeue());
        UseOperationTracker(daemon);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        if (daemon.LastHeadlessSelfAuthAttempt is { } startupAttempt) await startupAttempt.WaitAsync(TimeSpan.FromSeconds(15));
        var session = daemon.GetActivePersistentSession()!;
        var loginStarted = DateTime.UtcNow;
        await daemon.StartPersistentLoginAsync(session.Id, TimeSpan.FromSeconds(30), () => { }, Guid.NewGuid(), loginId: Guid.NewGuid())
            .WaitAsync(TimeSpan.FromSeconds(30));
        await daemon.GetSessionStatusAsync(session.Id);
        Assert.Equal(loginStarted.AddDays(30), session.ExpiresAt, TimeSpan.FromMinutes(1));

        // An image update replaces only an idle daemon of the same generation.
        var idle = IdleStatus(Guid.NewGuid().ToString());
        first.StatusOverride = idle;
        session.Capabilities = idle;
        gateway.ResolvedImageId = "sha256:image-b";
        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        if (daemon.LastHeadlessSelfAuthAttempt is { } updateAttempt) await updateAttempt.WaitAsync(TimeSpan.FromSeconds(15));
        var successor = daemon.GetActivePersistentSession()!;
        Assert.NotEqual(session.Id, successor.Id);
        Assert.Equal(session.ExpiresAt, successor.ExpiresAt);
        Assert.Equal(session.LastLoginAt, successor.LastLoginAt);
        Assert.Equal(session.LastLoginAt, (await GetRowAsync(options, successor.Id))!.LastLoginAtUtc);

        await new PersistentPrefillController(
                new SingleDaemonServiceProvider(daemon), deps.StateService, deps.CacheService, NullLogger<PersistentPrefillController>.Instance)
            .SetValidityAsync(new PersistentLoginValidityDto { Days = 30 });

        Assert.Equal(session.ExpiresAt, successor.ExpiresAt);
        Assert.Equal(session.ExpiresAt, (await GetRowAsync(options, successor.Id))!.ExpiresAtUtc);
    }

    // Clear stored logins while an image update cannot create or start its new container still removes the
    // stored login: the failed update is logged and the volume clear answers for that service.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, "create")]
    [InlineData(PersistenceMode.KeepAcrossRestart, "create")]
    [InlineData(PersistenceMode.FullPersistence, "create")]
    [InlineData(PersistenceMode.KillOnRestart, "start")]
    [InlineData(PersistenceMode.KeepAcrossRestart, "start")]
    [InlineData(PersistenceMode.FullPersistence, "start")]
    public async Task ClearLogins_WhileTheImageUpdateCannotStartItsContainer_RemovesTheStoredLogin(PersistenceMode mode, string failure)
    {
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        var scenario = NewImageScenario(gateway: gateway, mode: mode);
        using var daemon = scenario.Daemon;
        void FailTheNewContainer()
        {
            var refused = new DockerApiException(HttpStatusCode.InternalServerError, "simulated daemon image failure");
            if (failure == "create") gateway.FailNextCreateContainer(refused);
            else gateway.FailNextStartContainer(refused);
        }
        FailTheNewContainer();
        await Assert.ThrowsAsync<DockerApiException>(() => daemon.ReconcilePersistentImageAsync(CancellationToken.None));
        Assert.True(daemon.HasPersistentImageWork);
        FailTheNewContainer();

        var results = await PrefillDaemonServiceBase.ClearAllPersistentLoginsAsync(
            new SingleDaemonServiceProvider(daemon), CancellationToken.None);

        var steam = Assert.Single(results, result => result.Service == PrefillPlatform.Steam);
        Assert.True(steam.Success);
        Assert.Equal("Removed", steam.Detail);
        Assert.Equal(1, gateway.CountOf("RemoveVolume:"));
    }

    // After a startup whose container list timed out, the first tick adopts the running container before it
    // judges the platform, so a daemon that runs schedules side by side runs a second schedule instead of
    // skipping it, and the tick does not wait for the first run.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task ScheduledTick_AfterStartupListTimeout_AdoptsBeforeJudgingThePlatform(PersistenceMode mode)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessions, DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway { HoldListContainers = true };
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        var status = IdleStatus(Guid.NewGuid().ToString());
        var client = new FakeReconnectDaemonClient
        {
            StatusHandler = _ => Task.FromResult<DaemonStatus?>(status),
            SelectionHandler = (_, _) => Task.CompletedTask
        };
        var config = Config(mode, true);
        var first = new ScheduledPrefillSchedule
        {
            Id = config.Steam.Schedules[0].Id,
            Name = "Daily",
            Enabled = true,
            IntervalHours = -1,
            NotificationMode = NotificationMode.Silent,
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Auto }
        };
        var second = new ScheduledPrefillSchedule
        {
            Id = Guid.NewGuid(),
            Name = "Weekly",
            Enabled = true,
            IntervalHours = -1,
            NotificationMode = NotificationMode.Silent,
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Auto }
        };
        config.Steam.Schedules.Clear();
        config.Steam.Schedules.Add(first);
        config.Steam.Schedules.Add(second);
        var deps = MakeDeps(database, sessions, config);
        using var daemon = new TestSteamDaemon(deps, gateway, () => client);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(daemon.GetActivePersistentSession());
        gateway.HoldListContainers = false;
        var notifications = DispatchProxy.Create<ISignalRNotificationService, StageKeyRecorder>();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);
        using var services = new ServiceCollection()
            .AddSingleton<SteamDaemonService>(daemon)
            .AddSingleton<IUnifiedOperationTracker>(tracker)
            .AddSingleton(notifications)
            .BuildServiceProvider();
        using var scheduler = new ScheduledPrefillService(NullLogger<ScheduledPrefillService>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(), deps.StateService);
        var execute = typeof(ScheduledPrefillService).GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var tickStop = new CancellationTokenSource();

        var tick = (Task)execute.Invoke(scheduler, [tickStop.Token])!;
        var tickReturned = await Task.WhenAny(tick, Task.Delay(TimeSpan.FromSeconds(10))) == tick;
        var stageKeys = ((StageKeyRecorder)(object)notifications).StageKeys;
        tickStop.Cancel();
        await Task.WhenAny(scheduler.StopAsync(CancellationToken.None), Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(tickReturned);
        Assert.Equal(sessionId, daemon.GetActivePersistentSession()?.Id);
        Assert.DoesNotContain("signalr.scheduledPrefill.skippedOverlap", stageKeys);
    }

    // An errored session's replacement whose kill and removal Docker did not answer keeps its teardown
    // stored, so a scheduled adopt does not register its container again. The person's Stop then finishes
    // that teardown as an explicit Stop: nothing stays registered, the row reads Terminated, and the next
    // Start erases the saved login in every mode.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task ScheduledAdopt_WhileReplacementTeardownIsStored_DoesNotRegisterTheContainer(PersistenceMode mode)
    {
        var (options, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var clients = new List<ScriptedLoginDaemonClient>();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(mode, steamEnabled: true)), gateway, () =>
            {
                var created = new ScriptedLoginDaemonClient();
                clients.Add(created);
                return created;
            });
        var errored = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        var container = gateway.AddContainer(RunningPersistentContainer(errored.Id));
        errored.ContainerId = container.Id;
        errored.ContainerName = SteamPersistentContainerName;
        errored.Status = DaemonSessionStatus.Error;
        await sessionService.CreateSessionAsync(
            errored.Id, errored.UserId, errored.ContainerId, SteamPersistentContainerName, errored.ExpiresAt, DateTime.UtcNow, PrefillPlatform.Steam.ToString());
        gateway.FailNextKillContainer(RecordingContainerGateway.NoAnswer());
        gateway.HoldRemoveContainer = true;
        Assert.NotNull(await Record.ExceptionAsync(() =>
            daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId())));
        gateway.HoldRemoveContainer = false;

        var adopted = await daemon.AdoptRunningPersistentSessionAsync(CancellationToken.None);

        Assert.Null(adopted);
        Assert.DoesNotContain(SessionsOf(daemon).Values, registered => registered.IsPersistent);

        await daemon.StopPersistentSessionAsync(errored.Id, "admin");

        Assert.DoesNotContain(SessionsOf(daemon).Values, registered => registered.IsPersistent);
        Assert.False(gateway.ContainsContainer(container.Id));
        var row = (await GetRowAsync(options, errored.Id))!;
        Assert.Equal(PrefillSessionStatus.Terminated, row.Status);
        Assert.Equal("admin", row.TerminatedBy);

        var next = await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());
        Assert.Null(daemon.LastHeadlessSelfAuthAttempt);
        var nextClient = clients.Last();
        Assert.Equal(1, nextClient.LogoutCount);
        Assert.Equal(0, nextClient.StartLoginCallCount);
        Assert.NotEqual(errored.Id, next.Id);
    }

    // A person's Stop over a stored replacement teardown whose row write the database refuses is finished by
    // the next Start as that Stop: the row the replacement marked Orphaned reads Terminated, and the new
    // daemon's saved login is erased in every mode.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task StartAfterStopWhoseRowWriteFailed_EndsTheOrphanedRowAndErasesTheLogin(PersistenceMode mode)
    {
        var failing = new FailingRowSave(entry => entry.Entity.Status == PrefillSessionStatus.Terminated);
        var (options, database) = NewDatabase(failing);
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var clients = new List<ScriptedLoginDaemonClient>();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(mode, steamEnabled: true)), gateway, () =>
            {
                var created = new ScriptedLoginDaemonClient();
                clients.Add(created);
                return created;
            });
        var errored = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        var container = gateway.AddContainer(RunningPersistentContainer(errored.Id));
        errored.ContainerId = container.Id;
        errored.ContainerName = SteamPersistentContainerName;
        errored.Status = DaemonSessionStatus.Error;
        await sessionService.CreateSessionAsync(
            errored.Id, errored.UserId, errored.ContainerId, SteamPersistentContainerName, errored.ExpiresAt, DateTime.UtcNow, PrefillPlatform.Steam.ToString());
        gateway.FailNextKillContainer(RecordingContainerGateway.NoAnswer());
        gateway.HoldRemoveContainer = true;
        Assert.NotNull(await Record.ExceptionAsync(() =>
            daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId())));
        gateway.HoldRemoveContainer = false;
        Assert.Equal(PrefillSessionStatus.Orphaned, (await GetRowAsync(options, errored.Id))!.Status);
        failing.Armed = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => daemon.StopPersistentSessionAsync(errored.Id, "admin"));
        failing.Armed = false;

        var next = await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt)
        {
            await attempt.WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.NotEqual(errored.Id, next.Id);
        Assert.False(gateway.ContainsContainer(container.Id));
        Assert.Equal(PrefillSessionStatus.Terminated, (await GetRowAsync(options, errored.Id))!.Status);
        var nextClient = clients.Last();
        Assert.Equal(1, nextClient.LogoutCount);
        Assert.Equal(0, nextClient.StartLoginCallCount);
    }

    // A scheduled adopt inside Clear stored logins' hard removal does not register the session that Clear
    // is tearing down, so after Clear no persistent session is left registered.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task ScheduledAdopt_DuringClearHardRemoval_DoesNotRegisterTheSession(PersistenceMode mode)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(mode, steamEnabled: true)), gateway, () => new FakeReconnectDaemonClient
            {
                StatusHandler = _ => Task.FromResult<DaemonStatus?>(new DaemonStatus { Status = "logged-in" })
            });
        var started = await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());
        gateway.HoldKillContainer = true;

        var clear = PrefillDaemonServiceBase.ClearAllPersistentLoginsAsync(new SingleDaemonServiceProvider(daemon), CancellationToken.None);
        await gateway.KillContainerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var adopt = daemon.AdoptRunningPersistentSessionAsync(CancellationToken.None);
        gateway.ReleaseKillContainer.TrySetResult();
        var results = await clear.WaitAsync(TimeSpan.FromSeconds(30));
        var adopted = await adopt.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(adopted);
        Assert.Equal("hard-removed", Assert.Single(results, result => result.Service == PrefillPlatform.Steam).Detail);
        Assert.False(gateway.ContainsContainer(started.ContainerId));
        Assert.DoesNotContain(SessionsOf(daemon).Values, registered => registered.IsPersistent);
    }

    // A person's Start inside Clear stored logins' hard removal waits for that removal: Clear removes the old
    // container once and reports it removed, the Start creates a new container afterward, and the new
    // daemon's saved login is erased in every mode.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task StartDuringClearHardRemoval_WaitsForTheRemovalAndErasesTheLogin(PersistenceMode mode)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var clients = new List<ScriptedLoginDaemonClient>();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(mode, steamEnabled: true)), gateway, () =>
            {
                // The first daemon keeps answering logged-in after its logout, so Clear escalates to the hard removal.
                var created = clients.Count == 0
                    ? new ScriptedLoginDaemonClient { StatusOverride = new DaemonStatus { Status = "logged-in" } }
                    : new ScriptedLoginDaemonClient();
                clients.Add(created);
                return created;
            });
        var started = await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());
        if (daemon.LastHeadlessSelfAuthAttempt is { } firstAttempt)
        {
            await firstAttempt.WaitAsync(TimeSpan.FromSeconds(15));
        }
        gateway.HoldKillContainer = true;

        var clear = PrefillDaemonServiceBase.ClearAllPersistentLoginsAsync(new SingleDaemonServiceProvider(daemon), CancellationToken.None);
        await gateway.KillContainerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var start = daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());
        await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(2)));
        gateway.ReleaseKillContainer.TrySetResult();
        var results = await clear.WaitAsync(TimeSpan.FromSeconds(30));
        var next = await start.WaitAsync(TimeSpan.FromSeconds(30));
        if (daemon.LastHeadlessSelfAuthAttempt is { } nextAttempt)
        {
            await nextAttempt.WaitAsync(TimeSpan.FromSeconds(15));
        }

        var steam = Assert.Single(results, result => result.Service == PrefillPlatform.Steam);
        Assert.Equal("hard-removed", steam.Detail);
        Assert.True(steam.Success);
        Assert.Equal(1, gateway.CountOf($"Remove:{started.ContainerId}"));
        Assert.True(gateway.Calls.IndexOf($"Kill:{started.ContainerId}") < gateway.Calls.FindLastIndex(call => call.StartsWith("Create:", StringComparison.Ordinal)));
        Assert.NotEqual(started.Id, next.Id);
        var nextClient = clients.Last();
        Assert.Equal(1, nextClient.LogoutCount);
        Assert.Equal(0, nextClient.StartLoginCallCount);
        Assert.Null(daemon.GetFailedStopSession());
    }

    // A person who stops a scheduled run while its adopt waits on Docker sees the run stopped, not a missing
    // container, and the container is kept.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task ScheduledRun_StoppedDuringItsAdopt_EndsStoppedAndKeepsTheContainer(PersistenceMode mode)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway { HoldListContainers = true };
        var container = gateway.AddContainer(RunningPersistentContainer(sessionId));
        var deps = MakeDeps(database, sessionService, Config(mode, steamEnabled: true));
        using var daemon = new TestSteamDaemon(deps, gateway);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        gateway.HoldListContainers = false;
        gateway.HoldInspectUntilCanceled = container.Id;
        using var stop = new CancellationTokenSource();

        var run = RunScheduledSteamAsync(daemon, deps, mode, stop.Token);
        await gateway.InspectContainerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        var (result, stageKeys) = await run;

        Assert.Equal(ScheduledPrefillServiceRunResult.Cancelled, result);
        Assert.Contains("signalr.scheduledPrefill.stopped", stageKeys);
        Assert.DoesNotContain("signalr.scheduledPrefill.needsPersistentContainer", stageKeys);
        Assert.True(gateway.ContainsContainer(container.Id));
        Assert.Equal(0, gateway.CountOf("Remove:"));
    }

    // A manager self-inspect that its caller cancels fails that create and caches no host data path, so the
    // next create binds the real host mount. Linux only: on Windows the data path is the local data directory.
    [Fact]
    public async Task StartPersistentSession_OwnContainerInspectCanceled_NextCreateBindsTheMountedHostFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)), gateway,
            () => new ScriptedLoginDaemonClient());
        typeof(PrefillDaemonServiceBase).GetField("_isRunningInContainer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(daemon, true);
        var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
        Environment.SetEnvironmentVariable("HOSTNAME", "manager-container-0001");
        try
        {
            var ownId = (string)typeof(PrefillDaemonServiceBase)
                .GetMethod("GetOwnContainerId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(daemon, null)!;
            gateway.AddContainer(new FakeContainer
            {
                Id = ownId,
                Name = "lancache-manager",
                Running = true,
                Mounts = [new Docker.DotNet.Models.MountPoint { Destination = "/data", Source = "/mnt/host/data" }]
            });
            gateway.HoldInspectUntilCanceled = ownId;
            using var cancel = new CancellationTokenSource();
            var first = daemon.StartPersistentSessionAsync(
                PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId(), cancellationToken: cancel.Token);
            await gateway.InspectContainerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            gateway.HoldInspectUntilCanceled = null;

            await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());

            var binds = Assert.Single(gateway.CreatedParameters).HostConfig.Binds;
            Assert.Contains(binds, bind => bind.StartsWith("/mnt/host/data/", StringComparison.Ordinal));
            Assert.DoesNotContain(binds, bind => bind.StartsWith("/data/", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOSTNAME", hostname);
        }
    }

    // A user's own session create whose network check Docker does not answer fails at that first limit and
    // creates no container.
    [Fact]
    public async Task CreateSession_OwnSessionNetworkListTimesOut_FailsAndCreatesNothing()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway { HoldListContainers = true };
        var deps = MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)) with
        {
            NetworkOptions = new StaticOptionsMonitor(new PrefillNetworkOptions { NetworkMode = "auto" })
        };
        using var daemon = new TestSteamDaemon(deps, gateway);

        await Assert.ThrowsAsync<DockerTimeoutException>(() => daemon.CreateSessionAsync(Guid.NewGuid(), sessionType: SessionType.User));

        Assert.Equal(0, gateway.CountOf("Create:"));
    }

    // A startup the host cancels does not log that Docker did not answer.
    [Fact]
    public async Task StartAsync_HostCanceled_DoesNotSayDockerDidNotAnswer()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var logger = new CapturingLogger<SteamDaemonService>();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway(), logger: logger);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await daemon.StartAsync(cancel.Token);

        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("Docker did not answer", StringComparison.Ordinal));
    }

    // A caller that read Docker as unconnected just before another caller's connect finished starts no second
    // connect.
    [Fact]
    public async Task ConnectDocker_ConnectFinishedAfterTheRead_StartsNoSecondConnect()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway(available: false);
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)), gateway);
        await InvokePrivateAsync(daemon, "ConnectDockerAsync", CancellationToken.None);
        gateway.ReportUnavailableOnce();

        await InvokePrivateAsync(daemon, "ConnectDockerAsync", CancellationToken.None);

        Assert.True(gateway.IsAvailable);
        Assert.Equal(1, gateway.CountOf("Version"));
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

    private static (DbContextOptions<AppDbContext> Options, IDbContextFactory<AppDbContext> Factory) NewDatabase(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"prefill_orchestration_{Guid.NewGuid():N}")
            .AddInterceptors(interceptors)
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
            sessionId, ScheduledPrefillConstants.DeriveSystemUserId(), $"container-{sessionId}", containerName, expiresAt, DateTime.UtcNow, platform.ToString());
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
        SetupRecreateScenario(DateTime priorExpiry, int validityDays = 90, Func<IDaemonClient>? clientFactory = null, ISignalRNotificationService? notifications = null, ILogger<SteamDaemonService>? logger = null, TimeProvider? cacheStatusClock = null, IInterceptor[]? interceptors = null)
    {
        var (options, dbFactory) = NewDatabase(interceptors ?? []);
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var priorSessionId = SeedActivePersistentRowAsync(sessionService, priorExpiry).GetAwaiter().GetResult();

        var gateway = new RecordingContainerGateway();
        var config = Config(PersistenceMode.FullPersistence, steamEnabled: true);
        var daemon = new TestSteamDaemon(MakeDeps(dbFactory, sessionService, config, validityDays, notifications, cacheStatusClock: cacheStatusClock), gateway, clientFactory, logger);
        return (options, daemon, gateway, priorSessionId);
    }

    private static PrefillDaemonServiceBase BuildDaemon(
        PrefillPlatform platform, DaemonDeps deps, IPrefillContainerGateway gateway, IDaemonClient? client = null)
        => platform switch
        {
            PrefillPlatform.Steam => new TestSteamDaemon(deps, gateway, () => client ?? new FakeReconnectDaemonClient()),
            PrefillPlatform.Epic => new TestEpicDaemon(deps, gateway, client),
            PrefillPlatform.Xbox => new TestXboxDaemon(deps, gateway, client),
            PrefillPlatform.BattleNet => new TestBattleNetDaemon(deps, gateway),
            PrefillPlatform.Riot => new TestRiotDaemon(deps, gateway),
            _ => throw new ArgumentOutOfRangeException(nameof(platform))
        };

    private DaemonDeps MakeDeps(
        IDbContextFactory<AppDbContext> dbFactory, PrefillSessionService sessionService, ScheduledPrefillConfigDto config, int validityDays = 90,
        ISignalRNotificationService? notifications = null,
        string? image = null,
        TimeProvider? cacheStatusClock = null)
    {
        notifications ??= FakeInterface<ISignalRNotificationService>();
        var stateService = OrchestrationStateService.New(config, validityDays);
        var pathResolver = PathResolverProxy.New(NewTempRoot());
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        // A non-"auto" NetworkMode keeps the create path from probing Docker for lancache-dns.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Prefill:NetworkMode"] = "bridge",
                ["Prefill:SteamDockerImage"] = image
            })
            .Build();
        var networkOptions = new StaticOptionsMonitor(new PrefillNetworkOptions { NetworkMode = "bridge" });
        return new DaemonDeps(notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions, new TestLancacheServerLocator(), cacheStatusClock);
    }

    private static ScheduledPrefillConfigDto Config(PersistenceMode global, bool steamEnabled)
    {
        var b = ScheduledPrefillConfigFactory.CreateDefault();
        var defaultSchedule = b.Steam.Schedules.Single();
        var steam = new ScheduledPrefillServiceConfigDto
        {
            ServiceId = b.Steam.ServiceId,
            Schedules =
            [
                new ScheduledPrefillSchedule
                {
                    Id = defaultSchedule.Id,
                    Name = defaultSchedule.Name,
                    Enabled = steamEnabled,
                    IntervalHours = defaultSchedule.IntervalHours,
                    CustomSchedule = defaultSchedule.CustomSchedule,
                    Preset = defaultSchedule.Preset,
                    TopCount = defaultSchedule.TopCount,
                    SelectedAppIds = defaultSchedule.SelectedAppIds,
                    OperatingSystems = defaultSchedule.OperatingSystems,
                    Force = defaultSchedule.Force,
                    MaxConcurrency = defaultSchedule.MaxConcurrency,
                    NotificationMode = defaultSchedule.NotificationMode,
                    NotificationDisplayMode = defaultSchedule.NotificationDisplayMode
                }
            ],
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

    // Runs the persistent Start route through the exception middleware, as a browser request reaches it. A
    // Start that succeeds writes no body here, because the test never executes the returned result.
    private static async Task<(int Status, string? StageKey)> StartRouteAsync(TestSteamDaemon daemon, DaemonDeps deps)
    {
        var controller = new PersistentPrefillController(
            new SingleDaemonServiceProvider(daemon), deps.StateService, deps.CacheService,
            NullLogger<PersistentPrefillController>.Instance);
        var middleware = new GlobalExceptionMiddleware(
            async _ => await controller.StartAsync(
                new StartPersistentSessionRequest { Service = PrefillPlatform.Steam }, CancellationToken.None),
            NullLogger<GlobalExceptionMiddleware>.Instance,
            DispatchProxy.Create<IHostEnvironment, NullReturningProxy>());
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context).WaitAsync(TimeSpan.FromSeconds(30));

        if (body.Length == 0)
        {
            return (context.Response.StatusCode, null);
        }
        using var answer = JsonDocument.Parse(body.ToArray());
        return (context.Response.StatusCode, answer.RootElement.GetProperty("stageKey").GetString());
    }

    // Runs one scheduled Steam run as a tick does, and returns how it ended with the stage key of every event
    // it pushed.
    private static async Task<(ScheduledPrefillServiceRunResult Result, List<string?> StageKeys)> RunScheduledSteamAsync(
        TestSteamDaemon daemon, DaemonDeps deps, PersistenceMode mode, CancellationToken stop)
    {
        var serviceConfig = new ScheduledPrefillServiceConfigDto
        {
            ServiceId = PrefillPlatform.Steam,
            ScheduleId = ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam),
            ScheduleName = "Default",
            Enabled = true,
            NotificationMode = NotificationMode.Silent,
            IntervalHours = 24,
            Preset = ScheduledPrefillPreset.All,
            SelectedAppIds = new List<string>(),
            OperatingSystems = new List<ScheduledPrefillOperatingSystem> { ScheduledPrefillOperatingSystem.Windows },
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Auto }
        };
        var serviceRun = new ScheduledPrefillServiceRun(
            serviceConfig, Guid.NewGuid(), "op-1", "run-1",
            new ScheduledPrefillServiceRunState(
                serviceConfig.ServiceId, serviceConfig.ScheduleId, serviceConfig.ScheduleName,
                new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled)),
            stop);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, StageKeyRecorder>();
        using var schedulerServices = new ServiceCollection().BuildServiceProvider();
        using var scheduler = new ScheduledPrefillService(NullLogger<ScheduledPrefillService>.Instance,
            schedulerServices.GetRequiredService<IServiceScopeFactory>(), deps.StateService);
        var runAndStamp = typeof(ScheduledPrefillService).GetMethod("RunAndStampServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var run = (Task<ScheduledPrefillServiceRunResult>)runAndStamp.Invoke(scheduler,
            [serviceRun, FakeInterface<IUnifiedOperationTracker>(), new SingleDaemonServiceProvider(daemon), notifications,
                Config(mode, steamEnabled: true), CancellationToken.None, true, null])!;
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        return (result, ((StageKeyRecorder)(object)notifications).StageKeys);
    }

    // A fresh persistent create erases the saved login, except under FullPersistence after an earlier session
    // that ended without a Stop, which signs in from it.
    private static void AssertFreshLoginRule(PersistenceMode mode, ScriptedLoginDaemonClient client, DaemonSession session)
    {
        if (mode == PersistenceMode.FullPersistence)
        {
            Assert.Equal(0, client.LogoutCount);
            Assert.Equal(1, client.StartLoginCallCount);
            Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        }
        else
        {
            Assert.Equal(1, client.LogoutCount);
            Assert.Equal(0, client.StartLoginCallCount);
        }
    }

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

    // A database that refuses the first session's terminated row at shutdown must not keep the other guest
    // container running until the next start.
    [Fact]
    public async Task StopAsync_FailedWriteForOneGuestSessionStillStopsTheOtherGuestContainer()
    {
        var failing = new FailingRowSave(entry => entry.Entity.Status == PrefillSessionStatus.Terminated);
        var (_, contexts) = NewDatabase(failing);
        var sessionService = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var daemon = new TestSteamDaemon(MakeDeps(contexts, sessionService, Config(PersistenceMode.KillOnRestart, steamEnabled: true)), gateway);
        string[] containers = ["guest-container-a", "guest-container-b"];
        foreach (var containerId in containers)
        {
            var sessionId = Guid.NewGuid().ToString("N")[..16];
            await sessionService.CreateSessionAsync(sessionId, Guid.NewGuid(), containerId, containerId, DateTime.UtcNow.AddHours(1), DateTime.UtcNow);
            daemon.InjectSession(new DaemonSession
            {
                Id = sessionId,
                UserId = Guid.NewGuid(),
                Status = DaemonSessionStatus.Active,
                IsPersistent = false,
                ContainerId = containerId,
                ContainerName = containerId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
                Client = new ScriptedLoginDaemonClient()
            });
        }
        failing.FailuresLeft = 1;

        var stopError = await Record.ExceptionAsync(() => daemon.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.NotNull(stopError);

        foreach (var containerId in containers)
        {
            Assert.Contains($"Stop:{containerId}", gateway.Calls);
        }
        daemon.Dispose();
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
        ILancacheServerLocator Locator,
        TimeProvider? CacheStatusClock = null);

    private sealed class TestSteamDaemon : SteamDaemonService
    {
        private readonly Func<IDaemonClient>? _clientFactory;

        // The optional clientFactory lets the headless self-auth scenarios substitute a scripted
        // login client for the session the recreate path builds; everything else keeps the default
        // reconnect fake. The optional logger lets a test capture the warning/error contract.
        public TestSteamDaemon(DaemonDeps d, IPrefillContainerGateway g, Func<IDaemonClient>? clientFactory = null, ILogger<SteamDaemonService>? logger = null, IActivityRegistry? activity = null)
            : base(logger ?? NullLogger<SteamDaemonService>.Instance, d.Notifications, d.Configuration, d.PathResolver, d.StateService, d.SessionService, d.CacheService, d.NetworkOptions, d.Locator, new SingleContainerGatewayFactory(g), activity, cacheStatusClock: d.CacheStatusClock)
            => _clientFactory = clientFactory;

        protected override IDaemonClient CreateDaemonClient(bool useTcpMode, int? tcpHostPort, string socketPath, string socketSecret)
            => _clientFactory?.Invoke() ?? new FakeReconnectDaemonClient();

        // Direct-injection seam for the concurrency/cancel scenarios that need a session without a
        // full create round-trip (mirrors the InjectSession seam in PersistentEraseOnStopTests.cs).
        public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;

        // Set by a test that drives a person's "Use my saved login": the saved login is available, and its
        // import waits on this until the test releases it.
        public TaskCompletionSource? HeldSavedLogin { get; set; }
        public TaskCompletionSource SavedLoginEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override IntegrationLoginAvailability GetIntegrationLoginAvailability(Guid? accountId, IntegrationCaller? caller = null)
            => HeldSavedLogin is null ? base.GetIntegrationLoginAvailability(accountId, caller) : new(true, "saved-account", null);

        protected override async Task<bool> ReuseIntegrationLoginAsync(
            DaemonSession session, Guid? accountId, Action onCommandDispatched, CancellationToken cancellationToken, IntegrationLease? lease = null)
        {
            if (HeldSavedLogin is null)
            {
                return await base.ReuseIntegrationLoginAsync(session, accountId, onCommandDispatched, cancellationToken, lease);
            }
            onCommandDispatched();
            SavedLoginEntered.TrySetResult();
            await HeldSavedLogin.Task.WaitAsync(cancellationToken);
            return false;
        }
    }

    // The Epic/Xbox mapping service is a game-catalog dependency the startup re-adopt path never touches
    // (the fake reconnect client reports null live status, so no auth hooks that would use it ever fire;
    // a scripted client's sign-in reaches Epic's owned-games hook, which logs its failure and goes on),
    // so it is passed null here rather than standing up its heavy dependency graph.
    private sealed class TestEpicDaemon : EpicPrefillDaemonService
    {
        private readonly IDaemonClient? _client;

        public TestEpicDaemon(DaemonDeps d, IPrefillContainerGateway g, IDaemonClient? client = null)
            : base(NullLogger<EpicPrefillDaemonService>.Instance, d.Notifications, d.Configuration, d.PathResolver, d.StateService, d.SessionService, d.CacheService, null!, d.NetworkOptions, d.Locator, new SingleContainerGatewayFactory(g))
        {
            _client = client;
        }
        protected override IDaemonClient CreateDaemonClient(bool useTcpMode, int? tcpHostPort, string socketPath, string socketSecret)
            => _client ?? new FakeReconnectDaemonClient();
    }

    private sealed class TestXboxDaemon : XboxPrefillDaemonService
    {
        private readonly IDaemonClient? _client;

        public TestXboxDaemon(DaemonDeps d, IPrefillContainerGateway g, IDaemonClient? client = null)
            : base(NullLogger<XboxPrefillDaemonService>.Instance, d.Notifications, d.Configuration, d.PathResolver, d.StateService, d.SessionService, d.CacheService, null!, d.NetworkOptions, d.Locator, new SingleContainerGatewayFactory(g))
        {
            _client = client;
        }
        protected override IDaemonClient CreateDaemonClient(bool useTcpMode, int? tcpHostPort, string socketPath, string socketSecret)
            => _client ?? new FakeReconnectDaemonClient();
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

    private sealed class SingleDaemonServiceProvider : IServiceProvider
    {
        private readonly SteamDaemonService _daemon;

        public SingleDaemonServiceProvider(SteamDaemonService daemon)
        {
            _daemon = daemon;
        }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(SteamDaemonService) ? _daemon : null;
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

            if (targetMethod?.Name == nameof(IStateService.SetAdminPersistentLoginValidityDays))
            {
                _validityDays = (int)args![0]!;
                return null;
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

    // Returns the FullPersistence config for the first fullReads calls, then the later config.
    // The startup recreate uses the first call to decide to recreate. The volume decision is the next call.
    private class ModeReadState : DispatchProxy
    {
        private ScheduledPrefillConfigDto? _full;
        private ScheduledPrefillConfigDto? _later;
        private int _fullReads;
        private int _validityDays;

        public int Reads { get; private set; }

        public IStateService Service => (IStateService)(object)this;

        public static ModeReadState New(
            ScheduledPrefillConfigDto full,
            ScheduledPrefillConfigDto later,
            int fullReads,
            int validityDays)
        {
            var proxy = (IStateService)Create<IStateService, ModeReadState>();
            var self = (ModeReadState)(object)proxy;
            self._full = full;
            self._later = later;
            self._fullReads = fullReads;
            self._validityDays = validityDays;
            return self;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillConfig))
            {
                var read = Reads + 1;
                Reads = read;
                return read <= _fullReads ? _full : _later;
            }

            if (targetMethod?.Name == nameof(IStateService.GetAdminPersistentLoginValidityDays))
            {
                return _validityDays;
            }

            if (targetMethod?.Name == nameof(IStateService.SetAdminPersistentLoginValidityDays))
            {
                _validityDays = (int)args![0]!;
                return null;
            }

            return OrchestrationStateService.DefaultReturn(targetMethod?.ReturnType);
        }
    }

    // The state service the daemon reads its validity days from, so a test can store new days before a save.
    private static IStateService StateOf(PrefillDaemonServiceBase daemon)
        => (IStateService)typeof(PrefillDaemonServiceBase)
            .GetField("_stateService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(daemon)!;

    // A person's Log in is tracked as a sign-in run, which is how the manager tells it apart from an automatic sign-in.
    private static void UseOperationTracker(PrefillDaemonServiceBase daemon)
        => typeof(PrefillDaemonServiceBase)
            .GetField("_operationTracker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(daemon, new UnifiedOperationTracker(
                new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance));

    // Builds and registers a persistent session directly (no create round-trip), for the concurrency
    // and cancel scenarios that drive AttemptHeadlessPersistentSelfAuthAsync by hand.
    private static DaemonSession InjectedPersistentSession(TestSteamDaemon daemon, IDaemonClient client)
    {
        var createdAt = DateTime.UtcNow;
        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
            Status = DaemonSessionStatus.Active,
            IsPersistent = true,
            AuthState = DaemonAuthState.NotAuthenticated,
            CreatedAt = createdAt,
            LastLoginAt = createdAt,
            ExpiresAt = createdAt.AddDays(30),
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
        private readonly List<object?[]> _arguments = new();
        public Func<string, Task>? SendHandler { get; set; }

        /// <summary>The arguments of every notification call, in order.</summary>
        public List<object?[]> Arguments
        {
            get
            {
                lock (_eventNames)
                {
                    return _arguments.ToList();
                }
            }
        }

        public List<string> EventNames
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
                    _arguments.Add(args);
                }
                if (SendHandler is not null) return SendHandler(eventName);
            }

            return OrchestrationStateService.DefaultReturn(targetMethod?.ReturnType);
        }
    }

    // IActivityRegistry stub recording every presence report as domain/key/aspect=state.
    private class ActivityReportRecorder : NullReturningProxy
    {
        private readonly List<string> _reports = new();

        public List<string> Reports
        {
            get
            {
                lock (_reports)
                {
                    return _reports.ToList();
                }
            }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IActivityRegistry.ReportAsync) && args is [string domain, string key, string aspect, bool active, ..])
            {
                lock (_reports)
                {
                    _reports.Add($"{domain}/{key}/{aspect}={active}");
                }
            }

            return base.Invoke(targetMethod, args);
        }
    }

    // ISignalRNotificationService stub recording the stage key each hub push carries.
    private class StageKeyRecorder : NullReturningProxy
    {
        private readonly List<string?> _stageKeys = new();

        public List<string?> StageKeys
        {
            get
            {
                lock (_stageKeys)
                {
                    return _stageKeys.ToList();
                }
            }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is [string, { } payload, ..])
            {
                lock (_stageKeys)
                {
                    _stageKeys.Add(payload.GetType().GetProperty("stageKey")?.GetValue(payload) as string);
                }
            }

            return base.Invoke(targetMethod, args);
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
