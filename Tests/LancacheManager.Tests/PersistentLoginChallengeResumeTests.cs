using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// Proves the manager-side pending-login-challenge cache
/// on <see cref="DaemonSession.PendingLoginChallenge"/> that fixes the double-start bug from
/// diagnostic.md §5 - a second <c>StartLoginAsync</c> call arriving while a challenge is still pending
/// (e.g. the frontend closed/reopened the login modal) now resumes the SAME challenge instead of
/// issuing a second daemon <c>login</c> command, which the daemon answers "already in progress" without
/// re-emitting a challenge while the client destroys its own queued copy - the exact "No challenge
/// received" / 400 chain the diagnostic reproduced.
///
/// PRE-FIX BEHAVIOR (verified by temporarily reverting the resume check in
/// <c>PrefillDaemonServiceBase.StartLoginCoreAsync</c> and re-running
/// <see cref="StartLoginAsync_SecondCallWhilePending_ResumesSameChallengeWithoutDaemonCall"/>): the
/// second call falls through to the normal flow and invokes the daemon client's
/// <c>StartLoginAsync</c> a second time, which this test's fake client treats as a protocol violation
/// (throws) - mirroring the daemon's real "already in progress" reply racing against the client's own
/// queue-clearing.
/// </summary>
public class PersistentLoginChallengeResumeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartLoginAsync_CancelAfterChallengeLog_DoesNotRestoreChallenge(bool queued)
    {
        using var logger = new HeldChallengeLogger(queued);
        var client = queued ? (IDaemonClient)new QueuedChallengeDaemonClient() : new SingleChallengeDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client, logger);

        var login = Task.Run(() => daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None));
        try
        {
            await logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await daemon.CancelLoginAsync(session.Id, CancellationToken.None, session.LoginAttempt));
        }
        finally
        {
            logger.Release.Set();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await login.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(session.PendingLoginChallenge);
    }

    [Fact]
    public async Task OnCredentialChallenge_AfterConfirmedCancel_DoesNotChangeLoginState()
    {
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: LoginChallenge());
        var notifications = DispatchProxy.Create<ISignalRNotificationService, HeldNotificationProxy>();
        var (daemon, session) = CreateSessionWithClient(client, notifications: notifications);
        await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(await daemon.CancelLoginAsync(session.Id, CancellationToken.None, session.LoginAttempt));
        var authState = session.AuthState;
        var settled = session.LoginSettled;
        var expires = session.LoginExpiresAtUtc;

        await ((TestableSteamDaemonService)daemon).InvokeOnCredentialChallengeAsync(session,
            new CredentialChallenge
            {
                ChallengeId = "late-challenge",
                CredentialType = "password",
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });

        Assert.Null(session.PendingLoginChallenge);
        Assert.Equal(authState, session.AuthState);
        Assert.Equal(settled, session.LoginSettled);
        Assert.Equal(expires, session.LoginExpiresAtUtc);
        Assert.Equal(0, ((HeldNotificationProxy)notifications).ChallengeAdminCalls);
    }

    [Fact]
    public async Task OnCredentialChallenge_DuringFailedCancel_ResumesFollowOnChallenge()
    {
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: LoginChallenge())
        {
            HoldCancelLogin = true,
            CancelAcknowledged = false
        };
        var (daemon, session) = CreateSessionWithClient(client);
        await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        var cancel = daemon.CancelLoginAsync(session.Id, CancellationToken.None, session.LoginAttempt);
        await client.CancelLoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var authState = session.AuthState;
        var settled = session.LoginSettled;

        await ((TestableSteamDaemonService)daemon).InvokeOnCredentialChallengeAsync(session,
            new CredentialChallenge { ChallengeId = "during-cancel", CredentialType = "password" });
        Assert.Null(session.PendingLoginChallenge);
        Assert.Equal(authState, session.AuthState);
        Assert.Equal(settled, session.LoginSettled);

        client.ReleaseCancelLogin.TrySetResult();
        await Assert.ThrowsAsync<LancacheManager.Middleware.ConflictException>(
            async () => await cancel.WaitAsync(TimeSpan.FromSeconds(5)));
        var followOn = new CredentialChallenge { ChallengeId = "after-failed-cancel", CredentialType = "password" };
        await ((TestableSteamDaemonService)daemon).InvokeOnCredentialChallengeAsync(session, followOn);
        Assert.Same(followOn, session.PendingLoginChallenge);
        Assert.Equal(session.LoginAttempt, followOn.LoginAttempt);
    }

    [Fact]
    public async Task OnCredentialChallenge_NewAttemptRejectsStampedOldChallenge()
    {
        var (daemon, session) = CreateSessionWithClient(new MultiCallChallengeDaemonClient());
        await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        var oldAttempt = session.LoginAttempt;
        Assert.True(await daemon.CancelLoginAsync(session.Id, CancellationToken.None, oldAttempt));
        await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        var current = new CredentialChallenge { ChallengeId = "current", CredentialType = "password" };
        await ((TestableSteamDaemonService)daemon).InvokeOnCredentialChallengeAsync(session, current);
        Assert.Same(current, session.PendingLoginChallenge);
        Assert.Equal(session.LoginAttempt, current.LoginAttempt);

        await ((TestableSteamDaemonService)daemon).InvokeOnCredentialChallengeAsync(session,
            new CredentialChallenge { ChallengeId = "old", CredentialType = "username", LoginAttempt = oldAttempt });
        Assert.Same(current, session.PendingLoginChallenge);
        Assert.Equal(DaemonAuthState.PasswordRequired, session.AuthState);
    }

    [Fact]
    public async Task OnCredentialChallenge_CancelDuringSubscriberSend_SkipsAdminMirror()
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, HeldNotificationProxy>();
        var proxy = (HeldNotificationProxy)notifications;
        var (daemon, session) = CreateSessionWithClient(new SingleChallengeDaemonClient(), notifications: notifications);
        await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        session.SubscribedConnections.Add("test-connection");

        var challenge = new CredentialChallenge { ChallengeId = "fanout", CredentialType = "password" };
        var delivery = ((TestableSteamDaemonService)daemon).InvokeOnCredentialChallengeAsync(session, challenge);
        await proxy.SendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await daemon.CancelLoginAsync(session.Id, CancellationToken.None, session.LoginAttempt));
        proxy.ReleaseSend.TrySetResult();
        await delivery.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, proxy.ChallengeAdminCalls);
        Assert.Null(session.PendingLoginChallenge);
    }

    [Fact]
    public async Task OnCredentialChallenge_CurrentChallenge_ReachesSubscriberAndAdmin()
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, HeldNotificationProxy>();
        var proxy = (HeldNotificationProxy)notifications;
        var (daemon, session) = CreateSessionWithClient(new SingleChallengeDaemonClient(), notifications: notifications);
        await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        session.SubscribedConnections.Add("test-connection");
        proxy.ReleaseSend.TrySetResult();

        var challenge = new CredentialChallenge { ChallengeId = "current", CredentialType = "password" };
        await ((TestableSteamDaemonService)daemon).InvokeOnCredentialChallengeAsync(session, challenge);

        Assert.Equal(1, proxy.ChallengeSubscriberCalls);
        Assert.Equal(1, proxy.ChallengeAdminCalls);
        Assert.Same(challenge, session.PendingLoginChallenge);
    }

    [Fact]
    public async Task StartLoginAsync_SecondCallWhilePending_ResumesSameChallengeWithoutDaemonCall()
    {
        var client = new SingleChallengeDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var first = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(SingleChallengeDaemonClient.Challenge.ChallengeId, first!.ChallengeId);
        Assert.Equal(1, client.StartLoginCallCount);

        var second = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal(SingleChallengeDaemonClient.Challenge.ChallengeId, second!.ChallengeId);
        Assert.Equal(1, client.StartLoginCallCount);
    }

    [Fact]
    public async Task CancelLoginAsync_ClearsPendingChallenge_NextStartIssuesFreshDaemonLogin()
    {
        var client = new MultiCallChallengeDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var first = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal("chal-1", first!.ChallengeId);
        Assert.NotNull(session.PendingLoginChallenge);

        await daemon.CancelLoginAsync(session.Id, CancellationToken.None);

        Assert.Null(session.PendingLoginChallenge);

        var second = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal("chal-2", second!.ChallengeId);
        Assert.Equal(2, client.StartLoginCallCount);
    }

    /// <summary>
    /// A failed daemon-side cancel round-trip
    /// (socket error, unresponsive daemon) must NOT be treated as a successful cancel. Before this fix,
    /// <c>CancelLoginAsync</c> cleared <see cref="DaemonSession.PendingLoginChallenge"/> and flipped
    /// <see cref="DaemonSession.AuthState"/> to <see cref="DaemonAuthState.NotAuthenticated"/>
    /// unconditionally, even when the try/catch around the daemon round-trip caught an exception - so
    /// the next <c>StartLoginAsync</c> would issue a brand-new daemon login while the daemon might still
    /// believe the original attempt is in progress, reproducing the duplicate-login race the resume
    /// cache exists to prevent. The fix restores the captured challenge and rethrows on failure instead.
    /// </summary>
    [Fact]
    public async Task CancelLoginAsync_DaemonCancelFails_ChallengeStaysResumable_NextStartDoesNotIssueFreshDaemonLogin()
    {
        var client = new ThrowingCancelDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var first = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal("chal-1", first!.ChallengeId);
        Assert.NotNull(session.PendingLoginChallenge);
        var authStateBeforeCancel = session.AuthState;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => daemon.CancelLoginAsync(session.Id, CancellationToken.None));

        // Failure must not be swallowed into a successful cancel: the cached challenge stays in place
        // and auth state is left untouched (not reset as if the cancel had actually gone through).
        Assert.NotNull(session.PendingLoginChallenge);
        Assert.Equal("chal-1", session.PendingLoginChallenge!.ChallengeId);
        Assert.Equal(authStateBeforeCancel, session.AuthState);

        var second = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        // Resumed the SAME cached challenge - no second daemon login command was issued.
        Assert.Equal("chal-1", second!.ChallengeId);
        Assert.Equal(1, client.StartLoginCallCount);
    }

    /// <summary>
    /// The production client answers false both when the daemon refuses cancel-login and when the command
    /// times out or never reaches it (<c>DaemonClientBase.CancelLoginWithOutcomeAsync</c>).
    /// </summary>
    [Fact]
    public async Task CancelLoginAsync_DaemonDoesNotAcknowledgeCancel_ThrowsTypedConflictAndKeepsChallengeResumable()
    {
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: LoginChallenge()) { CancelAcknowledged = false };
        var (daemon, session) = CreateSessionWithClient(client);

        var first = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        var authStateBeforeCancel = session.AuthState;

        var error = await Assert.ThrowsAsync<LancacheManager.Middleware.ConflictException>(
            () => daemon.CancelLoginAsync(session.Id, CancellationToken.None, first!.LoginAttempt));

        Assert.Equal("prefill.persistent.loginNotCanceled", error.StageKey);
        Assert.Equal("chal-1", session.PendingLoginChallenge?.ChallengeId);
        Assert.Equal(authStateBeforeCancel, session.AuthState);
    }

    [Fact]
    public async Task CancelLoginAsync_UnacknowledgedCancelKeepsSettledSessionReady()
    {
        var client = new ScriptedLoginDaemonClient { CancelAcknowledged = false };
        var (daemon, session) = CreateSessionWithClient(client);

        Assert.True(session.LoginSettled);
        await Assert.ThrowsAsync<LancacheManager.Middleware.ConflictException>(
            () => daemon.CancelLoginAsync(session.Id, CancellationToken.None));

        Assert.True(session.LoginSettled);
        Assert.False(session.LoginCanceling);
    }

    [Fact]
    public async Task CancelLoginAsync_ForOlderAttempt_ChangesNothing()
    {
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: LoginChallenge());
        var (daemon, session) = CreateSessionWithClient(client);

        var first = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(1, first!.LoginAttempt);
        var authStateBeforeCancel = session.AuthState;

        Assert.False(await daemon.CancelLoginAsync(session.Id, CancellationToken.None, first.LoginAttempt - 1));

        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal("chal-1", session.PendingLoginChallenge?.ChallengeId);
        Assert.Equal(authStateBeforeCancel, session.AuthState);
    }

    [Fact]
    public async Task CancelLoginAsync_BlocksNewLoginUntilCancelSettles()
    {
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: LoginChallenge()) { HoldCancelLogin = true };
        var (daemon, session) = CreateSessionWithClient(client);

        var first = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        var cancel = daemon.CancelLoginAsync(session.Id, CancellationToken.None, first!.LoginAttempt);
        await client.CancelLoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<LancacheManager.Middleware.ConflictException>(
            () => daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.Equal("errors.prefill.loginInProgress", error.StageKey);
        Assert.Equal(1, client.StartLoginCallCount);

        client.ReleaseCancelLogin.SetResult();
        Assert.True(await cancel.WaitAsync(TimeSpan.FromSeconds(5)));

        var second = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(2, client.StartLoginCallCount);
        Assert.Equal(2, second!.LoginAttempt);
        Assert.NotNull(session.PendingLoginChallenge);
        Assert.NotEqual(DaemonAuthState.NotAuthenticated, session.AuthState);
    }

    [Fact]
    public async Task CancelLoginAsync_ConcurrentCallSharesDaemonRefusal()
    {
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: LoginChallenge())
        {
            HoldCancelLogin = true,
            CancelAcknowledged = false
        };
        var (daemon, session) = CreateSessionWithClient(client);
        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        var first = daemon.CancelLoginAsync(session.Id, CancellationToken.None, challenge!.LoginAttempt);
        await client.CancelLoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = daemon.CancelLoginAsync(session.Id, CancellationToken.None, challenge.LoginAttempt);
        Assert.False(second.IsCompleted);

        client.ReleaseCancelLogin.SetResult();
        var firstError = await Assert.ThrowsAsync<LancacheManager.Middleware.ConflictException>(() => first);
        var secondError = await Assert.ThrowsAsync<LancacheManager.Middleware.ConflictException>(() => second);
        Assert.Same(firstError, secondError);
        Assert.Equal(1, client.CancelLoginCallCount);
    }

    [Fact]
    public async Task CancelLoginAsync_ConcurrentCallSharesDaemonSuccess()
    {
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: LoginChallenge())
        {
            HoldCancelLogin = true
        };
        var (daemon, session) = CreateSessionWithClient(client);
        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        var first = daemon.CancelLoginAsync(session.Id, CancellationToken.None, challenge!.LoginAttempt);
        await client.CancelLoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = daemon.CancelLoginAsync(session.Id, CancellationToken.None, challenge.LoginAttempt);
        Assert.False(second.IsCompleted);
        client.ReleaseCancelLogin.SetResult();

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, client.CancelLoginCallCount);
        Assert.True(await daemon.CancelLoginAsync(session.Id, CancellationToken.None, challenge.LoginAttempt));
        Assert.Equal(1, client.CancelLoginCallCount);
    }

    [Fact]
    public async Task CancelLoginAsync_JoinerCancellationDoesNotCancelOwner()
    {
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: LoginChallenge())
        {
            HoldCancelLogin = true
        };
        var (daemon, session) = CreateSessionWithClient(client);
        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        var first = daemon.CancelLoginAsync(session.Id, CancellationToken.None, challenge!.LoginAttempt);
        await client.CancelLoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var waiting = new CancellationTokenSource();
        var second = daemon.CancelLoginAsync(session.Id, waiting.Token, challenge.LoginAttempt);
        await waiting.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(first.IsCompleted);
        client.ReleaseCancelLogin.SetResult();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, client.CancelLoginCallCount);
    }

    [Fact]
    public async Task CancelLoginAsync_ConcurrentCallSharesDaemonException()
    {
        var client = new ThrowingCancelDaemonClient { HoldCancelLogin = true };
        var (daemon, session) = CreateSessionWithClient(client);
        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        var first = daemon.CancelLoginAsync(session.Id, CancellationToken.None, challenge!.LoginAttempt);
        await client.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = daemon.CancelLoginAsync(session.Id, CancellationToken.None, challenge.LoginAttempt);
        client.ReleaseCancel.SetResult();
        var firstError = await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        var secondError = await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Same(firstError, secondError);
        Assert.Equal(1, client.CancelLoginCallCount);
    }

    [Fact]
    public async Task CancelLoginAsync_UnacknowledgedOlderCancelDoesNotRestoreChallenge()
    {
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: LoginChallenge())
        {
            HoldCancelLogin = true,
            CancelAcknowledged = false
        };
        var (daemon, session) = CreateSessionWithClient(client);

        var first = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        var cancel = daemon.CancelLoginAsync(session.Id, CancellationToken.None, first!.LoginAttempt);
        await client.CancelLoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<LancacheManager.Middleware.ConflictException>(
            () => daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.Null(session.PendingLoginChallenge);

        client.ReleaseCancelLogin.SetResult();
        var error = await Assert.ThrowsAsync<LancacheManager.Middleware.ConflictException>(
            () => cancel.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("prefill.persistent.loginNotCanceled", error.StageKey);
        Assert.Equal(first.ChallengeId, session.PendingLoginChallenge?.ChallengeId);

        var resumed = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(first.ChallengeId, resumed?.ChallengeId);
        Assert.Equal(1, client.StartLoginCallCount);
    }

    [Fact]
    public async Task CancelLoginAsync_UnacknowledgedCancelKeepsFollowOnChallenge()
    {
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: LoginChallenge())
        {
            HoldCancelLogin = true,
            CancelAcknowledged = false
        };
        var (daemon, session) = CreateSessionWithClient(client);

        var first = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        var cancel = daemon.CancelLoginAsync(session.Id, CancellationToken.None, first!.LoginAttempt);
        await client.CancelLoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var followOn = LoginChallenge();
        followOn.ChallengeId = "chal-2";
        session.PendingLoginChallenge = followOn;
        client.ReleaseCancelLogin.SetResult();

        await Assert.ThrowsAsync<LancacheManager.Middleware.ConflictException>(
            () => cancel.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(followOn, session.PendingLoginChallenge);
    }

    private static CredentialChallenge LoginChallenge() => new()
    {
        ChallengeId = "chal-1",
        CredentialType = "username",
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddMinutes(5)
    };

    [Fact]
    public async Task NotifyAuthStateChangeAsync_TransitionToAuthenticated_ClearsPendingChallenge()
    {
        var client = new SingleChallengeDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(challenge);
        Assert.NotNull(session.PendingLoginChallenge);

        session.AuthState = DaemonAuthState.Authenticated;
        await ((TestableSteamDaemonService)daemon).InvokeNotifyAuthStateChangeAsync(session);

        Assert.Null(session.PendingLoginChallenge);
    }

    [Fact]
    public async Task FailLoginFastAsync_ClearsPendingChallenge()
    {
        var client = new SingleChallengeDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(challenge);
        Assert.NotNull(session.PendingLoginChallenge);

        var result = await ((TestableSteamDaemonService)daemon)
            .InvokeFailLoginFastAsync(session, session.Id, "Login failed: some daemon error.");

        Assert.Null(result);
        Assert.Null(session.PendingLoginChallenge);
        Assert.Equal("Login failed: some daemon error.", session.LastLoginFailureMessage);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
    }

    /// <summary>
    /// Edge case: the daemon reports the session authenticated (e.g. via a live status broadcast) in
    /// the window between two StartLoginAsync calls, while a (now stale) pending challenge from before
    /// that transition is still sitting on the session. The resume check must defer to AuthState and
    /// NOT hand back the stale challenge - it must go through the "already authenticated" branch
    /// instead, which asks the daemon and returns null (confirmed authenticated).
    /// </summary>
    [Fact]
    public async Task StartLoginAsync_PendingChallengeButNowAuthenticated_DoesNotResumeStaleChallenge()
    {
        var client = new SingleChallengeDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(challenge);

        // Simulate the daemon having authenticated out-of-band (e.g. self-auth from its own volume)
        // without going through the normal clearing funnel, leaving a stale challenge in place on
        // purpose so the resume guard itself (not the clearing logic) is what's under test here.
        session.AuthState = DaemonAuthState.Authenticated;
        client.NextStartLoginReturnsNull = true;

        var result = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(2, client.StartLoginCallCount);
    }

    [Fact]
    public async Task WaitForChallengeAsync_ServesCachedChallengeImmediately_WithoutCallingClient()
    {
        var client = new SingleChallengeDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(challenge);

        // WaitForChallengeAsync on the fake client throws NotSupportedException if invoked (it isn't
        // overridden by SingleChallengeDaemonClient), so reaching a non-throwing result proves the
        // cached challenge was served instead of falling through to the daemon.
        var polled = await daemon.WaitForChallengeAsync(session.Id, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(polled);
        Assert.Equal(SingleChallengeDaemonClient.Challenge.ChallengeId, polled!.ChallengeId);
    }

    /// <summary>
    /// A multi-step login
    /// (username -> password) must never resume the STALE first-step challenge after the caller has
    /// already submitted credentials for it. Before this fix, <c>ProvideCredentialAsync</c> never
    /// cleared <see cref="DaemonSession.PendingLoginChallenge"/> and the follow-on challenge dispatch
    /// (<c>OnCredentialChallengeAsync</c>) never updated it either, so a GET-challenge poll (or the
    /// reopen reconcile) kept returning the already-answered username challenge forever.
    /// </summary>
    [Fact]
    public async Task ProvideCredentialAsync_ClearsCache_FollowOnChallengeReplacesIt_NeverServesStaleChallenge()
    {
        var client = new MultiStepLoginDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);
        var testableDaemon = (TestableSteamDaemonService)daemon;
        client.AttachDaemon(testableDaemon, session);

        var usernameChallenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal("chal-username", usernameChallenge!.ChallengeId);
        Assert.Same(usernameChallenge, session.PendingLoginChallenge);

        await daemon.ProvideCredentialAsync(session.Id, usernameChallenge, "user1", CancellationToken.None);

        // The follow-on (password) challenge must be what a poll/GET now serves - never the
        // already-answered username challenge that was cached before submission.
        var polled = await daemon.WaitForChallengeAsync(session.Id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(polled);
        Assert.Equal("chal-password", polled!.ChallengeId);
        Assert.NotEqual(usernameChallenge.ChallengeId, polled.ChallengeId);
        Assert.Same(polled, session.PendingLoginChallenge);
    }

    /// <summary>
    /// Drives the full Steam-shaped multi-step
    /// flow (username -> password -> 2FA -> authenticated) purely through the REST-facing service
    /// methods (<c>StartLoginAsync</c>/<c>ProvideCredentialAsync</c>/<c>WaitForChallengeAsync</c>) with
    /// no SignalR hub assertions at all, proving the flow completes correctly on REST alone (i.e. it
    /// does not depend on the SignalR push mirror to advance) and that no step ever observes a stale
    /// challenge from an earlier step.
    /// </summary>
    [Fact]
    public async Task MultiStepLogin_CompletesViaRestAlone_NeverServesStaleChallengeAtAnyStep()
    {
        var client = new MultiStepLoginDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);
        var testableDaemon = (TestableSteamDaemonService)daemon;
        client.AttachDaemon(testableDaemon, session);

        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal("username", challenge!.CredentialType);

        await daemon.ProvideCredentialAsync(session.Id, challenge, "user1", CancellationToken.None);
        challenge = await daemon.WaitForChallengeAsync(session.Id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("password", challenge!.CredentialType);

        await daemon.ProvideCredentialAsync(session.Id, challenge, "pass1", CancellationToken.None);
        challenge = await daemon.WaitForChallengeAsync(session.Id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("2fa", challenge!.CredentialType);

        await daemon.ProvideCredentialAsync(session.Id, challenge, "123456", CancellationToken.None);

        // Last step consumed with no further challenge queued: the cache must be empty (not the
        // stale 2fa challenge), matching the daemon having moved on to logged-in.
        Assert.Null(session.PendingLoginChallenge);
        Assert.True(client.LoggedIn);

        // The daemon's own "logged-in" status broadcast flips AuthState and clears the cache via the
        // existing auth-success funnel (proven independently by
        // NotifyAuthStateChangeAsync_TransitionToAuthenticated_ClearsPendingChallenge above) -
        // simulated here to close the loop end-to-end for this scenario.
        session.AuthState = DaemonAuthState.Authenticated;
        await testableDaemon.InvokeNotifyAuthStateChangeAsync(session);
        Assert.Null(session.PendingLoginChallenge);
    }

    [Fact]
    public async Task TerminateSessionAsync_ClearsPendingChallengeOnTheSessionObject()
    {
        var client = new SingleChallengeDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(challenge);
        Assert.NotNull(session.PendingLoginChallenge);

        await daemon.TerminateSessionAsync(session.Id, "test cleanup", force: true);

        Assert.Null(session.PendingLoginChallenge);
    }

    private static (PrefillDaemonServiceBase Daemon, DaemonSession Session) CreateSessionWithClient(
        IDaemonClient client, ILogger<SteamDaemonService>? logger = null,
        ISignalRNotificationService? notifications = null)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"login_challenge_resume_{Guid.NewGuid():N}")
            .Options;
        var dbFactory = new InMemoryDbContextFactory(dbOptions);
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        notifications ??= DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
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
            IsPersistent = true,
            AuthState = DaemonAuthState.NotAuthenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Client = client
        };
        daemon.InjectSession(session);

        return (daemon, session);
    }

    // Test-only seams: _sessions is `protected` and NotifyAuthStateChangeAsync/FailLoginFastAsync are
    // protected/private on PrefillDaemonServiceBase, so production code never exposes a way to inject a
    // session or drive these funnels without a real daemon round-trip. Mirrors the InjectSession seam
    // already established in PersistentLoginFailFastTests.cs; the two extra invokers here let the
    // clearing side effects at each documented clear-point (auth-success funnel, fail-fast failure) be
    // asserted directly instead of being fought around the resume short-circuit they're testing against.
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

        public Task InvokeNotifyAuthStateChangeAsync(DaemonSession session) => NotifyAuthStateChangeAsync(session);

        public Task<CredentialChallenge?> InvokeFailLoginFastAsync(DaemonSession session, string sessionId, string failureMessage)
            => FailLoginFastAsync(session, sessionId, failureMessage);

        // Test-only seam mirroring PersistentLoginChallengePushTests.cs: production wires
        // IDaemonClient.OnCredentialChallenge to this during real session creation, which
        // InjectSession bypasses. Lets MultiStepLoginDaemonClient simulate the daemon dispatching a
        // follow-on challenge exactly as the real socket event loop would.
        public Task InvokeOnCredentialChallengeAsync(DaemonSession session, CredentialChallenge challenge)
            => OnCredentialChallengeAsync(session, challenge);
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
    /// Base fake <see cref="IDaemonClient"/> exposing the same event surface as the real transports;
    /// every member outside a scenario's login-flow scope throws <see cref="NotSupportedException"/> so
    /// an unexpected call fails loudly (mirrors TestDaemonClientBase in PersistentLoginFailFastTests.cs).
    /// </summary>
    private abstract class TestDaemonClientBase : IDaemonClient
    {
        public event Func<CredentialChallenge, Task>? OnCredentialChallenge { add { } remove { } }
        public event Func<DaemonStatus, Task>? OnStatusUpdate { add { } remove { } }
        public event Func<SocketPrefillProgress, Task>? OnProgressUpdate { add { } remove { } }
        public event Func<string, Task>? OnError { add { } remove { } }
        public event Func<Task>? OnDisconnected { add { } remove { } }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Unexpected GetStatusAsync in this scenario.");

        public Task<CommandResponse> SendCommandAsync(
            string type, Dictionary<string, string>? parameters = null, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException($"Unexpected SendCommandAsync({type}) in this test.");

        public abstract Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);

        public virtual Task ProvideCredentialAsync(CredentialChallenge challenge, string credential, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CredentialChallenge?> GetAutoLoginChallengeAsync(string sessionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ProvideAutoLoginAsync(string sessionId, string username, string refreshToken, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ProvideEpicAutoLoginAsync(string sessionId, string refreshToken, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ProvideXboxAutoLoginAsync(string sessionId, string refreshToken, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public virtual Task<CredentialChallenge?> WaitForChallengeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Unexpected WaitForChallengeAsync in this scenario.");

        public virtual Task CancelLoginAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Unexpected LogoutAsync in this scenario.");

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
            List<CachedDepotInput>? cachedDepots = null, CancellationToken cancellationToken = default,
            Guid? runId = null,
            Action? onCommandDispatched = null)
            => throw new NotSupportedException();

        public Task<ClearCacheResult> ClearCacheAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ClearCacheResult> GetCacheInfoAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(List<string>? operatingSystems = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CacheStatusResult> CheckCacheStatusAsync(List<uint> appIds, List<CachedDepotInput> cachedDepots, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void ClearPendingChallenges() { }

        public void Dispose() { }
    }

    /// <summary>
    /// Scenario: returns a real challenge exactly once. A second <c>StartLoginAsync</c> call throws,
    /// mirroring the daemon's real "already in progress" reply combined with the client's own
    /// queue-clearing destroying the challenge - i.e. what happens today if a resume is NOT taken.
    /// <see cref="NextStartLoginReturnsNull"/> lets a later call simulate the daemon confirming
    /// already-authenticated (returns null) instead of throwing, for the stale-challenge edge case.
    /// </summary>
    private sealed class HeldChallengeLogger(bool queued) : ILogger<SteamDaemonService>, IDisposable
    {
        private readonly string _prefix = queued ? "Received queued challenge" : "Received challenge";
        private int _held;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information &&
                formatter(state, exception).StartsWith(_prefix, StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _held, 1) == 0)
            {
                Entered.TrySetResult();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
        }

        public void Dispose() => Release.Dispose();

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }

    private class HeldNotificationProxy : DispatchProxy
    {
        public TaskCompletionSource SendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ChallengeSubscriberCalls { get; private set; }
        public int ChallengeAdminCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISignalRNotificationService.SendToPrefillClientRawAsync) &&
                args?[1] is string eventName && eventName == "CredentialChallenge")
            {
                ChallengeSubscriberCalls++;
                SendEntered.TrySetResult();
                return ReleaseSend.Task;
            }

            if (targetMethod?.Name == nameof(ISignalRNotificationService.NotifyAdminAsync) &&
                args?[0] is string adminEvent && adminEvent == "CredentialChallenge")
            {
                ChallengeAdminCalls++;
            }

            return targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }
    }

    private sealed class QueuedChallengeDaemonClient : TestDaemonClientBase
    {
        public override Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) => Task.FromResult<CredentialChallenge?>(null);

        public override Task<CredentialChallenge?> WaitForChallengeAsync(TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) => Task.FromResult<CredentialChallenge?>(
                new CredentialChallenge { ChallengeId = "queued-challenge", CredentialType = "username" });
    }

    private sealed class SingleChallengeDaemonClient : TestDaemonClientBase
    {
        public static readonly CredentialChallenge Challenge = new()
        {
            ChallengeId = "chal-pending",
            CredentialType = "username"
        };

        public int StartLoginCallCount { get; private set; }
        public bool NextStartLoginReturnsNull { get; set; }

        public override Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            StartLoginCallCount++;
            if (StartLoginCallCount == 1)
            {
                return Task.FromResult<CredentialChallenge?>(Challenge);
            }
            if (NextStartLoginReturnsNull)
            {
                return Task.FromResult<CredentialChallenge?>(null);
            }
            throw new InvalidOperationException(
                "StartLoginAsync should not be called again while a challenge is pending (resume expected).");
        }

        public override Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<DaemonStatus?>(new DaemonStatus { Status = "logged-in" });
    }

    /// <summary>
    /// Scenario: returns a fresh, distinct challenge on every call - used to prove a NEW daemon login is
    /// issued once the pending-challenge cache has been cleared (cancel-login).
    /// </summary>
    private sealed class MultiCallChallengeDaemonClient : TestDaemonClientBase
    {
        public int StartLoginCallCount { get; private set; }

        public override Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            StartLoginCallCount++;
            return Task.FromResult<CredentialChallenge?>(new CredentialChallenge
            {
                ChallengeId = $"chal-{StartLoginCallCount}",
                CredentialType = "username"
            });
        }
    }

    /// <summary>
    /// Scenario: returns a single real challenge, but the daemon-side cancel command itself throws -
    /// simulating a socket error or unresponsive daemon during <c>CancelLoginAsync</c>'s round-trip.
    /// <see cref="MultiCallChallengeDaemonClient.StartLoginCallCount"/>-style counting distinguishes a
    /// resumed cached challenge (count stays 1) from a fresh daemon login being issued (count reaches 2).
    /// </summary>
    private sealed class ThrowingCancelDaemonClient : TestDaemonClientBase
    {
        public int StartLoginCallCount { get; private set; }
        public int CancelLoginCallCount { get; private set; }
        public bool HoldCancelLogin { get; set; }
        public TaskCompletionSource CancelEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCancel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            StartLoginCallCount++;
            return Task.FromResult<CredentialChallenge?>(new CredentialChallenge
            {
                ChallengeId = $"chal-{StartLoginCallCount}",
                CredentialType = "username"
            });
        }

        public override async Task CancelLoginAsync(CancellationToken cancellationToken = default)
        {
            CancelLoginCallCount++;
            CancelEntered.TrySetResult();
            if (HoldCancelLogin) await ReleaseCancel.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("Simulated daemon cancel-login round-trip failure.");
        }
    }

    /// <summary>
    /// Scenario: a Steam-shaped three-step login (username -> password -> 2fa -> logged-in).
    /// <see cref="AttachDaemon"/> wires this fake to the same
    /// <see cref="TestableSteamDaemonService.InvokeOnCredentialChallengeAsync"/> seam production uses
    /// (<c>daemonClient.OnCredentialChallenge += ...</c> at session-creation time), so
    /// <see cref="ProvideCredentialAsync"/> can dispatch each follow-on challenge exactly the way the
    /// real socket event loop would - proving the manager-side cache tracks whichever challenge the
    /// daemon most recently emitted, not just the first one of the attempt.
    /// </summary>
    private sealed class MultiStepLoginDaemonClient : TestDaemonClientBase
    {
        private static readonly string[] Steps = { "username", "password", "2fa" };
        private TestableSteamDaemonService? _daemon;
        private DaemonSession? _session;
        private int _stepIndex;

        public int StartLoginCallCount { get; private set; }
        public bool LoggedIn { get; private set; }

        public void AttachDaemon(TestableSteamDaemonService daemon, DaemonSession session)
        {
            _daemon = daemon;
            _session = session;
        }

        public override Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            StartLoginCallCount++;
            return Task.FromResult<CredentialChallenge?>(MakeChallenge(_stepIndex));
        }

        public override async Task ProvideCredentialAsync(CredentialChallenge challenge, string credential, CancellationToken cancellationToken = default)
        {
            _stepIndex++;
            if (_stepIndex < Steps.Length)
            {
                // Simulate the daemon's async follow-on challenge dispatch (the real
                // IDaemonClient.OnCredentialChallenge event) synchronously so the test can assert
                // immediately after ProvideCredentialAsync returns.
                await _daemon!.InvokeOnCredentialChallengeAsync(_session!, MakeChallenge(_stepIndex));
            }
            else
            {
                LoggedIn = true;
            }
        }

        public override Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<DaemonStatus?>(new DaemonStatus { Status = LoggedIn ? "logged-in" : "awaiting-login" });

        private static CredentialChallenge MakeChallenge(int stepIndex) => new()
        {
            ChallengeId = $"chal-{Steps[stepIndex]}",
            CredentialType = Steps[stepIndex]
        };
    }
}
