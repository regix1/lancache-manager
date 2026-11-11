using System.Reflection;
using System.Runtime.ExceptionServices;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    private const string EarlierSignInFailure = "Login failed: the saved token was refused.";

    // A dropped connection ends the Xbox saved-login sign-in while the daemon, which kept running, still runs it. A person's
    // typed Log in after that must not end with that earlier sign-in's late failure, in whichever order the failure push and
    // the Log in's steps come. The daemon pushes that failure only while the earlier sign-in still runs there: a cancel-login
    // ends it without one (xbox-prefill-daemon SocketCommandInterface.cs:313-317).
    [Fact]
    public async Task ATypedXboxLogInAfterADroppedSavedSignInIsNotEndedByThatSignInsLateFailureAsync()
    {
        var failures = new List<string>();
        foreach (var order in new[] { "failure before the Log in", "failure while the login is out", "failure after the challenge" })
        {
            var failure = await RunTypedLogInAfterADroppedSavedSignInAsync(order);
            if (failure is not null)
            {
                failures.Add(failure);
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private async Task<string?> RunTypedLogInAfterADroppedSavedSignInAsync(string order)
    {
        var instance = Guid.NewGuid().ToString("D");
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        var inner = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        var client = DispatchProxy.Create<IDaemonClient, XboxDaemonStillSigningIn>();
        var model = (XboxDaemonStillSigningIn)(object)client;
        model.Inner = inner;
        var thirdRead = ThirdXboxStatusReadAfterLogin(inner, instance, () => model.Dropped
            ? (true, model.Reconnected ? XboxSignedOut(instance, loggingIn: model.EarlierSignInRunning) : null)
            : (false, null));

        const string containerName = "xbox-daemon-persistent";
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(
            sessionService, DateTime.UtcNow.AddDays(30), PrefillPlatform.Xbox, containerName);
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId, containerName, PrefillPlatform.Xbox));
        using var daemon = BuildDaemon(
            PrefillPlatform.Xbox,
            MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true), cacheStatusClock: clock),
            gateway,
            client);

        await daemon.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
        await inner.StartLoginEntered.Task.WaitAsync(TestTimeout);
        var session = daemon.GetActivePersistentSession()!;
        await thirdRead.WaitAsync(TestTimeout);
        var attempt = daemon.LastHeadlessSelfAuthAttempt!;

        // The socket drops and the daemon does not answer until the saved sign-in's 2-minute bound has passed.
        model.Dropped = true;
        await inner.RaiseDisconnectedAsync();
        await WaitUntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            return attempt.IsCompleted;
        });
        await attempt.WaitAsync(TestTimeout);
        model.Reconnected = true;

        async Task PushTheEarlierFailureAsync()
        {
            // Only a sign-in the daemon still runs can push its failure.
            if (!model.EarlierSignInRunning)
            {
                return;
            }
            model.EarlierSignInRunning = false;
            var failed = XboxSignedOut(instance, loggingIn: false);
            failed.Message = EarlierSignInFailure;
            failed.ErrorCode = "login-failed";
            await inner.RaiseStatusAsync(failed);
        }

        Task<CredentialChallenge?> logIn;
        switch (order)
        {
            case "failure before the Log in":
                await PushTheEarlierFailureAsync();
                model.ReleaseTypedLogin.SetResult();
                logIn = daemon.StartLoginAsync(session.Id);
                break;
            case "failure while the login is out":
                logIn = daemon.StartLoginAsync(session.Id);
                await Task.WhenAny(model.TypedLoginSent.Task, logIn).WaitAsync(TestTimeout);
                await PushTheEarlierFailureAsync();
                model.ReleaseTypedLogin.SetResult();
                break;
            default:
                model.ReleaseTypedLogin.SetResult();
                logIn = daemon.StartLoginAsync(session.Id);
                await Task.WhenAny(logIn).WaitAsync(TestTimeout);
                await PushTheEarlierFailureAsync();
                break;
        }

        CredentialChallenge? challenge = null;
        var error = await Record.ExceptionAsync(async () => challenge = await logIn.WaitAsync(TestTimeout));
        model.ReleaseTypedLogin.TrySetResult();

        var survived = error is null
            && challenge?.ChallengeId == "typed-login"
            && session.PendingLoginChallenge?.ChallengeId == "typed-login"
            && session.LastLoginFailureMessage != EarlierSignInFailure
            && session.AuthState != DaemonAuthState.NotAuthenticated;
        return survived
            ? null
            : $"{order}: the typed Log in was ended by the earlier saved sign-in's late failure "
                + $"(error: {error?.GetType().Name}: {error?.Message}; challenge: {challenge?.ChallengeId}; "
                + $"pending challenge: {session.PendingLoginChallenge?.ChallengeId}; failure text: {session.LastLoginFailureMessage}; "
                + $"auth state: {session.AuthState})";
    }

    // The Xbox daemon after a dropped connection: the saved-login sign-in it was sent keeps running until a cancel-login
    // ends it, and a later login command answers with a new device-code prompt once the test lets it.
    private class XboxDaemonStillSigningIn : DispatchProxy
    {
        private int _logins;

        public ScriptedLoginDaemonClient Inner { get; set; } = null!;
        public bool Dropped { get; set; }
        public bool Reconnected { get; set; }
        public bool EarlierSignInRunning { get; set; } = true;
        public TaskCompletionSource TypedLoginSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseTypedLogin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IDaemonClient.StartLoginAsync) when Interlocked.Increment(ref _logins) > 1:
                    return TypedLoginAsync((CancellationToken)args![1]!);
                case nameof(IDaemonClient.CancelLoginAsync):
                case nameof(IDaemonClient.CancelLoginWithOutcomeAsync):
                    // A cancel-login ends the sign-in still running, which then pushes no failure.
                    EarlierSignInRunning = false;
                    break;
            }
            try
            {
                return targetMethod!.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private async Task<CredentialChallenge?> TypedLoginAsync(CancellationToken cancellationToken)
        {
            TypedLoginSent.TrySetResult();
            await ReleaseTypedLogin.Task.WaitAsync(cancellationToken);
            return UsernameChallenge("typed-login");
        }
    }
}
