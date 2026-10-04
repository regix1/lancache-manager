using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        if (signedIn)
        {
            var ending = Assert.IsType<PrefillLoginEnding>(session.LastLoginEnding);
            Assert.Equal(OperationStatus.Completed, ending.Status);
            Assert.Equal("prefill.persistent.status.loggedIn", ending.StageKey);
        }
        else
        {
            Assert.Null(session.LastLoginEnding);
        }
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
        var ending = Assert.IsType<PrefillLoginEnding>(session.LastLoginEnding);
        Assert.Equal(session.LoginAttempt, ending.LoginAttempt);
        Assert.Equal(expected, ending.Status);
        Assert.Equal(
            expected == OperationStatus.Failed ? "prefill.auth.signInRefused" : "errors.integration.attemptExpired",
            ending.StageKey);
    }

    /// <summary>
    /// A browser that missed the auth-state event reads how the sign-in ended from the session snapshot every
    /// resubscribe sends.
    /// </summary>
    [Fact]
    public async Task ASessionSnapshotCarriesItsLastSignInsEnding()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        await daemon.StartLoginAsync(session.Id);
        session.AuthState = DaemonAuthState.PasswordRequired;

        await DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session,
            new DaemonStatus { Status = "awaiting-login", Message = "Login failed: Credentials rejected." });

        var snapshot = DaemonSessionDto.FromSession(session);
        Assert.NotNull(snapshot.LoginEnding);
        Assert.Equal(session.LastLoginEnding, snapshot.LoginEnding);
    }

    /// <summary>
    /// A refused persistent sign-in sends no further challenge, so a read for the attempt that was refused must
    /// answer how it ended instead of leaving the dialog to its deadline; a read for another attempt must not.
    /// </summary>
    [Fact]
    public async Task APersistentChallengeReadAnswersHowItsAttemptEnded()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(
            tracker, ScheduledPrefillConstants.DeriveSystemUserId(), isPersistent: true);
        var controller = CreateController(daemon, Guid.NewGuid());
        await controller.StartLoginAsync(
            new PersistentLoginRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = session.Id,
                EditSessionId = "edit-session-login",
                EditActionId = "login"
            },
            CancellationToken.None);
        session.AuthState = DaemonAuthState.PasswordRequired;
        await DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session,
            new DaemonStatus { Status = "awaiting-login", Message = "Login failed: Credentials rejected." });

        var ended = await controller.GetChallengeAsync(
            PrefillPlatform.Steam, sessionId: session.Id, loginAttempt: session.LoginAttempt);

        var response = Assert.IsType<PersistentLoginStatusResponse>(Assert.IsType<OkObjectResult>(ended.Result).Value);
        Assert.Equal("ended", response.Status);
        Assert.Equal(OperationStatus.Failed, Assert.IsType<PrefillLoginEnding>(response.LoginEnding).Status);
        Assert.Equal("prefill.auth.signInRefused", response.LoginEnding.StageKey);

        var other = await controller.GetChallengeAsync(
            PrefillPlatform.Steam, sessionId: session.Id, loginAttempt: session.LoginAttempt + 1);
        Assert.False((other.Result as OkObjectResult)?.Value is PersistentLoginStatusResponse { Status: "ended" });
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
        Assert.Equal("errors.prefill.requestFailed", Assert.IsType<PrefillLoginEnding>(session.LastLoginEnding).StageKey);
    }

    /// <summary>
    /// A run refused before it takes an attempt number of its own must not replace the ending the session already
    /// holds for the attempt it still carries.
    /// </summary>
    [Fact]
    public async Task ARunRefusedBeforeItsAttemptKeepsThePreviousEnding()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        await daemon.StartLoginAsync(session.Id);
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);
        session.LastLoginEnding = new PrefillLoginEnding(
            session.LoginAttempt, OperationStatus.Failed, "prefill.auth.signInRefused");
        session.LoginStopReason = "common.notifications.warnings.signInExpired";
        session.AuthState = DaemonAuthState.NotAuthenticated;

        typeof(PrefillDaemonServiceBase)
            .GetMethod("CompleteLoginOperation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(daemon, [session]);

        var ending = Assert.IsType<PrefillLoginEnding>(session.LastLoginEnding);
        Assert.Equal(OperationStatus.Failed, ending.Status);
        Assert.Equal("prefill.auth.signInRefused", ending.StageKey);
        Assert.Equal(OperationStatus.Failed, tracker.GetOperation(operationId)!.Status);
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

    /// <summary>
    /// A sign-in nobody came back to is ended by the app, so it draws red and names why instead of
    /// drawing the gray card of a cancel the person asked for.
    /// </summary>
    [Fact]
    public async Task AnAbandonedSignInTheAppEndedIsRedAsync()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        await daemon.StartLoginAsync(session.Id);
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);
        session.AuthState = DaemonAuthState.UsernameRequired;

        var result = await daemon.ProcessSessionExpiryAsync(DateTime.UtcNow.AddDays(1));

        Assert.Equal(1, result.AbandonedLoginsCancelled);
        var operation = tracker.GetOperation(operationId)!;
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Equal("common.notifications.warnings.signInExpired", Assert.Single(operation.Warnings).StageKey);
        var ending = Assert.IsType<PrefillLoginEnding>(session.LastLoginEnding);
        Assert.Equal(OperationStatus.Failed, ending.Status);
        Assert.Equal("common.notifications.warnings.signInExpired", ending.StageKey);
    }

    /// <summary>
    /// The expired reason belongs to the cancel that ends the sign-in. When the app's cancel fails and the
    /// sign-in stays open, a later cancel by the person ends it gray with no warning.
    /// </summary>
    [Fact]
    public async Task ASignInWhoseExpiryCancelFailedEndsGrayWhenThePersonCancelsItAsync()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: true);
        await daemon.StartLoginForEditAsync(session.Id, null, () => { }, Guid.NewGuid());
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);
        session.AuthState = DaemonAuthState.UsernameRequired;
        var client = (ScriptedLoginDaemonClient)session.Client;
        client.CancelAcknowledged = false;

        var result = await daemon.ProcessSessionExpiryAsync(DateTime.UtcNow.AddDays(1));

        Assert.Equal(0, result.AbandonedLoginsCancelled);
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(operationId)!.Status);
        client.CancelAcknowledged = true;
        await daemon.CancelLoginAsync(session.Id);

        var operation = tracker.GetOperation(operationId)!;
        Assert.Equal(OperationStatus.Cancelled, operation.Status);
        Assert.Empty(operation.Warnings);
    }

    /// <summary>
    /// The sweep's cancel joins a cancel the person already started; the person's cancel owns the ending,
    /// so the sign-in ends gray with no expired warning.
    /// </summary>
    [Fact]
    public async Task AnExpiryCancelThatJoinsThePersonsCancelLeavesItGrayAsync()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        await daemon.StartLoginAsync(session.Id);
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);
        session.AuthState = DaemonAuthState.UsernameRequired;
        var client = (ScriptedLoginDaemonClient)session.Client;
        client.HoldCancelLogin = true;

        var personsCancel = daemon.CancelLoginAsync(session.Id);
        await client.CancelLoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var sweep = daemon.ProcessSessionExpiryAsync(DateTime.UtcNow.AddDays(1));
        await Task.Delay(200);
        Assert.False(sweep.IsCompleted);
        client.ReleaseCancelLogin.SetResult();
        await personsCancel;
        await sweep;

        var operation = tracker.GetOperation(operationId)!;
        Assert.Equal(OperationStatus.Cancelled, operation.Status);
        Assert.Empty(operation.Warnings);
    }

    /// <summary>
    /// The daemon announces the ended sign-in before it answers the cancel, so that announcement must end
    /// the sign-in red with the expired reason, and the reason must not outlive it to turn the next
    /// sign-in's cancel red.
    /// </summary>
    [Fact]
    public async Task AnExpiredSignInEndsRedAndLeavesNoReasonForTheNextSignInAsync()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        await daemon.StartLoginAsync(session.Id);
        var firstRunId = Assert.IsType<Guid>(session.LoginOperationId);
        session.AuthState = DaemonAuthState.UsernameRequired;
        var client = (ScriptedLoginDaemonClient)session.Client;
        // A live session is wired this way at SessionLifecycle.cs:1109-1112.
        client.OnStatusUpdate += status =>
            DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session, status);

        await daemon.ProcessSessionExpiryAsync(DateTime.UtcNow.AddDays(1));

        var firstRun = tracker.GetOperation(firstRunId)!;
        Assert.Equal(OperationStatus.Failed, firstRun.Status);
        Assert.Equal("common.notifications.warnings.signInExpired", Assert.Single(firstRun.Warnings).StageKey);
        Assert.Null(session.LoginStopReason);

        await daemon.StartLoginAsync(session.Id);
        var secondRunId = Assert.IsType<Guid>(session.LoginOperationId);
        session.AuthState = DaemonAuthState.UsernameRequired;
        await daemon.CancelLoginAsync(session.Id);

        var secondRun = tracker.GetOperation(secondRunId)!;
        Assert.Equal(OperationStatus.Cancelled, secondRun.Status);
        Assert.Empty(secondRun.Warnings);
    }

    /// <summary>
    /// A new sign-in starts with no stop reason, so a reason left on the session by a cancel that judged an
    /// earlier sign-in ended never turns this sign-in's cancel red.
    /// </summary>
    [Fact]
    public async Task ANewSignInStartsWithNoStopReasonAsync()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        // What the owner branch of CancelLoginAsync leaves when the sign-in it judged still waiting ended in the instant before that branch.
        session.LoginStopReason = "common.notifications.warnings.signInExpired";
        await daemon.StartLoginAsync(session.Id);
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);
        session.AuthState = DaemonAuthState.UsernameRequired;

        await daemon.CancelLoginAsync(session.Id);

        var operation = tracker.GetOperation(operationId)!;
        Assert.Equal(OperationStatus.Cancelled, operation.Status);
        Assert.Empty(operation.Warnings);
    }

    /// <summary>
    /// The sweep reads the sign-in's state when it lists sessions, so its cancel can land after the sign-in
    /// succeeded. A cancel that names a stop reason ends only a sign-in that still waits: this one stays
    /// signed in and keeps no reason.
    /// </summary>
    [Fact]
    public async Task AnExpiryCancelThatLandsAfterTheSignInSucceededLeavesItSignedInAsync()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        await daemon.StartLoginAsync(session.Id);
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);
        var status = RunClient.Capabilities(Guid.NewGuid().ToString());
        status.Status = "logged-in";
        ((ScriptedLoginDaemonClient)session.Client).StatusOverride = status;
        await daemon.GetSessionStatusAsync(session.Id);
        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(operationId)!.Status);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);

        var canceled = await daemon.CancelLoginAsync(session.Id, stopReason: "common.notifications.warnings.signInExpired");

        Assert.False(canceled);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Null(session.LoginStopReason);
    }

    /// <summary>
    /// The daemon can finish the sign-in while the sweep's cancel is on its way to it. A daemon whose login
    /// finished announces the sign-in and leaves it alone when the cancel arrives, so the session stays signed
    /// in and the sweep does not count a cancel.
    /// </summary>
    [Fact]
    public async Task ASignInThatFinishesWhileTheExpiryCancelIsInFlightStaysSignedInAsync()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        await daemon.StartLoginAsync(session.Id);
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);
        session.AuthState = DaemonAuthState.UsernameRequired;
        var client = (ScriptedLoginDaemonClient)session.Client;
        client.HoldCancelLogin = true;

        var sweep = daemon.ProcessSessionExpiryAsync(DateTime.UtcNow.AddDays(1));
        await client.CancelLoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var status = RunClient.Capabilities(Guid.NewGuid().ToString());
        status.Status = "logged-in";
        client.StatusOverride = status;
        client.LoginFinished = true;
        await daemon.GetSessionStatusAsync(session.Id);
        client.ReleaseCancelLogin.TrySetResult();
        var result = await sweep.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(operationId)!.Status);
        Assert.Equal(0, result.AbandonedLoginsCancelled);
        Assert.True(session.LoginSettled);
    }

    /// <summary>
    /// A daemon whose sign-in finished in the same instant can answer the sweep's cancel before it announces the
    /// sign-in. Its status already reads signed in, so the session stays signed in and the sweep counts no cancel.
    /// </summary>
    [Fact]
    public async Task ASignInTheDaemonFinishedBeforeAnsweringTheExpiryCancelStaysSignedInAsync()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        await daemon.StartLoginAsync(session.Id);
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);
        session.AuthState = DaemonAuthState.UsernameRequired;
        var client = (ScriptedLoginDaemonClient)session.Client;
        client.HoldCancelLogin = true;

        var sweep = daemon.ProcessSessionExpiryAsync(DateTime.UtcNow.AddDays(1));
        await client.CancelLoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var status = RunClient.Capabilities(Guid.NewGuid().ToString());
        status.Status = "logged-in";
        client.StatusOverride = status;
        client.LoginFinished = true;
        client.ReleaseCancelLogin.TrySetResult();
        var result = await sweep.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(operationId)!.Status);
        Assert.Equal(0, result.AbandonedLoginsCancelled);
        Assert.True(session.LoginSettled);
    }

    /// <summary>
    /// A sign-in that ends signed in is settled, the same way an untracked status push settles one, so a
    /// persistent container's image update does not wait for a restart.
    /// </summary>
    [Fact]
    public async Task ASignInThatEndsSignedInIsSettledAsync()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        await daemon.StartLoginAsync(session.Id);
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);
        var status = RunClient.Capabilities(Guid.NewGuid().ToString());
        status.Status = "logged-in";
        ((ScriptedLoginDaemonClient)session.Client).StatusOverride = status;

        await daemon.GetSessionStatusAsync(session.Id);

        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(operationId)!.Status);
        Assert.True(session.LoginSettled);
    }

    /// <summary>
    /// A session the app ends (expired or shutting down) ends its open sign-in red; any other
    /// termination reason is a person's choice and stays a gray cancel.
    /// </summary>
    [Theory]
    [InlineData("Session expired", OperationStatus.Failed, "common.notifications.warnings.signInSessionEnded")]
    [InlineData("Service shutdown", OperationStatus.Failed, "common.notifications.warnings.signInSessionEnded")]
    [InlineData("User requested", OperationStatus.Cancelled, null)]
    public async Task ATerminatedSessionEndsItsSignInByWhoEndedIt(string reason, OperationStatus expected, string? warningKey)
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        await daemon.StartLoginAsync(session.Id);
        var operationId = Assert.IsType<Guid>(session.LoginOperationId);

        await daemon.TerminateSessionAsync(session.Id, reason);

        var operation = tracker.GetOperation(operationId)!;
        Assert.Equal(expected, operation.Status);
        Assert.Equal(warningKey, operation.Warnings.SingleOrDefault()?.StageKey);
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

    /// <summary>
    /// A refusal the daemon pushes for a prompt nobody answered ends that sign-in, so the next start begins a new
    /// one instead of resuming the ended attempt's cached prompt.
    /// </summary>
    [Fact]
    public async Task ARefusedSignInIsNotResumedByTheNextStart()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        var client = (ScriptedLoginDaemonClient)session.Client;
        client.OnCredentialChallenge += _ =>
        {
            session.AuthState = DaemonAuthState.UsernameRequired;
            return Task.CompletedTask;
        };

        await daemon.StartLoginAsync(session.Id);
        var firstAttempt = session.LoginAttempt;
        Assert.NotNull(session.PendingLoginChallenge);

        await DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session,
            new DaemonStatus { Status = "awaiting-login", Message = "Login failed: Credentials rejected." });
        Assert.Null(session.PendingLoginChallenge);

        var second = await daemon.StartLoginAsync(session.Id);

        Assert.Equal(firstAttempt + 1, Assert.IsType<CredentialChallenge>(second).LoginAttempt);
        Assert.Equal(2, client.StartLoginCallCount);
    }

    /// <summary>
    /// A start while the previous sign-in still runs with its prompt answered ends that sign-in first, so the
    /// daemon's late refusal (it names no attempt) ends the sign-in it belongs to and never the new one.
    /// </summary>
    [Fact]
    public async Task ANewStartEndsTheUnfinishedSignInBeforeItTakesItsAttempt()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        var client = (ScriptedLoginDaemonClient)session.Client;
        client.OnCredentialChallenge += _ =>
        {
            session.AuthState = DaemonAuthState.UsernameRequired;
            return Task.CompletedTask;
        };

        await daemon.StartLoginAsync(session.Id);
        var firstAttempt = session.LoginAttempt;
        var firstRunId = Assert.IsType<Guid>(session.LoginOperationId);
        // The state ProvideCredentialAsync and the daemon's acknowledgement leave: prompt consumed, sign-in still running.
        session.PendingLoginChallenge = null;
        session.AuthState = DaemonAuthState.LoggingIn;
        client.HoldCancelLogin = true;

        var start = daemon.StartLoginAsync(session.Id);
        var winner = await Task.WhenAny(client.CancelLoginEntered.Task, start).WaitAsync(TimeSpan.FromSeconds(10));
        var cancelEntered = ReferenceEquals(winner, client.CancelLoginEntered.Task);
        if (!cancelEntered)
        {
            await start.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session,
            new DaemonStatus { Status = "awaiting-login", Message = "Login failed: Credentials rejected." });
        if (cancelEntered)
        {
            client.ReleaseCancelLogin.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var firstEnding = Assert.IsType<PrefillLoginEnding>(daemon.GetLoginEnding(session, firstAttempt));
        Assert.Equal(OperationStatus.Failed, firstEnding.Status);
        Assert.Equal("prefill.auth.signInRefused", firstEnding.StageKey);
        Assert.Null(daemon.GetLoginEnding(session, firstAttempt + 1));
        Assert.NotEqual(firstRunId, session.LoginOperationId);
        Assert.Equal(1, client.CancelLoginCallCount);
    }

    /// <summary>
    /// A start while the previous sign-in's prompt is still cached resumes it and cancels nothing.
    /// </summary>
    [Fact]
    public async Task AStartWhileAPromptIsCachedResumesItWithoutACancel()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        var client = (ScriptedLoginDaemonClient)session.Client;
        client.OnCredentialChallenge += _ =>
        {
            session.AuthState = DaemonAuthState.UsernameRequired;
            return Task.CompletedTask;
        };

        await daemon.StartLoginAsync(session.Id);
        var firstAttempt = session.LoginAttempt;

        var resumed = await daemon.StartLoginAsync(session.Id);

        Assert.Equal(firstAttempt, Assert.IsType<CredentialChallenge>(resumed).LoginAttempt);
        Assert.Equal(firstAttempt, session.LoginAttempt);
        Assert.Equal(1, client.StartLoginCallCount);
        Assert.Equal(0, client.CancelLoginCallCount);
    }

    /// <summary>
    /// One guest update whose send to a browser fails drops that browser's subscription, so it cannot hold the
    /// updates and endings that follow. The browser reads how its sign-in ended when it subscribes again, the way
    /// the hub's SubscribeToSessionAsync answers it.
    /// </summary>
    [Fact]
    public async Task ADroppedBrowserReadsTheSignInEndingWhenItSubscribesAgain()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, SlowFirstSendNotificationsProxy>();
        var proxy = (SlowFirstSendNotificationsProxy)(object)notifications;
        var (daemon, session) = CreateSessionWithClient(
            tracker, Guid.NewGuid(), isPersistent: false, notifications: notifications);
        daemon.AddSubscriber(session.Id, "conn-1");

        // The first guest event goes to conn-1 and its write fails at once, as a broken socket's write does.
        proxy.HeldSend.TrySetException(new IOException("Send failed"));
        await daemon.StartLoginAsync(session.Id).WaitAsync(TimeSpan.FromSeconds(20));
        lock (session.PrefillLock)
        {
            Assert.DoesNotContain("conn-1", session.SubscribedConnections);
        }

        proxy.ClearSends();
        session.AuthState = DaemonAuthState.PasswordRequired;
        await DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session,
            new DaemonStatus { Status = "awaiting-login", Message = "Login failed: Credentials rejected." });
        Assert.DoesNotContain(proxy.Sends(), send => send.ConnectionId == "conn-1");

        daemon.AddSubscriber(session.Id, "conn-1");
        var ending = Assert.IsType<PrefillLoginEnding>(DaemonSessionDto.FromSession(session).LoginEnding);
        Assert.Equal(session.LoginAttempt, ending.LoginAttempt);
        Assert.Equal(OperationStatus.Failed, ending.Status);
        Assert.Equal("prefill.auth.signInRefused", ending.StageKey);
    }

    /// <summary>
    /// A new start ends the unfinished sign-in with the same auth-state push as any other cancel, and the push names
    /// the ended sign-in's own attempt, so the dialog that is starting reads it as the ending of what it replaced and
    /// not as its own refusal. The fake daemon copies the real one (steam-prefill-daemon SocketCommandInterface.cs:
    /// 329-351): it announces "awaiting-login" before it answers an acknowledged cancel, and a service is wired to
    /// that announcement as SessionLifecycle.cs:1109-1112 does.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ANewStartAnnouncesTheEndedSignInUnderItsOwnAttempt(bool acknowledged)
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, SlowFirstSendNotificationsProxy>();
        var proxy = (SlowFirstSendNotificationsProxy)(object)notifications;
        proxy.HeldSend.TrySetResult();
        var (daemon, session) = CreateSessionWithClient(
            tracker, Guid.NewGuid(), isPersistent: false, notifications: notifications);
        var client = (ScriptedLoginDaemonClient)session.Client;
        client.CancelAcknowledged = acknowledged;
        // A live session is wired this way at SessionLifecycle.cs:1109-1112.
        client.OnStatusUpdate += status =>
            DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session, status);
        client.OnCredentialChallenge += _ =>
        {
            session.AuthState = DaemonAuthState.UsernameRequired;
            return Task.CompletedTask;
        };
        daemon.AddSubscriber(session.Id, "conn-1");

        await daemon.StartLoginAsync(session.Id);
        var firstAttempt = session.LoginAttempt;
        // The state ProvideCredentialAsync and the daemon's acknowledgement leave: prompt consumed, sign-in still running.
        session.PendingLoginChallenge = null;
        session.AuthState = DaemonAuthState.LoggingIn;
        proxy.ClearSends();

        var second = await daemon.StartLoginAsync(session.Id);

        var endings = proxy.Sends().Where(send =>
                send.ConnectionId == "conn-1"
                && send.EventName == SignalREvents.AuthStateChanged
                && (string?)send.Payload!.GetType().GetProperty("authState")!.GetValue(send.Payload)
                    == nameof(DaemonAuthState.NotAuthenticated))
            .ToArray();
        Assert.NotEmpty(endings);
        Assert.All(endings, send =>
        {
            var attempt = Assert.IsType<long>(send.Payload!.GetType().GetProperty("loginAttempt")!.GetValue(send.Payload));
            Assert.Equal(firstAttempt, attempt);
        });
        Assert.Contains(proxy.Sends(), send =>
            send.ConnectionId == "conn-1" && send.EventName == SignalREvents.DaemonSessionUpdated);
        var firstEnding = Assert.IsType<PrefillLoginEnding>(daemon.GetLoginEnding(session, firstAttempt));
        Assert.Equal(OperationStatus.Cancelled, firstEnding.Status);
        Assert.Equal("errors.integration.attemptExpired", firstEnding.StageKey);
        Assert.Equal(1, client.CancelLoginCallCount);
        Assert.Equal(firstAttempt + 1, Assert.IsType<CredentialChallenge>(second).LoginAttempt);
    }

    /// <summary>
    /// On a shared persistent container another holder's start is refused while the first holder's sign-in runs:
    /// that sign-in is theirs to finish or cancel.
    /// </summary>
    [Fact]
    public async Task AnotherHoldersStartIsRefusedWhileTheirSignInRuns()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: true);
        var client = (ScriptedLoginDaemonClient)session.Client;
        client.OnCredentialChallenge += _ =>
        {
            session.AuthState = DaemonAuthState.UsernameRequired;
            return Task.CompletedTask;
        };
        var holderA = Guid.NewGuid();
        var holderB = Guid.NewGuid();

        await daemon.StartLoginForEditAsync(session.Id, null, () => { }, holderA);
        var firstAttempt = session.LoginAttempt;
        var firstRunId = Assert.IsType<Guid>(session.LoginOperationId);
        session.PendingLoginChallenge = null;
        session.AuthState = DaemonAuthState.LoggingIn;

        var refused = await Assert.ThrowsAsync<ConflictException>(
            () => daemon.StartLoginForEditAsync(session.Id, null, () => { }, holderB));

        Assert.Equal("errors.prefill.loginInProgress", refused.StageKey);
        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal(firstRunId, session.LoginOperationId);
        Assert.Null(daemon.GetLoginEnding(session, firstAttempt));
    }

    /// <summary>
    /// The holder who started the unfinished sign-in ends it themselves with their next start.
    /// </summary>
    [Fact]
    public async Task TheSameHoldersStartEndsTheirOwnUnfinishedSignIn()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: true);
        var client = (ScriptedLoginDaemonClient)session.Client;
        client.OnCredentialChallenge += _ =>
        {
            session.AuthState = DaemonAuthState.UsernameRequired;
            return Task.CompletedTask;
        };
        var holder = Guid.NewGuid();

        await daemon.StartLoginForEditAsync(session.Id, null, () => { }, holder);
        var firstRunId = Assert.IsType<Guid>(session.LoginOperationId);
        session.PendingLoginChallenge = null;
        session.AuthState = DaemonAuthState.LoggingIn;

        await daemon.StartLoginForEditAsync(session.Id, null, () => { }, holder);

        Assert.Equal(1, client.CancelLoginCallCount);
        Assert.NotEqual(firstRunId, session.LoginOperationId);
    }

    /// <summary>
    /// A force-stopped sign-in run is reaped from the tracker after a short wait (what OperationCancellationService.cs:129
    /// and the tracker's reap do), and the session still names it. Its owner's next start finds no run and ends the
    /// unfinished sign-in; only a run that is found with another owner is refused.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheOwnersStartAfterAForceStopEndsTheirUnfinishedSignIn(bool isPersistent)
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var holder = Guid.NewGuid();
        var (daemon, session) = CreateSessionWithClient(tracker, holder, isPersistent);
        var client = (ScriptedLoginDaemonClient)session.Client;
        // A live session is wired this way at SessionLifecycle.cs:1109-1112.
        client.OnStatusUpdate += status =>
            DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session, status);
        client.OnCredentialChallenge += _ =>
        {
            session.AuthState = DaemonAuthState.UsernameRequired;
            return Task.CompletedTask;
        };

        async Task StartAsync() =>
            _ = isPersistent
                ? await daemon.StartLoginForEditAsync(session.Id, null, () => { }, holder)
                : await daemon.StartLoginAsync(session.Id);

        await StartAsync();
        var firstRunId = Assert.IsType<Guid>(session.LoginOperationId);
        session.PendingLoginChallenge = null;
        session.AuthState = DaemonAuthState.LoggingIn;
        tracker.CompleteOperation(firstRunId, success: false, error: "Force killed by user", cancelled: true);
        OperationWaitingBlockerTests.Reap(tracker, firstRunId);
        Assert.Null(tracker.GetOperation(firstRunId));
        Assert.Equal(firstRunId, session.LoginOperationId);

        await StartAsync();

        Assert.Equal(1, client.CancelLoginCallCount);
        Assert.NotEqual(firstRunId, session.LoginOperationId);
    }

    /// <summary>
    /// A subscribe that arrives while a send to the same connection is waiting keeps the connection when that send
    /// fails: the tab subscribed again after the send's snapshot, so it is live.
    /// </summary>
    [Fact]
    public async Task AResubscribeDuringAStalledSendKeepsTheConnection()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, SlowFirstSendNotificationsProxy>();
        var proxy = (SlowFirstSendNotificationsProxy)(object)notifications;
        var (daemon, session) = CreateSessionWithClient(
            tracker, Guid.NewGuid(), isPersistent: false, notifications: notifications);
        var client = (ScriptedLoginDaemonClient)session.Client;
        // A live session is wired this way at SessionLifecycle.cs:1109-1112.
        client.OnStatusUpdate += status =>
            DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session, status);
        daemon.AddSubscriber(session.Id, "conn-1");

        var start = daemon.StartLoginAsync(session.Id);
        await proxy.FirstSendTaken.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // What the hub's SubscribeToSessionAsync does when the tab comes back.
        daemon.AddSubscriber(session.Id, "conn-1");
        proxy.HeldSend.TrySetException(new IOException("Send failed"));
        await start.WaitAsync(TimeSpan.FromSeconds(20));

        lock (session.PrefillLock)
        {
            Assert.Contains("conn-1", session.SubscribedConnections);
        }
    }

    /// <summary>
    /// A failed send drops its connection even when another tab subscribed while the send waited: only a subscribe
    /// by the stalled connection itself shows its tab is live, so the next broadcast does not wait on it again. The
    /// second subscribe is what the hub's SubscribeToSessionAsync does for another tab.
    /// </summary>
    [Fact]
    public async Task AFailedSendDropsItsConnectionWhenAnotherTabSubscribesMeanwhile()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, SlowFirstSendNotificationsProxy>();
        var proxy = (SlowFirstSendNotificationsProxy)(object)notifications;
        var (daemon, session) = CreateSessionWithClient(
            tracker, Guid.NewGuid(), isPersistent: false, notifications: notifications);
        var client = (ScriptedLoginDaemonClient)session.Client;
        // A live session is wired this way at SessionLifecycle.cs:1109-1112.
        client.OnStatusUpdate += status =>
            DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session, status);
        daemon.AddSubscriber(session.Id, "conn-1");

        var start = daemon.StartLoginAsync(session.Id);
        await proxy.FirstSendTaken.Task.WaitAsync(TimeSpan.FromSeconds(10));
        daemon.AddSubscriber(session.Id, "conn-2");
        proxy.HeldSend.TrySetException(new IOException("Send failed"));
        await start.WaitAsync(TimeSpan.FromSeconds(20));

        lock (session.PrefillLock)
        {
            Assert.DoesNotContain("conn-1", session.SubscribedConnections);
            Assert.Contains("conn-2", session.SubscribedConnections);
        }
    }

    /// <summary>
    /// A connection that subscribes again while the session already holds its limit keeps every live tab: only a new
    /// connection makes room by removing the oldest. A tab coming back into view subscribes again this way
    /// (usePrefillSignalR.ts resubscribes on visibility, and the hub's SubscribeToSessionAsync adds it).
    /// </summary>
    [Fact]
    public void AResubscribeAtTheConnectionLimitKeepsEveryLiveTab()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);

        daemon.AddSubscriber(session.Id, "conn-1");
        daemon.AddSubscriber(session.Id, "conn-2");
        daemon.AddSubscriber(session.Id, "conn-3");
        daemon.AddSubscriber(session.Id, "conn-3");

        lock (session.PrefillLock)
        {
            Assert.Contains("conn-1", session.SubscribedConnections);
            Assert.Contains("conn-2", session.SubscribedConnections);
            Assert.Contains("conn-3", session.SubscribedConnections);
        }
    }

    /// <summary>
    /// The abandoned sign-in sweep cancels the attempt it found overdue, so a restart that lands after the sweep's
    /// check and before its cancel takes the lock is left running. The logger callback stands in for the operating
    /// system pausing the sweep between its check and the cancel: it does what a start does (Login.cs moves the
    /// deadline, then the attempt, then the auth state) at the sweep's last log line before the cancel.
    /// </summary>
    [Fact]
    public async Task TheAbandonedSignInSweepLeavesASignInStartedAfterItsCheckAsync()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var logger = new CapturingLogger<SteamDaemonService>();
        var (daemon, session) = CreateSessionWithClient(
            tracker, Guid.NewGuid(), isPersistent: false, logger: logger);
        var client = (ScriptedLoginDaemonClient)session.Client;
        // A live session is wired this way at SessionLifecycle.cs:1109-1112.
        client.OnStatusUpdate += status =>
            DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session, status);
        await daemon.StartLoginAsync(session.Id);
        session.PendingLoginChallenge = null;
        session.AuthState = DaemonAuthState.UsernameRequired;
        var restarted = false;
        logger.OnLogged = entry =>
        {
            if (restarted || !entry.Message.Contains("went unanswered past its deadline")) return;
            restarted = true;
            lock (session.PrefillLock)
            {
                session.LoginExpiresAtUtc = DateTime.UtcNow.AddMinutes(15);
                session.LoginAttempt++;
                session.AuthState = DaemonAuthState.LoggingIn;
            }
        };

        var result = await daemon.ProcessSessionExpiryAsync(DateTime.UtcNow.AddDays(1));

        Assert.True(restarted);
        Assert.Equal(0, result.AbandonedLoginsCancelled);
        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal(DaemonAuthState.LoggingIn, session.AuthState);
    }

    /// <summary>
    /// The subscriber set is changed by the broadcast's catch under the session lock, so a subscribe or an unsubscribe
    /// takes that lock too. The wait only gives an unlocked change time to finish; a locked one cannot, whatever the
    /// wait.
    /// </summary>
    [Fact]
    public async Task SubscribersChangeOnlyUnderTheSessionLockAsync()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);

        await AssertWaitsForTheSessionLockAsync(session, () => daemon.AddSubscriber(session.Id, "conn-2"));
        lock (session.PrefillLock) Assert.Contains("conn-2", session.SubscribedConnections);

        await AssertWaitsForTheSessionLockAsync(session, () => daemon.RemoveSubscriber("conn-2"));
        lock (session.PrefillLock) Assert.DoesNotContain("conn-2", session.SubscribedConnections);
    }

    /// <summary>
    /// Holds the session lock on a thread of its own, starts the change, and checks it has not finished within a second
    /// of starting while the lock is held.
    /// </summary>
    private static async Task AssertWaitsForTheSessionLockAsync(DaemonSession session, Action change)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (session.PrefillLock)
            {
                entered.Set();
                release.Wait();
            }
        });
        entered.Wait();
        using var started = new ManualResetEventSlim();
        var run = Task.Run(() =>
        {
            started.Set();
            change();
        });
        // Waits for the change to start, so a thread pool slow to run it cannot hide an unlocked change. Once started,
        // an unlocked change finishes in microseconds; a locked one cannot finish while the lock is held.
        started.Wait();
        var finishedWhileLocked = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(1))) == run;
        release.Set();
        await holder;
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(finishedWhileLocked);
    }

    /// <summary>
    /// A cancel that names an older attempt, which is what the hub passes through from a stale dialog's timer, leaves
    /// the newer sign-in running (Login.cs refuses an older attempt before it reaches the daemon).
    /// </summary>
    [Fact]
    public async Task AStaleAttemptsCancelLeavesTheNewerSignInRunning()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var (daemon, session) = CreateSessionWithClient(tracker, Guid.NewGuid(), isPersistent: false);
        var client = (ScriptedLoginDaemonClient)session.Client;
        // A live session is wired this way at SessionLifecycle.cs:1109-1112.
        client.OnStatusUpdate += status =>
            DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session, status);
        client.OnCredentialChallenge += _ =>
        {
            session.AuthState = DaemonAuthState.UsernameRequired;
            return Task.CompletedTask;
        };

        await daemon.StartLoginAsync(session.Id);
        var firstAttempt = session.LoginAttempt;
        session.PendingLoginChallenge = null;
        session.AuthState = DaemonAuthState.LoggingIn;
        await daemon.StartLoginAsync(session.Id);
        var secondRunId = Assert.IsType<Guid>(session.LoginOperationId);
        var cancelCallsBefore = client.CancelLoginCallCount;

        Assert.False(await daemon.CancelLoginAsync(session.Id, CancellationToken.None, loginAttempt: firstAttempt));

        Assert.Equal(secondRunId, session.LoginOperationId);
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(secondRunId)!.Status);
        Assert.Equal(cancelCallsBefore, client.CancelLoginCallCount);
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
        IUnifiedOperationTracker tracker, Guid userId, bool isPersistent, bool isTemporary = false,
        ISignalRNotificationService? notifications = null, ILogger<SteamDaemonService>? logger = null)
    {
        var contexts = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"prefill_login_{Guid.NewGuid():N}")
            .Options);
        var daemon = new TestableSteamDaemonService(
            logger ?? NullLogger<SteamDaemonService>.Instance,
            notifications ?? DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
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

    /// <summary>
    /// Records every per-connection send, and answers the first one with a task the test completes or faults.
    /// </summary>
    private class SlowFirstSendNotificationsProxy : NullReturningProxy
    {
        private readonly object _gate = new();
        private readonly List<(string ConnectionId, string EventName, object? Payload)> _sends = new();
        private bool _firstSendTaken;

        public TaskCompletionSource HeldSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstSendTaken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ClearSends()
        {
            lock (_gate) _sends.Clear();
        }

        public (string ConnectionId, string EventName, object? Payload)[] Sends()
        {
            lock (_gate) return _sends.ToArray();
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISignalRNotificationService.SendToPrefillClientRawAsync) && args is { Length: >= 2 })
            {
                lock (_gate)
                {
                    _sends.Add(((string)args[0]!, (string)args[1]!, args.Length >= 3 ? args[2] : null));
                    if (!_firstSendTaken)
                    {
                        _firstSendTaken = true;
                        FirstSendTaken.TrySetResult();
                        return HeldSend.Task;
                    }
                }
            }

            return base.Invoke(targetMethod, args);
        }
    }
}
