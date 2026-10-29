using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Proves
/// <see cref="PrefillDaemonServiceBase.LogoutPersistentSessionAsync(string, CancellationToken)"/>,
/// which lets the "Log out" button forget a persistent container's stored account IN PLACE (no
/// container restart) when the daemon supports the <c>logout</c> command. A daemon reporting failure
/// or an exception from the round-trip leaves the running container and its last confirmed auth state
/// intact so the controller can report a typed conflict. The pending login challenge is cleared
/// unconditionally, before the daemon round-trip even starts (mirrors <c>CancelLoginAsync</c>'s
/// ordering) - logout intent is terminal, so unlike cancel, a failed round-trip does not restore it.
/// </summary>
public class PersistentLogoutTests
{
    [Fact]
    public async Task LogoutPersistentSessionAsync_DaemonSucceeds_ClearsAuthStateAndChallenge_ReturnsLoggedOutTrue()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, session) = CreateSessionWithClient(client);
        session.AuthState = DaemonAuthState.Authenticated;
        session.NeedsRelogin = true;
        session.PendingLoginChallenge = new CredentialChallenge { ChallengeId = "stale-chal", CredentialType = "username" };

        var result = await daemon.LogoutPersistentSessionAsync(session.Id, CancellationToken.None);

        Assert.True(result.LoggedOut);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
        Assert.False(session.NeedsRelogin);
        Assert.Null(session.PendingLoginChallenge);
        Assert.Equal(1, client.LogoutCallCount);
        Assert.Same(session, daemon.GetSession(session.Id));
        Assert.Same(client, session.Client);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
    }

    [Fact]
    public async Task LogoutPersistentSessionAsync_DaemonReportsFailure_ReturnsLoggedOutFalse_NoTeardown()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: false);
        var (daemon, session) = CreateSessionWithClient(client);
        session.AuthState = DaemonAuthState.Authenticated;
        var pending = new CredentialChallenge { ChallengeId = "still-pending", CredentialType = "username" };
        session.PendingLoginChallenge = pending;

        var result = await daemon.LogoutPersistentSessionAsync(session.Id, CancellationToken.None);

        Assert.False(result.LoggedOut);
        // A refusal must never touch the last confirmed auth state. The pending challenge is cleared
        // unconditionally before the daemon round-trip even starts (mirrors CancelLoginAsync's
        // ordering) and is NOT restored on failure - logout intent is terminal.
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Null(session.PendingLoginChallenge);
        Assert.Same(session, daemon.GetSession(session.Id));
        Assert.Same(client, session.Client);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
    }

    [Fact]
    public async Task LogoutPersistentSessionAsync_DaemonThrows_TreatedSameAsNotSupported_NoTeardown()
    {
        var client = new ThrowingLogoutDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);
        session.AuthState = DaemonAuthState.Authenticated;
        var pending = new CredentialChallenge { ChallengeId = "still-pending", CredentialType = "username" };
        session.PendingLoginChallenge = pending;

        var result = await daemon.LogoutPersistentSessionAsync(session.Id, CancellationToken.None);

        Assert.False(result.LoggedOut);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Null(session.PendingLoginChallenge);
        Assert.Same(session, daemon.GetSession(session.Id));
        Assert.Same(client, session.Client);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
    }

    [Fact]
    public async Task LogoutPersistentSessionAsync_CallerCancellation_PropagatesAndKeepsContainer()
    {
        var client = new CancelingLogoutDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);
        session.AuthState = DaemonAuthState.Authenticated;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => daemon.LogoutPersistentSessionAsync(session.Id, cancellation.Token));

        Assert.Equal(cancellation.Token, thrown.CancellationToken);
        Assert.Same(session, daemon.GetSession(session.Id));
        Assert.Same(client, session.Client);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.False(session.AdmissionClosed);
    }

    [Fact]
    public async Task LogoutPersistentSessionAsync_InternalCommandCancellation_ReturnsFailureAndKeepsContainer()
    {
        var client = new InternallyCanceledLogoutDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);
        session.AuthState = DaemonAuthState.Authenticated;

        var result = await daemon.LogoutPersistentSessionAsync(session.Id, CancellationToken.None);

        Assert.False(result.LoggedOut);
        Assert.Same(session, daemon.GetSession(session.Id));
        Assert.Same(client, session.Client);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.False(session.AdmissionClosed);
    }

    [Fact]
    public async Task LogoutPersistentSessionAsync_ClearsChallengeAndPendingWaitBeforeDaemonRoundTrip()
    {
        // Proves the CancelLoginAsync-style ordering: the challenge cache and the daemon client's own
        // pending-wait are both cleared BEFORE the (possibly slow) daemon round-trip, not after - a
        // concurrent resume/poll landing during that await must never be able to serve a challenge
        // that a logout is in the middle of tearing down.
        var client = new OrderRecordingLogoutDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);
        client.Session = session;
        session.AuthState = DaemonAuthState.Authenticated;
        session.PendingLoginChallenge = new CredentialChallenge { ChallengeId = "stale-chal", CredentialType = "username" };

        var result = await daemon.LogoutPersistentSessionAsync(session.Id, CancellationToken.None);

        Assert.True(result.LoggedOut);
        Assert.Equal(new[] { "ClearPendingChallenges", "LogoutAsync" }, client.CallOrder);
        Assert.True(client.ChallengeWasAlreadyNullDuringLogoutCall);
    }

    [Fact]
    public async Task LogoutPersistentSessionAsync_NonPersistentSession_ReturnsLoggedOutFalse_NeverCallsDaemon()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: false);
        session.AuthState = DaemonAuthState.Authenticated;

        var result = await daemon.LogoutPersistentSessionAsync(session.Id, CancellationToken.None);

        Assert.False(result.LoggedOut);
        Assert.Equal(0, client.LogoutCallCount);
        // Defense-in-depth guard must short-circuit before touching auth state at all.
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
    }

    [Fact]
    public async Task LogoutPersistentSessionAsync_UnknownSessionId_ThrowsKeyNotFound()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, _) = CreateSessionWithClient(client);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => daemon.LogoutPersistentSessionAsync("no-such-session", CancellationToken.None));
    }

    [Fact]
    public async Task LogoutPersistentSessionAsync_DaemonRejectsPreLoginLogout_ReturnsLoggedOutFalse_NoTeardown_DistinctLog()
    {
        // Older daemon image: its pre-login command gate rejects "logout" outright while the session
        // hasn't finished authenticating (erase-on-stop regression diagnosis). Must still return
        // forgotten=false (the frontend routes this to cancelling the in-flight login instead of a
        // stop+restart), but the log line must say so distinctly rather than reading as a genuine
        // daemon failure every single time an admin cancels a mid-challenge login.
        var client = new PreLoginRejectionDaemonClient();
        var logger = new CapturingLogger();
        var (daemon, session) = CreateSessionWithClient(client, logger: logger);
        session.AuthState = DaemonAuthState.Authenticated;
        var pending = new CredentialChallenge { ChallengeId = "still-pending", CredentialType = "username" };
        session.PendingLoginChallenge = pending;

        var result = await daemon.LogoutPersistentSessionAsync(session.Id, CancellationToken.None);

        Assert.False(result.LoggedOut);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Null(session.PendingLoginChallenge);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("before authentication completed"));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("Daemon reported logout failed"));
    }

    private static (PrefillDaemonServiceBase Daemon, DaemonSession Session) CreateSessionWithClient(
        IDaemonClient client, bool isPersistent = true, ILogger<SteamDaemonService>? logger = null)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"persistent_logout_{Guid.NewGuid():N}")
            .Options;
        var dbFactory = new TestDbContextFactory(dbOptions);
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
    // seam in PersistentLoginChallengeResumeTests.cs / PersistentLoginFailFastTests.cs.
    /// <summary>
    /// Records the order in which <see cref="ClearPendingChallenges"/> and <see cref="LogoutAsync"/>
    /// are invoked, and snapshots whether the session's cached <c>PendingLoginChallenge</c> was already
    /// cleared by the time the daemon round-trip starts - proves the clear-before-round-trip ordering
    /// rather than just its end state.
    /// </summary>
    private sealed class OrderRecordingLogoutDaemonClient : TestDaemonClientBase
    {
        public List<string> CallOrder { get; } = new();
        public DaemonSession? Session { get; set; }
        public bool ChallengeWasAlreadyNullDuringLogoutCall { get; private set; }

        public override void ClearPendingChallenges()
        {
            CallOrder.Add(nameof(ClearPendingChallenges));
        }

        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
        {
            CallOrder.Add(nameof(LogoutAsync));
            ChallengeWasAlreadyNullDuringLogoutCall = Session?.PendingLoginChallenge is null;
            return Task.FromResult(true);
        }
    }

    private sealed class CancelingLogoutDaemonClient : TestDaemonClientBase
    {
        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
            => Task.FromCanceled<bool>(cancellationToken);
    }

    private sealed class InternallyCanceledLogoutDaemonClient : TestDaemonClientBase
    {
        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
            => Task.FromException<bool>(new OperationCanceledException("Daemon command timed out."));
    }

}

