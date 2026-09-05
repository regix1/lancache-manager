using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// RC3 session pinning + RC4 manager leg.
///
/// RC3: the persistent-login REST surface re-resolved "the current active session" fresh on every
/// call with zero pinning, so an in-flight login for a stopped session A transparently operated on a
/// replacement session B (leaking A's queued username challenge onto a fresh container). These tests
/// prove the pinned REST contract: challenge/credential pinned to a since-replaced session return a
/// 409 <c>session_replaced</c> and NEVER touch the replacement session's daemon; cancel is an
/// idempotent 200 that also never cancels the replacement; sessionId is required (400 when missing);
/// and a queued challenge for a terminated session is ignored instead of repopulating its cache.
///
/// RC4: a fixed daemon reports <c>Success=false</c> when it had no matching pending challenge; the
/// manager surfaces that as a 409 <c>credential_rejected</c> instead of celebrating a dropped
/// credential as accepted.
/// </summary>
public class PersistentLoginSessionPinningTests
{
    // ---- RC3: controller pinning ---------------------------------------------------------------

    [Fact]
    public async Task GetChallenge_PinnedToReplacedSession_Returns409SessionReplaced_NeverServesReplacement()
    {
        var (controller, _, activeClient) = CreateControllerWithActiveSession(activeSessionId: "session-B");

        var result = await controller.GetChallengeAsync(PrefillPlatform.Steam, sessionId: "session-A", timeoutSeconds: 1);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var body = Assert.IsType<PersistentLoginConflictResponse>(conflict.Value);
        Assert.Equal(PersistentLoginConflictReasons.SessionReplaced, body.Error);
        // The replacement session's daemon client must never be asked for a challenge.
        Assert.DoesNotContain(nameof(IDaemonClient.WaitForChallengeAsync), activeClient.InvokedMethods);
    }

    [Fact]
    public async Task ProvideCredential_PinnedToReplacedSession_Returns409_NeverInvokesReplacementDaemon()
    {
        var (controller, _, activeClient) = CreateControllerWithActiveSession(activeSessionId: "session-B");

        var request = new PersistentProvideCredentialRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-A",
            Challenge = new CredentialChallenge { ChallengeId = "c1", CredentialType = "2fa", ServerPublicKey = "k" },
            Credential = "123456"
        };

        var result = await controller.ProvideCredentialAsync(request, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var body = Assert.IsType<PersistentLoginConflictResponse>(conflict.Value);
        Assert.Equal(PersistentLoginConflictReasons.SessionReplaced, body.Error);
        Assert.DoesNotContain(nameof(IDaemonClient.ProvideCredentialAsync), activeClient.InvokedMethods);
    }

