using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A prefill sign-in is a server run: it registers one operation with a manual notice, the platform
/// as its metadata, and the auth session that started it as its owner, so only that browser draws it.
/// A persistent session runs under the system owner, so its sign-in must take the owner from the
/// request that started it instead.
/// </summary>
public class PrefillLoginRunTests
{
    [Fact]
    public async Task HubLogin_RegistersOneSignInOwnedByTheCallersAuthSession()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var callerSessionId = Guid.NewGuid();
        var (daemon, session) = CreateSessionWithClient(tracker, callerSessionId, isPersistent: false);

        var challenge = await daemon.StartLoginAsync(session.Id);

        var operationId = Assert.IsType<Guid>(session.LoginOperationId);
        Assert.Equal(operationId.ToString(), challenge!.OperationId);
        AssertSignIn(tracker, operationId, callerSessionId);
    }

    [Fact]
    public async Task PersistentLogin_ThroughTheController_IsOwnedByTheCallerNotTheSystemOwner()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(
            tracker, ScheduledPrefillConstants.DeriveSystemUserId(), isPersistent: true);
        var callerSessionId = Guid.NewGuid();
        var controller = CreateController(daemon, callerSessionId);

        var result = await controller.StartLoginAsync(
            new PersistentLoginRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = session.Id,
                EditSessionId = "edit-session-login",
                EditActionId = "login"
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);
        AssertSignIn(tracker, operationId, callerSessionId);
        Assert.NotEqual(session.UserId, tracker.GetOperation(operationId)!.OwnerSessionId);
    }

    [Fact]
    public async Task HeadlessLogin_RegistersNothing()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(
            tracker, ScheduledPrefillConstants.DeriveSystemUserId(), isPersistent: true);
        session.SuppressLoginChallengePublication = true;

        await daemon.StartLoginAsync(session.Id);

        Assert.Null(session.LoginOperationId);
        Assert.Empty(tracker.GetRuns().Runs);
    }

    /// <summary>
    /// A guest has no notification bar and cannot close a kept card, so a guest's failed sign-in ends
    /// without one; an account holder's failed sign-in keeps its card until closed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AGuestsSignInEndsWithoutAKeptCard(bool isTemporary)
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false, isTemporary);
        await daemon.StartLoginAsync(session.Id);
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);

        session.LastLoginFailureMessage = "Sign-in was refused.";
        await daemon.CancelLoginAsync(session.Id);

        Assert.Equal(!isTemporary, tracker.GetRuns().Runs.Any(row => row.OperationId == operationId && row.Retained));
    }

    /// <summary>
    /// A status refresh reads the daemon's idle "awaiting-login" while a sign-in is still running, so it
    /// must not end the sign-in; a refresh that finds the daemon signed in ends it as a success.
    /// </summary>
    [Theory]
    [InlineData("awaiting-login", false)]
    [InlineData("logged-in", true)]
    public async Task ARefreshDuringASignInEndsItOnlyWhenSignedIn(string reply, bool signedIn)
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        await daemon.StartLoginAsync(session.Id);
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);
        var status = RunClient.Capabilities(Guid.NewGuid().ToString());
        status.Status = reply;
        ((ScriptedLoginDaemonClient)session.Client).StatusOverride = status;

        await daemon.GetSessionStatusAsync(session.Id);

        Assert.Equal(signedIn ? OperationStatus.Completed : OperationStatus.Running, tracker.GetOperation(operationId)!.Status);
    }

    /// <summary>
    /// The daemon sends each prompt as an event before the login call returns it, and the event moves the
    /// session to the prompt's state; the sign-in is still running at that point and ends when the daemon
    /// reports the result.
    /// </summary>
    [Fact]
    public async Task APromptDoesNotEndTheSignIn()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        var client = (ScriptedLoginDaemonClient)session.Client;
        // The state the service's own challenge handler writes for a username challenge.
        client.OnCredentialChallenge += _ =>
        {
            session.AuthState = DaemonAuthState.UsernameRequired;
            return Task.CompletedTask;
        };

        await daemon.StartLoginAsync(session.Id);
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(operationId)!.Status);

        client.StatusOverride = RunClient.Capabilities(Guid.NewGuid().ToString());
        await daemon.GetSessionStatusAsync(session.Id);
        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(operationId)!.Status);
    }

    /// <summary>
    /// The daemon reports a refused sign-in as "awaiting-login" with a "Login failed:" message through its
    /// status push, which can arrive while the login call still waits (a saved login) or after it returned
    /// a prompt. That sign-in ends as a failure that stays until closed; any other sign-out ends it as a
    /// cancel that leaves on its own.
    /// </summary>
    [Theory]
    [InlineData("Login failed: Credentials rejected.", DaemonAuthState.LoggingIn, OperationStatus.Failed)]
    [InlineData("Login failed: Credentials rejected.", DaemonAuthState.PasswordRequired, OperationStatus.Failed)]
    [InlineData("Login cancelled", DaemonAuthState.PasswordRequired, OperationStatus.Cancelled)]
    public async Task APushedSignInFailureEndsItAsAFailure(string message, DaemonAuthState stateAtPush, OperationStatus expected)
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        await daemon.StartLoginAsync(session.Id);
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);
        session.AuthState = stateAtPush;

        await DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session,
            new DaemonStatus { Status = "awaiting-login", Message = message });

        Assert.Equal(expected, tracker.GetOperation(operationId)!.Status);
        Assert.Equal(expected == OperationStatus.Failed,
            tracker.GetRuns().Runs.Any(row => row.OperationId == operationId && row.Retained));
    }

    /// <summary>
    /// A daemon without concurrent prefill and with no run that drops its connection while a sign-in waits
    /// at a prompt leaves nothing to answer that prompt, and the expiry sweeps skip the Error session it
    /// becomes. The sign-in must end as a failure that stays until closed, not run forever.
    /// </summary>
    [Fact]
    public async Task ADisconnectAtAPromptEndsTheSignInAsAFailure()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        await daemon.StartLoginAsync(session.Id);
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);
        session.AuthState = DaemonAuthState.PasswordRequired;

        await DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "OnDisconnectedAsync", session, session.Client);
        await daemon.ProcessSessionExpiryAsync(DateTime.UtcNow.AddDays(40));

        Assert.Equal(DaemonSessionStatus.Error, session.Status);
        Assert.Equal(OperationStatus.Failed, tracker.GetOperation(operationId)!.Status);
        Assert.Contains(tracker.GetRuns().Runs, row => row.OperationId == operationId && row.Retained);
    }

    /// <summary>
    /// A persistent container's refused sign-in already shows as the red line on its container card, so
    /// the sign-in's own run ends without a kept card; a refused sign-in on the Prefill page keeps its card.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARefusedContainerSignInKeepsNoRunCard(bool isPersistent)
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent);
        await (isPersistent
            ? daemon.StartLoginForEditAsync(session.Id, null, () => { }, Guid.NewGuid())
            : daemon.StartLoginAsync(session.Id));
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);

        session.LastLoginFailureMessage = "Sign-in was refused.";
        await daemon.CancelLoginAsync(session.Id);

        Assert.Equal(!isPersistent, tracker.GetRuns().Runs.Any(row => row.OperationId == operationId && row.Retained));
    }

    private static void AssertSignIn(UnifiedOperationTracker tracker, Guid operationId, Guid ownerSessionId)
    {
        var operation = Assert.IsType<OperationInfo>(tracker.GetOperation(operationId));
        Assert.Equal(OperationType.PrefillLogin, operation.Type);
        Assert.Equal(PrefillPlatform.Steam, operation.Metadata);
        var notice = Assert.IsType<RunNotice>(operation.Notice);
        Assert.Equal(NotificationMode.All, notice.Mode);
        Assert.Equal(RunTrigger.Manual, notice.Trigger);
        Assert.Equal(ownerSessionId, operation.OwnerSessionId);

        var row = Assert.Single(tracker.GetRuns().Runs);
        Assert.Equal(operationId, row.OperationId);
        Assert.Equal(ownerSessionId, row.OwnerSessionId);
        Assert.Equal(PrefillPlatform.Steam, row.ServiceId);
        Assert.Equal(RunVisibility.Card, row.Visibility);
    }

    private static PersistentPrefillController CreateController(TestableSteamDaemonService daemon, Guid callerSessionId)
    {
        var contexts = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"prefill_login_controller_{Guid.NewGuid():N}")
            .Options);
        var controller = new PersistentPrefillController(
            new ServiceCollection().AddSingleton<SteamDaemonService>(daemon).BuildServiceProvider(),
            (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>(),
            new PrefillCacheService(contexts, NullLogger<PrefillCacheService>.Instance),
            NullLogger<PersistentPrefillController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Items["Session"] = new UserSession
        {
            Id = callerSessionId,
            SessionType = SessionType.Admin
        };
        return controller;
    }

    private static (TestableSteamDaemonService Daemon, DaemonSession Session) CreateSessionWithClient(
        IUnifiedOperationTracker tracker, Guid userId, bool isPersistent, bool isTemporary = false)
    {
        var contexts = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"prefill_login_{Guid.NewGuid():N}")
            .Options);
        var daemon = new TestableSteamDaemonService(
            NullLogger<SteamDaemonService>.Instance,
            (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            new ConfigurationBuilder().Build(),
            (IPathResolver)DispatchProxy.Create<IPathResolver, NullReturningProxy>(),
            (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>(),
            new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance),
            new PrefillCacheService(contexts, NullLogger<PrefillCacheService>.Instance),
            new StaticOptionsMonitor<PrefillNetworkOptions>(new PrefillNetworkOptions()),
            operationTracker: tracker);

        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = userId,
            Status = DaemonSessionStatus.Active,
            IsPersistent = isPersistent,
            IsTemporary = isTemporary,
            AuthState = DaemonAuthState.NotAuthenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            // A username challenge keeps the sign-in open the way a real one does while it waits
            // for the user.
            Client = new ScriptedLoginDaemonClient(challengeOnLogin: new CredentialChallenge
            {
                ChallengeId = "username-challenge",
                CredentialType = "username",
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            })
        };
        daemon.InjectSession(session);

        return (daemon, session);
    }
}