public sealed partial class PrefillContainerOrchestrationTests
{
    // An admin action whose prefill cleanup did not finish names that with a key the page shows in the reader's language.
    [Theory]
    [InlineData("terminate", "errors.prefill.cleanupIncomplete")]
    [InlineData("terminate-all", "errors.prefill.terminateAllIncomplete")]
    [InlineData("ban", "errors.prefill.banSavedCleanupIncomplete")]
    public async Task AdminActionWhoseCleanupFailsAnswersItsStageKeyAsync(string action, string stageKey)
    {
        var (_, contexts) = NewDatabase();
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var deps = MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true));
        var gateways = Enumerable.Range(0, 5).Select(_ => new RecordingContainerGateway()).ToArray();
        var daemons = new PrefillDaemonServiceBase[]
        {
            new TestSteamDaemon(deps, gateways[0]), new TestEpicDaemon(deps, gateways[1]),
            new TestXboxDaemon(deps, gateways[2]), new TestBattleNetDaemon(deps, gateways[3]),
            new TestRiotDaemon(deps, gateways[4])
        };
        var owner = new UserSession
        {
            Id = Guid.NewGuid(), SessionType = SessionType.Guest, SessionTokenHash = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        await using (var database = await contexts.CreateDbContextAsync())
        {
            database.UserSessions.Add(owner);
            await database.SaveChangesAsync();
        }
        var guest = AddGuest(daemons[0], gateways[0], owner.Id);
        await history.CreateSessionAsync(guest.Id, owner.Id, guest.ContainerId, guest.ContainerName, guest.ExpiresAt, DateTime.UtcNow);
        var controller = new PrefillAdminController(history, (SteamDaemonService)daemons[0], (EpicPrefillDaemonService)daemons[1],
            (BattleNetDaemonService)daemons[3], (RiotDaemonService)daemons[4], (XboxPrefillDaemonService)daemons[2],
            deps.CacheService, deps.Notifications, NullLogger<PrefillAdminController>.Instance);
        var http = new DefaultHttpContext();
        http.Items["Session"] = new UserSession { Id = Guid.NewGuid(), SessionType = SessionType.Admin };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        gateways[0].FailNextRemoveContainer(new InvalidOperationException("Injected removal failure"));

        ActionResult? answer = action switch
        {
            "terminate" => (await controller.TerminateAsync(guest.Id)).Result,
            "terminate-all" => (await controller.TerminateAllAsync()).Result,
            "ban" => (await controller.BanBySessionAsync(guest.Id, new BanRequest())).Result,
            _ => throw new InvalidOperationException("Unknown test action")
        };

        var refusal = Assert.IsAssignableFrom<ObjectResult>(answer);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, refusal.StatusCode);
        Assert.Equal(stageKey, Assert.IsType<ErrorResponse>(refusal.Value).StageKey);
    }

    // A Stop retried after Docker did not answer the login volume removal finishes that Stop instead of
    // answering that the session does not exist.
    [Fact]
    public async Task RetriedStopAfterTheVolumeRemovalTimedOutFinishesTheStopAsync()
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

        async Task<(int Status, ActionResult? Answer, byte[] Body)> StopAsync()
        {
            ActionResult? answer = null;
            var middleware = new GlobalExceptionMiddleware(
                async _ => answer = await controller.StopAsync(new StopPersistentSessionRequest { SessionId = session.Id }),
                NullLogger<GlobalExceptionMiddleware>.Instance,
                DispatchProxy.Create<IHostEnvironment, NullReturningProxy>());
            var context = new DefaultHttpContext();
            var body = new MemoryStream();
            context.Response.Body = body;
            await middleware.InvokeAsync(context);
            return (context.Response.StatusCode, answer, body.ToArray());
        }

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (await StopAsync()).Status);
        var retry = await StopAsync();

        Assert.Null(retry.Answer);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, retry.Status);
        using (var answer = JsonDocument.Parse(retry.Body))
        {
            Assert.Equal("errors.prefill.dockerUnavailable", answer.RootElement.GetProperty("stageKey").GetString());
        }
        gateway.HoldRemoveVolume = false;
        var last = await StopAsync();
        Assert.IsType<OkResult>(last.Answer);
        Assert.Equal(StatusCodes.Status200OK, last.Status);
    }

    // Clear stored logins against a Docker that never answers asks for one connect in all, not one for each
    // service's adopt and another for its volume removal.
    [Fact]
    public async Task ClearAllPersistentLogins_DockerNeverConnects_WaitsForOneConnectAsync()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var config = Config(PersistenceMode.FullPersistence, steamEnabled: true);
        var steamGateway = new RecordingContainerGateway(available: false) { HoldGetVersion = true };
        var epicGateway = new RecordingContainerGateway(available: false) { HoldGetVersion = true };
        using var steam = new TestSteamDaemon(MakeDeps(database, sessionService, config), steamGateway);
        using var epic = new TestEpicDaemon(MakeDeps(database, sessionService, config), epicGateway);
        await using var services = new ServiceCollection()
            .AddSingleton<SteamDaemonService>(steam)
            .AddSingleton<EpicPrefillDaemonService>(epic)
            .BuildServiceProvider();

        var results = await PrefillDaemonServiceBase.ClearAllPersistentLoginsAsync(services, CancellationToken.None);

        Assert.Equal(new[] { PrefillPlatform.Steam, PrefillPlatform.Epic }, results.Select(row => row.Service));
        Assert.All(results, row =>
        {
            Assert.False(row.Success);
            Assert.Equal("DockerUnavailable", row.Detail);
        });
        Assert.Equal(1, steamGateway.CountOf("Version") + epicGateway.CountOf("Version"));
    }
}