    [Fact]
    public async Task CancelLogin_PinnedToReplacedSession_IsIdempotent200_NeverCancelsReplacement()
    {
        var (controller, _, activeClient) = CreateControllerWithActiveSession(activeSessionId: "session-B");

        var request = new PersistentCancelLoginRequest { Service = PrefillPlatform.Steam, SessionId = "session-A" };

        var result = await controller.CancelLoginAsync(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.DoesNotContain(nameof(IDaemonClient.CancelLoginAsync), activeClient.InvokedMethods);
    }

    [Fact]
    public async Task GetChallenge_MissingSessionId_Returns400()
    {
        var (controller, _, _) = CreateControllerWithActiveSession(activeSessionId: "session-B");

        var result = await controller.GetChallengeAsync(PrefillPlatform.Steam, sessionId: null, timeoutSeconds: 1);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task ProvideCredential_MissingSessionId_Returns400()
    {
        var (controller, _, _) = CreateControllerWithActiveSession(activeSessionId: "session-B");

        var request = new PersistentProvideCredentialRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = null,
            Challenge = new CredentialChallenge { ChallengeId = "c1", CredentialType = "2fa", ServerPublicKey = "k" },
            Credential = "123456"
        };

        var result = await controller.ProvideCredentialAsync(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CancelLogin_MissingSessionId_Returns400()
    {
        var (controller, _, _) = CreateControllerWithActiveSession(activeSessionId: "session-B");

        var request = new PersistentCancelLoginRequest { Service = PrefillPlatform.Steam, SessionId = null };

        var result = await controller.CancelLoginAsync(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ---- RC4: manager leg surfaces a dropped credential -----------------------------------------

    [Fact]
    public async Task ProvideCredential_DaemonRejects_Returns409CredentialRejected()
    {
        var (controller, _, activeClient) = CreateControllerWithActiveSession(activeSessionId: "session-B");
        activeClient.RejectCredential = true;

        var request = new PersistentProvideCredentialRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            Challenge = new CredentialChallenge { ChallengeId = "c1", CredentialType = "2fa", ServerPublicKey = "k" },
            Credential = "123456"
        };

        var result = await controller.ProvideCredentialAsync(request, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var body = Assert.IsType<PersistentLoginConflictResponse>(conflict.Value);
        Assert.Equal(PersistentLoginConflictReasons.CredentialRejected, body.Error);
        // Proves the credential actually reached the daemon client (which then rejected it).
        Assert.Contains(nameof(IDaemonClient.ProvideCredentialAsync), activeClient.InvokedMethods);
    }

    [Fact]
    public async Task ProvideCredential_MatchingSessionAccepted_ReturnsOk()
    {
        var (controller, _, activeClient) = CreateControllerWithActiveSession(activeSessionId: "session-B");

        var request = new PersistentProvideCredentialRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            Challenge = new CredentialChallenge { ChallengeId = "c1", CredentialType = "2fa", ServerPublicKey = "k" },
            Credential = "123456"
        };

        var result = await controller.ProvideCredentialAsync(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Contains(nameof(IDaemonClient.ProvideCredentialAsync), activeClient.InvokedMethods);
    }

    [Fact]
    public async Task StartLogin_EditCommandDispatchedWithoutChallenge_CleanupCancelsAndResetsLogin()
    {
        const string sessionId = "session-shared";
        const string editSessionId = "edit-session-login-timeout";
        var (controller, daemon, activeClient) = CreateControllerWithActiveSession(sessionId);

        var login = await controller.StartLoginAsync(
            new PersistentLoginRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = sessionId,
                EditSessionId = editSessionId,
                EditActionId = "login-timeout"
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(login.Result);
        Assert.Contains(
            nameof(IDaemonClient.StartLoginWithDispatchAsync),
            activeClient.InvokedMethods);
        Assert.Equal(
            DaemonAuthState.LoggingIn,
            daemon.GetSession(sessionId)!.AuthState);
        Assert.Single(daemon.PersistentEditSessionGate.GetCompensableResources(
            editSessionId,
            PersistentPrefillEditResourceKind.Login));

        var cleanup = await controller.CleanupEditSessionAsync(
            CreateCleanupRequest(editSessionId, sessionId),
            CancellationToken.None);

        Assert.IsType<OkResult>(cleanup);
        Assert.Equal(
            1,
            activeClient.InvokedMethods.Count(
                method => method == nameof(IDaemonClient.CancelLoginAsync)));
        Assert.Equal(
            DaemonAuthState.NotAuthenticated,
            daemon.GetSession(sessionId)!.AuthState);
        Assert.Empty(daemon.PersistentEditSessionGate.GetCompensableResources(
            editSessionId,
            PersistentPrefillEditResourceKind.Login));
    }

    [Fact]
    public async Task StartLogin_EditCommandDispatchFails_CleanupDoesNotClaimOrCancelLogin()
    {
        const string sessionId = "session-shared";
        const string editSessionId = "edit-session-login-dispatch-failure";
        var (controller, daemon, activeClient) = CreateControllerWithActiveSession(sessionId);
        activeClient.FailLoginDispatch = true;

        await Assert.ThrowsAsync<IOException>(() => controller.StartLoginAsync(
            new PersistentLoginRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = sessionId,
                EditSessionId = editSessionId,
                EditActionId = "login-dispatch-failure"
            },
            CancellationToken.None));

        Assert.Empty(daemon.PersistentEditSessionGate.GetCompensableResources(
            editSessionId,
            PersistentPrefillEditResourceKind.Login));
        Assert.Equal(
            DaemonAuthState.NotAuthenticated,
            daemon.GetSession(sessionId)!.AuthState);

        var cleanup = await controller.CleanupEditSessionAsync(
            CreateCleanupRequest(editSessionId, sessionId),
            CancellationToken.None);

        Assert.IsType<OkResult>(cleanup);
        Assert.DoesNotContain(
            nameof(IDaemonClient.CancelLoginAsync),
            activeClient.InvokedMethods);
    }

    [Fact]
    public async Task PrefillConflict_CleanupDoesNotCancelExistingRun()
    {
        var (controller, daemon, activeClient) = CreateControllerWithActiveSession(
            activeSessionId: "session-shared");
        daemon.GetSession("session-shared")!.IsPrefilling = true;

        var start = await controller.StartPrefillAsync(
            new PersistentStartPrefillRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = "session-shared",
                AppIds = ["10"],
                EditSessionId = "edit-session-a",
                EditActionId = "prefill-a"
            },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(start.Result);

        var cleanup = await controller.CleanupEditSessionAsync(
            new PersistentPrefillEditSessionCleanupRequest
            {
                EditSessionId = "edit-session-a",
                CleanupId = "cleanup-a",
                Services =
                [
                    new PersistentPrefillEditSessionCleanupServiceRequest
                    {
                        Service = PrefillPlatform.Steam,
                        BaselineSessionId = "session-shared",
                        BaselineSelectedAppIds = ["baseline"],
                        StartSessionId = null,
                        LoginSessionId = null,
                        PrefillSessionId = "session-shared",
                        SelectionSessionId = "session-shared"
                    }
                ]
            },
            CancellationToken.None);

        Assert.IsType<OkResult>(cleanup);
        Assert.DoesNotContain(
            nameof(IDaemonClient.CancelPrefillAsync),
            activeClient.InvokedMethods);
        Assert.DoesNotContain(
            nameof(IDaemonClient.SetSelectedAppsAsync),
            activeClient.InvokedMethods);
        Assert.True(daemon.GetSession("session-shared")!.IsPrefilling);
    }

    [Fact]
    public async Task CreatedSessionAdoptedByLaterEdit_IsRetainedAndEarlierSelectionIsRestored()
    {
        var (controller, daemon, activeClient) = CreateControllerWithActiveSession(
            activeSessionId: "session-shared");

        var selection = await controller.SetSelectedAppsAsync(
            new PersistentSelectedAppsRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = "session-shared",
                AppIds = ["10"],
                EditSessionId = "edit-session-a",
                EditActionId = "selection-a"
            },
            CancellationToken.None);
        Assert.IsType<OkResult>(selection);

        _ = daemon.PersistentEditSessionGate.BeginStart("edit-session-a", "start-a");
        daemon.PersistentEditSessionGate.CompleteStart(
            "edit-session-a",
            "start-a",
            new PersistentPrefillEditSessionStartRecord(
                "start-a",
                "session-shared",
                CreatedByEditSession: true));
        _ = daemon.PersistentEditSessionGate.BeginStart("edit-session-b", "start-b");
        daemon.PersistentEditSessionGate.CompleteStart(
            "edit-session-b",
            "start-b",
            new PersistentPrefillEditSessionStartRecord(
                "start-b",
                "session-shared",
                CreatedByEditSession: false));

        var cleanup = await controller.CleanupEditSessionAsync(
            new PersistentPrefillEditSessionCleanupRequest
            {
                EditSessionId = "edit-session-a",
                CleanupId = "cleanup-a",
                Services =
                [
                    new PersistentPrefillEditSessionCleanupServiceRequest
                    {
                        Service = PrefillPlatform.Steam,
                        BaselineSessionId = "session-shared",
                        BaselineSelectedAppIds = ["baseline"],
                        StartSessionId = "session-shared",
                        LoginSessionId = null,
                        PrefillSessionId = null,
                        SelectionSessionId = "session-shared"
                    }
                ]
            },
            CancellationToken.None);

        Assert.IsType<OkResult>(cleanup);
        Assert.NotNull(daemon.GetSession("session-shared"));
        Assert.Equal(
            2,
            activeClient.InvokedMethods.Count(
                method => method == nameof(IDaemonClient.SetSelectedAppsAsync)));
    }

    // ---- RC3: dead-session challenge guard (#10) ------------------------------------------------

    [Fact]
    public async Task OnCredentialChallenge_ForTerminatedSession_IsIgnored_DoesNotRepopulateCache()
    {
        var daemon = CreateDaemon();
        var session = CreatePersistentSession("session-dead");
        daemon.InjectSession(session);

        // Simulate the terminate path's end state: the session flips out of Active.
        session.Status = DaemonSessionStatus.Terminated;

        await daemon.InvokeOnCredentialChallengeAsync(
            session,
            new CredentialChallenge { ChallengeId = "queued-1", CredentialType = "username" });

        Assert.Null(session.PendingLoginChallenge);
    }

    [Fact]
    public async Task OnCredentialChallenge_ForActiveSession_StillPopulatesCache()
    {
        // The guard must not regress the normal in-flight challenge path.
        var daemon = CreateDaemon();
        var session = CreatePersistentSession("session-live");
        daemon.InjectSession(session);

        await daemon.InvokeOnCredentialChallengeAsync(
            session,
            new CredentialChallenge { ChallengeId = "live-1", CredentialType = "username" });

        Assert.NotNull(session.PendingLoginChallenge);
        Assert.Equal("live-1", session.PendingLoginChallenge!.ChallengeId);
    }

    [Fact]
    public void IntegrationLoginAvailability_UsesOnlyTheTrustedCallerAccount()
    {
        var accountId = Guid.NewGuid();
        var (controller, daemon, _) = CreateControllerWithActiveSession("session-B", accountId);
        daemon.SaveLogin(accountId, "saved-account");

        var result = controller.GetIntegrationLoginAvailability(PrefillPlatform.Steam);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var availability = Assert.IsType<IntegrationLoginAvailability>(ok.Value);
        Assert.True(availability.Available);
        Assert.Equal("saved-account", availability.Account);
        Assert.Equal(accountId, daemon.AvailabilityAccountId);
    }

    [Fact]
    public void IntegrationLoginAvailability_DoesNotRevealAnotherAccountsSavedLogin()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var (controller, daemon, _) = CreateControllerWithActiveSession("session-B", accountB);
        daemon.SaveLogin(accountA, "Account A");

        var result = controller.GetIntegrationLoginAvailability(PrefillPlatform.Steam);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var availability = Assert.IsType<IntegrationLoginAvailability>(ok.Value);
        Assert.False(availability.Available);
        Assert.Null(availability.Account);
        Assert.Equal("no-saved-login", availability.Reason);
        Assert.Equal(accountB, daemon.AvailabilityAccountId);
    }

    [Fact]
    public void IntegrationLoginAvailability_WithoutStableAccountExplainsRequirement()
    {
        var (controller, _, _) = CreateControllerWithActiveSession("session-B", accountId: null);

        var result = controller.GetIntegrationLoginAvailability(PrefillPlatform.Steam);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var availability = Assert.IsType<IntegrationLoginAvailability>(ok.Value);
        Assert.False(availability.Available);
        Assert.Null(availability.Account);
        Assert.Equal("account-required", availability.Reason);
    }

    [Fact]
    public async Task ReuseSavedLogin_PassesTrustedAccountAndConfirmsPostDispatchAsync()
    {
        var accountId = Guid.NewGuid();
        var (controller, daemon, _) = CreateControllerWithActiveSession("session-B", accountId);
        daemon.SaveLogin(accountId, "saved-account");

        var result = await controller.StartLoginAsync(new PersistentLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            ReuseIntegration = true
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<PersistentLoginStatusResponse>(ok.Value);
        Assert.Equal("logged-in", status.Status);
        Assert.Equal("Logged in", status.Message);
        Assert.Equal(accountId, daemon.ReuseAccountId);
    }

    [Fact]
    public async Task ReuseSavedLogin_WithoutStableAccountReturns403Async()
    {
        var (controller, daemon, _) = CreateControllerWithActiveSession("session-B", accountId: null);

        var result = await controller.StartLoginAsync(new PersistentLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            ReuseIntegration = true
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Null(daemon.ReuseAccountId);
    }

    [Fact]
    public async Task ReuseSavedLogin_RejectsAuthenticationAlreadyInProgressAsync()
    {
        var accountId = Guid.NewGuid();
        var (controller, daemon, _) = CreateControllerWithActiveSession("session-B", accountId);
        daemon.SaveLogin(accountId, "saved-account");
        daemon.GetSession("session-B")!.AuthState = DaemonAuthState.LoggingIn;

        var thrown = await Assert.ThrowsAsync<ConflictException>(() => controller.StartLoginAsync(
            new PersistentLoginRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = "session-B",
                ReuseIntegration = true
            },
            CancellationToken.None));

        Assert.Equal("errors.prefill.loginInProgress", thrown.StageKey);
        Assert.Null(daemon.ReuseAccountId);
    }

    [Fact]
    public async Task ReuseSavedLogin_RequiresExplicitLogoutOfAuthenticatedTargetAsync()
    {
        var (controller, daemon, client) = CreateControllerWithActiveSession("session-B", Guid.NewGuid());
        client.LiveStatus = "logged-in";

        var thrown = await Assert.ThrowsAsync<ConflictException>(() => controller.StartLoginAsync(
            new PersistentLoginRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = "session-B",
                ReuseIntegration = true
            },
            CancellationToken.None));

        Assert.Equal("errors.prefill.logoutBeforeSavedLogin", thrown.StageKey);
        Assert.Null(daemon.ReuseAccountId);
    }

    // ---- Fixtures -------------------------------------------------------------------------------

    private static (PersistentPrefillController Controller, TestableSteamDaemonService Daemon, RecordingDaemonClientProxy ActiveClient)
        CreateControllerWithActiveSession(string activeSessionId, Guid? accountId = null)
    {
        var daemon = CreateDaemon();

        var client = DispatchProxy.Create<IDaemonClient, RecordingDaemonClientProxy>();
        var recorder = (RecordingDaemonClientProxy)client;

        var session = CreatePersistentSession(activeSessionId);
        session.Client = client;
        daemon.InjectSession(session);

        var stateService = (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"session_pinning_{Guid.NewGuid():N}")
            .Options;
        var dbFactory = new InMemoryDbContextFactory(dbOptions);
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        var provider = new SingleDaemonServiceProvider(daemon);

        var controller = new PersistentPrefillController(
            provider, stateService, cacheService, NullLogger<PersistentPrefillController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.HttpContext.Items["Session"] = new UserSession
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            SessionType = SessionType.Admin
        };

        return (controller, daemon, recorder);
    }

    private static PersistentPrefillEditSessionCleanupRequest CreateCleanupRequest(
        string editSessionId,
        string baselineSessionId) => new()
    {
        EditSessionId = editSessionId,
        CleanupId = $"cleanup-{editSessionId}",
        Services =
        [
            new PersistentPrefillEditSessionCleanupServiceRequest
            {
                Service = PrefillPlatform.Steam,
                BaselineSessionId = baselineSessionId,
                BaselineSelectedAppIds = [],
                StartSessionId = null,
                LoginSessionId = baselineSessionId,
                PrefillSessionId = null,
                SelectionSessionId = null
            }
        ]
    };

    private static TestableSteamDaemonService CreateDaemon()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"session_pinning_daemon_{Guid.NewGuid():N}")
            .Options;
        var dbFactory = new InMemoryDbContextFactory(dbOptions);
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var configuration = new ConfigurationBuilder().Build();
        var pathResolver = (IPathResolver)DispatchProxy.Create<IPathResolver, NullReturningProxy>();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();
        var networkOptions = new StaticOptionsMonitor<PrefillNetworkOptions>(new PrefillNetworkOptions());

        return new TestableSteamDaemonService(
            NullLogger<SteamDaemonService>.Instance, notifications, configuration, pathResolver,
            stateService, sessionService, cacheService, networkOptions);
    }

    private static DaemonSession CreatePersistentSession(string sessionId) => new()
    {
        Id = sessionId,
        UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
        Status = DaemonSessionStatus.Active,
        IsPersistent = true,
        AuthState = DaemonAuthState.NotAuthenticated,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(30),
        Client = (IDaemonClient)DispatchProxy.Create<IDaemonClient, NullReturningProxy>()
    };

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
            IOptionsMonitor<PrefillNetworkOptions> networkOptions)
            : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions, new TestLancacheServerLocator(), new UnavailableContainerGatewayFactory())
        {
        }

        public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;

        public Guid? AvailabilityAccountId { get; private set; }
        public Guid? ReuseAccountId { get; private set; }
        private Dictionary<Guid, string> SavedAccounts { get; } = [];

        public void SaveLogin(Guid accountId, string account) => SavedAccounts[accountId] = account;

        public override IntegrationLoginAvailability GetIntegrationLoginAvailability(Guid? accountId)
        {
            AvailabilityAccountId = accountId;
            if (accountId is null)
            {
                return new IntegrationLoginAvailability(false, null, "account-required");
            }

            return SavedAccounts.TryGetValue(accountId.Value, out var account)
                ? new IntegrationLoginAvailability(true, account, null)
                : new IntegrationLoginAvailability(false, null, "no-saved-login");
        }

        protected override Task<bool> ReuseIntegrationLoginAsync(
            DaemonSession session,
            Guid accountId,
            Action onCommandDispatched,
            CancellationToken cancellationToken)
        {
            ReuseAccountId = accountId;
            onCommandDispatched();
            ((RecordingDaemonClientProxy)(object)session.Client).LiveStatus = "logged-in";
            return Task.FromResult(true);
        }

        public Task InvokeOnCredentialChallengeAsync(DaemonSession session, CredentialChallenge challenge)
            => OnCredentialChallengeAsync(session, challenge);
    }

    /// <summary>
    /// Minimal <see cref="IServiceProvider"/> that resolves only <see cref="SteamDaemonService"/> - the
    /// single concrete type the exercised endpoints request via
    /// <see cref="PrefillDaemonServiceBase.ResolveDaemon"/>.
    /// </summary>
    private sealed class SingleDaemonServiceProvider : IServiceProvider
    {
        private readonly SteamDaemonService _steam;

        public SingleDaemonServiceProvider(SteamDaemonService steam) => _steam = steam;

        public object? GetService(Type serviceType)
            => serviceType == typeof(SteamDaemonService) ? _steam : null;
    }

    /// <summary>
    /// Records every <see cref="IDaemonClient"/> method invoked so a test can prove a replacement
    /// session's client was NOT touched, and can optionally throw
    /// <see cref="DaemonCredentialRejectedException"/> to simulate a fixed daemon dropping a credential.
    /// </summary>
    // Not sealed: DispatchProxy.Create returns an instance that is both IDaemonClient and this proxy,
    // and the (RecordingDaemonClientProxy)client cast a test uses is only legal when the target class
    // is not sealed (a sealed class the interface does not declare yields CS0030).
    private class RecordingDaemonClientProxy : DispatchProxy
    {
        public List<string> InvokedMethods { get; } = new();
        public bool RejectCredential { get; set; }
        public bool FailLoginDispatch { get; set; }
        public string LiveStatus { get; set; } = "awaiting-login";

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is not null)
            {
                InvokedMethods.Add(targetMethod.Name);
            }

            if (RejectCredential && targetMethod?.Name == nameof(IDaemonClient.ProvideCredentialAsync))
            {
                throw new DaemonCredentialRejectedException("No matching login challenge is pending for this credential");
            }

            if (targetMethod?.Name == nameof(IDaemonClient.StartLoginWithDispatchAsync))
            {
                if (FailLoginDispatch)
                {
                    return Task.FromException<CredentialChallenge?>(
                        new IOException("Login command failed before dispatch"));
                }

                ((Action)args![1]!)();
                return Task.FromResult<CredentialChallenge?>(null);
            }

            if (targetMethod?.Name == nameof(IDaemonClient.GetStatusAsync))
            {
                return Task.FromResult<DaemonStatus?>(new DaemonStatus { Status = LiveStatus });
            }

            return DefaultReturnValue(targetMethod);
        }
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext() => new(_options);

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

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = returnType.GetGenericArguments()[0];
            var value = inner.IsValueType && Nullable.GetUnderlyingType(inner) is null
                ? Activator.CreateInstance(inner)
                : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { value });
        }

        if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
        {
            return Activator.CreateInstance(returnType);
        }

        return null;
    }
}
