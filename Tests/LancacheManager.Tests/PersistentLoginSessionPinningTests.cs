using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    [Fact]
    public async Task GetGames_PinnedToReplacedSession_RejectsBeforeQuery()
    {
        var (controller, _, client) = CreateControllerWithActiveSession("session-B");
        var result = await controller.GetGamesAsync(PrefillPlatform.Steam, CancellationToken.None, "session-A");
        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(PersistentLoginConflictReasons.SessionReplaced,
            Assert.IsType<PersistentLoginConflictResponse>(conflict.Value).Error);
        Assert.DoesNotContain(nameof(IDaemonClient.GetOwnedGamesAsync), client.InvokedMethods);
    }

    [Fact]
    public async Task GetGames_SessionReplacedDuringQuery_DiscardsResult()
    {
        var (controller, daemon, client) = CreateControllerWithActiveSession("session-A");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<List<OwnedGame>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Games = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var pending = controller.GetGamesAsync(PrefillPlatform.Steam, CancellationToken.None, "session-A");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        daemon.GetSession("session-A")!.Status = DaemonSessionStatus.Terminated;
        daemon.InjectSession(CreatePersistentSession("session-B"));
        release.SetResult([]);
        var result = await pending;
        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetGames_LoginLossPropagatesSafeFailure()
    {
        var (controller, _, client) = CreateControllerWithActiveSession("session-A");
        client.Games = () => Task.FromException<List<OwnedGame>>(new DaemonCommandException("auth-lost"));
        var failure = await Assert.ThrowsAsync<DaemonCommandException>(() =>
            controller.GetGamesAsync(PrefillPlatform.Steam, CancellationToken.None, "session-A"));
        Assert.True(failure.RequiresLogin);
        Assert.Equal("errors.steam.signInLost", failure.StageKey);
    }

    [Fact]
    public async Task GetGames_XboxLoginLossAsksThePersonToLogIn()
    {
        var session = CreatePersistentSession("session-A");
        var client = DispatchProxy.Create<IDaemonClient, RecordingDaemonClientProxy>();
        ((RecordingDaemonClientProxy)client).Games =
            () => Task.FromException<List<OwnedGame>>(new DaemonCommandException("auth-lost"));
        session.Client = client;
        var database = new InMemoryDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"session_pinning_{Guid.NewGuid():N}")
            .Options);
        var controller = new PersistentPrefillController(
            new SingleDaemonServiceProvider(CreateXboxDaemon(session)),
            (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>(),
            new PrefillCacheService(database, NullLogger<PrefillCacheService>.Instance),
            NullLogger<PersistentPrefillController>.Instance);

        var failure = await Assert.ThrowsAsync<DaemonCommandException>(() =>
            controller.GetGamesAsync(PrefillPlatform.Xbox, CancellationToken.None, "session-A"));

        Assert.True(failure.RequiresLogin);
        Assert.Equal("errors.prefill.loginRequired", failure.StageKey);
    }

    [Fact]
    public async Task Prefill_XboxSignedOutAsksThePersonToLogInWithoutNamingSteam()
    {
        var session = CreatePersistentSession("session-A");
        session.Capabilities = new DaemonStatus
        {
            ProtocolVersion = 2,
            DaemonInstanceId = Guid.NewGuid().ToString(),
            MaxConcurrentRuns = 1,
            MaxConcurrentRequests = 1,
            Features = ["concurrentPrefill", "operationProgress", "targetedCancel", "inlineSelection", "activeOperations"],
            ActiveOperations = [],
            RecentOperations = []
        };
        var daemon = CreateXboxDaemon(session);

        var failure = await Assert.ThrowsAsync<DaemonCommandException>(() =>
            daemon.PrefillAsync("session-A", recent: true));

        Assert.True(failure.RequiresLogin);
        Assert.Equal("errors.prefill.loginRequired", failure.StageKey);
        Assert.DoesNotContain("Steam", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetChallenge_PersonsCancelHoldsTheContainerSignedOut()
    {
        var (controller, daemon, client) = CreateControllerWithActiveSession("session-A");
        client.LiveStatus = "logged-in";

        var before = await controller.GetChallengeAsync(PrefillPlatform.Steam, sessionId: "session-A");
        daemon.GetSession("session-A")!.LoginCanceledByPerson = true;
        var held = await controller.GetChallengeAsync(PrefillPlatform.Steam, sessionId: "session-A");

        var loggedIn = Assert.IsType<PersistentLoginStatusResponse>(Assert.IsType<OkObjectResult>(before.Result).Value);
        Assert.Equal("logged-in", loggedIn.Status);
        Assert.IsType<NoContentResult>(held.Result);
    }

    [Fact]
    public async Task GetGames_CacheCheckSeparatesLoginLossFromUnknownStatus()
    {
        var (controller, daemon, client) = CreateControllerWithActiveSession("session-A");
        client.Games = () => Task.FromResult(new List<OwnedGame> { new() { AppId = "10", Name = "Game" } });
        var cache = (PrefillCacheService)typeof(PersistentPrefillController)
            .GetField("_cacheService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
        await cache.RecordCachedAppAsync(PrefillPlatform.Steam, "10", "Game", 100, null);

        var readFailure = new InvalidOperationException("Cache status is unavailable.");
        daemon.CacheStatus = () => Task.FromException<CacheStatusResult>(readFailure);
        Assert.Same(readFailure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.GetGamesAsync(PrefillPlatform.Steam, CancellationToken.None, "session-A")));

        daemon.CacheStatus = () => Task.FromException<CacheStatusResult>(
            new DaemonCommandException("game-details-unavailable"));
        var result = await controller.GetGamesAsync(PrefillPlatform.Steam, CancellationToken.None, "session-A");
        var body = Assert.IsType<PersistentPrefillGamesDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["10"], body.CachedAppIds);
        Assert.Equal(["10"], body.UnknownAppIds);
        Assert.Empty(body.OutdatedAppIds);
        Assert.Single(body.Games);

        foreach (var failure in new[]
        {
            new DaemonCommandException("auth-lost"),
            new DaemonCommandException(requiresLogin: true)
        })
        {
            daemon.CacheStatus = () => Task.FromException<CacheStatusResult>(failure);
            var thrown = await Assert.ThrowsAsync<DaemonCommandException>(() =>
                controller.GetGamesAsync(PrefillPlatform.Steam, CancellationToken.None, "session-A"));
            Assert.Same(failure, thrown);
            Assert.True(thrown.RequiresLogin);
            Assert.Equal("errors.steam.signInLost", thrown.StageKey);
        }

        var cancellation = new OperationCanceledException();
        daemon.CacheStatus = () => Task.FromException<CacheStatusResult>(cancellation);
        Assert.Same(cancellation, await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.GetGamesAsync(PrefillPlatform.Steam, CancellationToken.None, "session-A")));
    }

    [Fact]
    public async Task GetGames_ForwardsOneExpiryAndNormalizedStatus()
    {
        var now = new DateTimeOffset(2026, 9, 21, 1, 0, 0, TimeSpan.Zero);
        var clock = new CacheStatusClock(now);
        var (controller, daemon, client) = CreateControllerWithActiveSession("session-A", cacheStatusClock: clock);
        client.Games = () => Task.FromResult(new List<OwnedGame> { new() { AppId = "10", Name = "Game" } });
        var cache = (PrefillCacheService)typeof(PersistentPrefillController)
            .GetField("_cacheService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
        await cache.RecordCachedAppAsync(PrefillPlatform.Steam, "10", "Game", 100, null);
        daemon.CacheStatus = () => Task.FromResult(new CacheStatusResult
        {
            Version = 2,
            Apps = [AppCacheStatus.Current("10")],
            Message = "partial inspection"
        });

        var result = await controller.GetGamesAsync(PrefillPlatform.Steam, CancellationToken.None, "session-A");
        var body = Assert.IsType<PersistentPrefillGamesDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(now.AddSeconds(120), daemon.CacheStatusExpiresAtUtc);
        Assert.Equal(CacheOutcome.Current, Assert.Single(body.Apps).Outcome);
        Assert.Equal("partial inspection", body.Message);
    }

    [Fact]
    public async Task GetGames_ExpiryCancelsOwnedGamesReadWithoutCancellingCaller()
    {
        var now = new DateTimeOffset(2026, 9, 21, 1, 0, 0, TimeSpan.Zero);
        var clock = new CacheStatusClock(now);
        var (controller, _, client) = CreateControllerWithActiveSession("session-A", cacheStatusClock: clock);
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GamesWithToken = async token =>
        {
            entered.TrySetResult(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        };

        var pending = controller.GetGamesAsync(PrefillPlatform.Steam, caller.Token, "session-A");
        var dependencyToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(120));

        var timedOut = await Assert.ThrowsAsync<ServiceUnavailableException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("errors.prefill.ownedGamesTimedOut", timedOut.StageKey);
        Assert.True(dependencyToken.IsCancellationRequested);
        Assert.False(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task GetGames_ExpiryCancelsCachedAppReadWithoutCancellingCaller()
    {
        var now = new DateTimeOffset(2026, 9, 21, 1, 0, 0, TimeSpan.Zero);
        var clock = new CacheStatusClock(now);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (controller, _, client) = CreateControllerWithActiveSession(
            "session-A",
            cacheStatusClock: clock,
            configureCache: cache => cache.BeforeCreateAsync = async token =>
            {
                entered.TrySetResult(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        client.Games = () => Task.FromResult(new List<OwnedGame> { new() { AppId = "10", Name = "Game" } });
        using var caller = new CancellationTokenSource();

        var pending = controller.GetGamesAsync(PrefillPlatform.Steam, caller.Token, "session-A");
        var dependencyToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(120));

        var timedOut = await Assert.ThrowsAsync<ServiceUnavailableException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("errors.prefill.ownedGamesTimedOut", timedOut.StageKey);
        Assert.True(dependencyToken.IsCancellationRequested);
        Assert.False(caller.IsCancellationRequested);
    }

    // The caller's own cancel is the only other thing that ends the read, and it still reaches the
    // middleware as a cancel, never as the timeout answer.
    [Fact]
    public async Task GetGames_CallerCancellationCancelsOwnedGamesRead()
    {
        var now = new DateTimeOffset(2026, 9, 21, 1, 0, 0, TimeSpan.Zero);
        var clock = new CacheStatusClock(now);
        var (controller, _, client) = CreateControllerWithActiveSession("session-A", cacheStatusClock: clock);
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GamesWithToken = async token =>
        {
            entered.TrySetResult(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        };

        var pending = controller.GetGamesAsync(PrefillPlatform.Steam, caller.Token, "session-A");
        var dependencyToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(dependencyToken.IsCancellationRequested);
        Assert.True(caller.IsCancellationRequested);
        Assert.Equal(now, clock.GetUtcNow());
    }

    // ---- RC3: controller pinning ---------------------------------------------------------------

    [Fact]
    public async Task GetChallenge_PinnedToReplacedSession_Returns409SessionReplaced_NeverServesReplacement()
    {
        var (controller, _, activeClient) = CreateControllerWithActiveSession(activeSessionId: "session-B");

        var result = await controller.GetChallengeAsync(PrefillPlatform.Steam, sessionId: "session-A");

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var body = Assert.IsType<PersistentLoginConflictResponse>(conflict.Value);
        Assert.Equal(PersistentLoginConflictReasons.SessionReplaced, body.Error);
        // The replacement session's daemon client must never be asked for a challenge.
        Assert.DoesNotContain(nameof(IDaemonClient.WaitForChallengeAsync), activeClient.InvokedMethods);
    }

    [Fact]
    public async Task GetChallenge_NeverWaitsOnTheDaemon()
    {
        var (controller, _, activeClient) = CreateControllerWithActiveSession(activeSessionId: "session-B");

        var result = await controller.GetChallengeAsync(PrefillPlatform.Steam, sessionId: "session-B");

        Assert.IsType<NoContentResult>(result.Result);
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

        var request = new PersistentCancelLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-A",
            LoginId = Guid.NewGuid()
        };

        var result = await controller.CancelLoginAsync(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.DoesNotContain(nameof(IDaemonClient.CancelLoginAsync), activeClient.InvokedMethods);
    }

    [Fact]
    public async Task GetChallenge_MissingSessionId_Returns400()
    {
        var (controller, _, _) = CreateControllerWithActiveSession(activeSessionId: "session-B");

        var result = await controller.GetChallengeAsync(PrefillPlatform.Steam, sessionId: null);

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

    [Fact]
    public async Task Logout_MissingSessionId_Returns400WithoutCallingDaemon()
    {
        var (controller, _, client) = CreateControllerWithActiveSession(activeSessionId: "session-B");

        var result = await controller.LogoutAsync(
            new PersistentLoginRequest { Service = PrefillPlatform.Steam, SessionId = " " },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.DoesNotContain(nameof(IDaemonClient.LogoutWithReasonAsync), client.InvokedMethods);
    }

    [Fact]
    public async Task Logout_PinnedToReplacedSession_Returns409WithoutCallingReplacementDaemon()
    {
        var (controller, _, client) = CreateControllerWithActiveSession(activeSessionId: "session-B");

        var result = await controller.LogoutAsync(
            new PersistentLoginRequest { Service = PrefillPlatform.Steam, SessionId = "session-A" },
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var body = Assert.IsType<PersistentLoginConflictResponse>(conflict.Value);
        Assert.Equal(PersistentLoginConflictReasons.SessionReplaced, body.Error);
        Assert.DoesNotContain(nameof(IDaemonClient.LogoutWithReasonAsync), client.InvokedMethods);
    }

    [Fact]
    public async Task Logout_MatchingSession_LeavesSameContainerRunningAndReturnsForgotten()
    {
        var (controller, daemon, client) = CreateControllerWithActiveSession(activeSessionId: "session-A");
        client.Logout = _ => Task.FromResult(new LogoutOutcome(Success: true, RequiresLogin: false));
        var session = daemon.GetSession("session-A")!;
        session.AuthState = DaemonAuthState.Authenticated;

        var result = await controller.LogoutAsync(
            new PersistentLoginRequest { Service = PrefillPlatform.Steam, SessionId = "session-A" },
            CancellationToken.None);

        var response = Assert.IsType<PersistentLogoutResponseDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(response.Forgotten);
        Assert.Same(session, daemon.GetSession("session-A"));
        Assert.Same(client, session.Client);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
        Assert.Contains(nameof(IDaemonClient.LogoutWithReasonAsync), client.InvokedMethods);
    }

    [Fact]
    public async Task Logout_DaemonRefusal_ThrowsTypedConflictAndKeepsCurrentContainer()
    {
        var (controller, daemon, client) = CreateControllerWithActiveSession(activeSessionId: "session-A");
        client.Logout = _ => Task.FromResult(new LogoutOutcome(Success: false, RequiresLogin: false));
        var session = daemon.GetSession("session-A")!;
        session.AuthState = DaemonAuthState.Authenticated;

        var thrown = await Assert.ThrowsAsync<ConflictException>(() => controller.LogoutAsync(
            new PersistentLoginRequest { Service = PrefillPlatform.Steam, SessionId = "session-A" },
            CancellationToken.None));

        Assert.Equal("management.auth.errors.logoutFailed", thrown.StageKey);
        Assert.Contains("did not confirm logout", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("restart", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(session, daemon.GetSession("session-A"));
        Assert.Same(client, session.Client);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
    }

    [Fact]
    public async Task Logout_CallerCancellation_PropagatesForHttp499WithoutRestartInstruction()
    {
        var (controller, daemon, client) = CreateControllerWithActiveSession(activeSessionId: "session-A");
        using var cancellation = new CancellationTokenSource();
        client.Logout = token => Task.FromCanceled<LogoutOutcome>(token);
        var session = daemon.GetSession("session-A")!;
        session.AuthState = DaemonAuthState.Authenticated;
        await cancellation.CancelAsync();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.LogoutAsync(
            new PersistentLoginRequest { Service = PrefillPlatform.Steam, SessionId = "session-A" },
            cancellation.Token));

        Assert.Equal(cancellation.Token, thrown.CancellationToken);
        Assert.Same(session, daemon.GetSession("session-A"));
        Assert.Same(client, session.Client);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
    }

    [Fact]
    public async Task Logout_AfterANewerDaemonRunFinished_ReturnsForgotten()
    {
        var (controller, daemon, client) = CreateControllerWithActiveSession("session-A");
        client.Logout = _ => Task.FromResult(new LogoutOutcome(Success: true, RequiresLogin: false));
        var session = daemon.GetSession("session-A")!;
        session.AuthState = DaemonAuthState.Authenticated;
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        session.Runs[runId] = new DaemonRun
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
                State = "completed"
            },
            TerminalCompletedFlag = 2,
            CompletedAtUtc = DateTime.UtcNow
        };

        var result = await controller.LogoutAsync(
            new PersistentLoginRequest { Service = PrefillPlatform.Steam, SessionId = "session-A" },
            CancellationToken.None);

        var response = Assert.IsType<PersistentLogoutResponseDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(response.Forgotten);
        Assert.False(session.AdmissionClosed);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
    }

    [Fact]
    public async Task CancelLogin_RequiresAValidAttemptIdentity()
    {
        var (controller, _, client) = CreateControllerWithActiveSession("session-B");
        foreach (var request in new[]
        {
            new PersistentCancelLoginRequest { Service = PrefillPlatform.Steam, SessionId = "session-B" },
            new PersistentCancelLoginRequest { Service = PrefillPlatform.Steam, SessionId = "session-B", LoginId = Guid.Empty }
        })
        {
            var result = await controller.CancelLoginAsync(request, CancellationToken.None);
            Assert.IsType<BadRequestObjectResult>(result.Result);
        }
        Assert.DoesNotContain(nameof(IDaemonClient.CancelLoginWithOutcomeAsync), client.InvokedMethods);
    }

    [Fact]
    public async Task CancelLogin_UuidBeforeStartPreventsThatRequestWithoutTouchingDaemon()
    {
        var (controller, daemon, client) = CreateControllerWithActiveSession("session-B");
        var loginId = Guid.NewGuid();
        var cancel = await controller.CancelLoginAsync(new PersistentCancelLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            LoginId = loginId
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(cancel.Result);
        Assert.DoesNotContain(nameof(IDaemonClient.CancelLoginWithOutcomeAsync), client.InvokedMethods);

        await Assert.ThrowsAsync<ConflictException>(() => controller.StartLoginAsync(new PersistentLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            LoginId = loginId
        }, CancellationToken.None));
        Assert.DoesNotContain(nameof(IDaemonClient.StartLoginWithDispatchAsync), client.InvokedMethods);
        Assert.Equal(0, daemon.GetSession("session-B")!.LoginAttempt);
    }

    [Fact]
    public async Task StartLogin_RejectsEmptyUuidBeforeDaemonWork()
    {
        var (controller, _, client) = CreateControllerWithActiveSession("session-B");
        var result = await controller.StartLoginAsync(new PersistentLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            LoginId = Guid.Empty
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.DoesNotContain(nameof(IDaemonClient.StartLoginWithDispatchAsync), client.InvokedMethods);
    }

    [Fact]
    public async Task CancelLogin_OldUuidAndMismatchedNumberCannotCancelNewAttempt()
    {
        var (controller, daemon, client) = CreateControllerWithActiveSession("session-B");
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        await controller.StartLoginAsync(new PersistentLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            LoginId = oldId
        }, CancellationToken.None);
        var firstAttempt = daemon.GetSession("session-B")!.LoginAttempt;
        await controller.CancelLoginAsync(new PersistentCancelLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            LoginId = oldId
        }, CancellationToken.None);
        await controller.StartLoginAsync(new PersistentLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            LoginId = newId
        }, CancellationToken.None);
        var currentAttempt = daemon.GetSession("session-B")!.LoginAttempt;
        Assert.Equal(firstAttempt + 1, currentAttempt);
        var cancelsBefore = client.InvokedMethods.Count(method => method == nameof(IDaemonClient.CancelLoginWithOutcomeAsync));

        foreach (var request in new[]
        {
            new PersistentCancelLoginRequest { Service = PrefillPlatform.Steam, SessionId = "session-B", LoginId = oldId },
            new PersistentCancelLoginRequest { Service = PrefillPlatform.Steam, SessionId = "session-B", LoginId = newId, LoginAttempt = firstAttempt }
        })
        {
            var result = await controller.CancelLoginAsync(request, CancellationToken.None);
            Assert.IsType<OkObjectResult>(result.Result);
        }

        Assert.Equal(cancelsBefore,
            client.InvokedMethods.Count(method => method == nameof(IDaemonClient.CancelLoginWithOutcomeAsync)));
        Assert.Equal(currentAttempt, daemon.GetSession("session-B")!.LoginAttempt);
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
    public async Task LoginAndCredential_BodiesWithOldEditIds_StillSucceed()
    {
        var (controller, _, client) = CreateControllerWithActiveSession("session-B");
        client.LoginChallenge = new CredentialChallenge { ChallengeId = "c1", CredentialType = "password", ServerPublicKey = "k" };
        // Same settings as the MVC JSON options in Program.cs, so an unknown member is skipped as it is in production.
        var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var login = System.Text.Json.JsonSerializer.Deserialize<PersistentLoginRequest>(
            $$"""{"service":"Steam","sessionId":"session-B","loginId":"{{Guid.NewGuid()}}","editSessionId":"old-edit","editActionId":"old-login"}""",
            jsonOptions)!;
        var credential = System.Text.Json.JsonSerializer.Deserialize<PersistentProvideCredentialRequest>(
            """{"service":"Steam","sessionId":"session-B","challenge":{"challengeId":"c1","credentialType":"password","serverPublicKey":"k"},"credential":"secret","editSessionId":"old-edit","editActionId":"old-credential"}""",
            jsonOptions)!;

        var loginResult = await controller.StartLoginAsync(login, CancellationToken.None);
        var credentialResult = await controller.ProvideCredentialAsync(credential, CancellationToken.None);

        Assert.Equal("c1", Assert.IsType<CredentialChallenge>(Assert.IsType<OkObjectResult>(loginResult.Result).Value).ChallengeId);
        Assert.IsType<OkObjectResult>(credentialResult.Result);
        Assert.Contains(nameof(IDaemonClient.ProvideCredentialAsync), client.InvokedMethods);
    }

    [Fact]
    public async Task CancelLogin_BeforeDispatchWaitsForReceiptWithoutSendingDaemonCancel()
    {
        var (controller, daemon, client) = CreateControllerWithActiveSession("session-B");
        client.HoldLoginDispatch = true;
        client.FailLoginDispatch = true;
        var loginId = Guid.NewGuid();
        var start = controller.StartLoginAsync(new PersistentLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            LoginId = loginId
        }, CancellationToken.None);
        await client.LoginDispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancel = controller.CancelLoginAsync(new PersistentCancelLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            LoginId = loginId
        }, CancellationToken.None);
        Assert.False(cancel.IsCompleted);
        client.ReleaseLoginDispatch.SetResult();
        await Assert.ThrowsAsync<IOException>(() => start);
        Assert.IsType<OkObjectResult>((await cancel.WaitAsync(TimeSpan.FromSeconds(5))).Result);
        Assert.DoesNotContain(nameof(IDaemonClient.CancelLoginWithOutcomeAsync), client.InvokedMethods);
        Assert.Equal(DaemonAuthState.NotAuthenticated, daemon.GetSession("session-B")!.AuthState);
    }

    [Fact]
    public async Task CancelLogin_AfterDispatchReturnsBeforeHeldLoginResult()
    {
        var (controller, _, client) = CreateControllerWithActiveSession("session-B");
        client.HoldLoginResult = true;
        var loginId = Guid.NewGuid();
        var start = controller.StartLoginAsync(new PersistentLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            LoginId = loginId
        }, CancellationToken.None);
        await client.LoginResultEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancel = await controller.CancelLoginAsync(new PersistentCancelLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            LoginId = loginId
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<OkObjectResult>(cancel.Result);
        Assert.False(start.IsCompleted);
        Assert.Contains(nameof(IDaemonClient.CancelLoginWithOutcomeAsync), client.InvokedMethods);
        client.ReleaseLoginResult.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
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
    public async Task IntegrationLoginAvailability_UsesOnlyTheTrustedCallerAccount()
    {
        var accountId = Guid.NewGuid();
        var (controller, daemon, _) = CreateControllerWithActiveSession("session-B", accountId);
        daemon.SaveLogin(accountId, "saved-account");

        var result = await controller.GetIntegrationLoginAvailability(PrefillPlatform.Steam);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var availability = Assert.IsType<IntegrationLoginAvailability>(ok.Value);
        Assert.True(availability.Available);
        Assert.Equal("saved-account", availability.Account);
        Assert.Equal(accountId, daemon.AvailabilityAccountId);
    }

    [Fact]
    public async Task IntegrationLoginAvailability_DoesNotRevealAnotherAccountsSavedLogin()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var (controller, daemon, _) = CreateControllerWithActiveSession("session-B", accountB);
        daemon.SaveLogin(accountA, "Account A");

        var result = await controller.GetIntegrationLoginAvailability(PrefillPlatform.Steam);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var availability = Assert.IsType<IntegrationLoginAvailability>(ok.Value);
        Assert.False(availability.Available);
        Assert.Null(availability.Account);
        Assert.Equal("no-saved-login", availability.Reason);
        Assert.Equal(accountB, daemon.AvailabilityAccountId);
    }

    [Fact]
    public async Task IntegrationLoginAvailability_WithoutStableAccountExplainsRequirement()
    {
        var (controller, _, _) = CreateControllerWithActiveSession("session-B", accountId: null);

        var result = await controller.GetIntegrationLoginAvailability(PrefillPlatform.Steam);

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
    public async Task ReuseSavedLogin_ClearsDaemonLoginAndHidesInternalChallengeAsync()
    {
        var accountId = Guid.NewGuid();
        var (controller, daemon, recorder) = CreateControllerWithActiveSession("session-B", accountId);
        daemon.SaveLogin(accountId, "saved-account");
        daemon.EmitChallengeDuringReuse = true;

        var result = await controller.StartLoginAsync(new PersistentLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            ReuseIntegration = true
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var session = daemon.GetSession("session-B")!;
        Assert.Contains(nameof(IDaemonClient.CancelLoginWithOutcomeAsync), recorder.InvokedMethods);
        Assert.Null(session.PendingLoginChallenge);
        Assert.False(session.SuppressLoginChallengePublication);
    }

    [Fact]
    public async Task ReuseSavedLogin_RemainsAvailableWithoutAuthenticationAsync()
    {
        var (controller, daemon, _) = CreateControllerWithActiveSession(
            "session-B",
            accountId: null,
            authenticationEnabled: false);
        daemon.SaveSharedLogin("saved-account");

        var result = await controller.StartLoginAsync(new PersistentLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            ReuseIntegration = true
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("logged-in", Assert.IsType<PersistentLoginStatusResponse>(ok.Value).Status);
        Assert.Null(daemon.ReuseAccountId);
    }

    [Fact]
    public async Task ReuseSavedLogin_StopsWhenDaemonLoginCannotBeClearedAsync()
    {
        var accountId = Guid.NewGuid();
        var (controller, daemon, recorder) = CreateControllerWithActiveSession("session-B", accountId);
        daemon.SaveLogin(accountId, "saved-account");
        recorder.AcknowledgeLoginCancel = false;

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
        Assert.False(daemon.GetSession("session-B")!.SuppressLoginChallengePublication);
    }

    [Fact]
    public async Task ReuseSavedLogin_ATimeoutNamesTheLoginTimedOutReasonAsync()
    {
        var accountId = Guid.NewGuid();
        var (controller, daemon, _) = CreateControllerWithActiveSession("session-B", accountId);
        daemon.SaveLogin(accountId, "saved-account");
        daemon.ReuseTimesOut = true;

        var result = await controller.StartLoginAsync(new PersistentLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            ReuseIntegration = true
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("prefill.persistent.loginTimedOut", daemon.GetSession("session-B")!.LastLoginFailureKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReuseSavedLogin_WaitsForAuthenticationAfterAdmissionAsync(bool success)
    {
        var accountId = Guid.NewGuid();
        var (controller, daemon, recorder) = CreateControllerWithActiveSession("session-B", accountId);
        daemon.SaveLogin(accountId, "saved-account");
        daemon.CompleteLogin = false;
        var pending = controller.StartLoginAsync(new PersistentLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            ReuseIntegration = true
        }, CancellationToken.None);

        Assert.False(pending.IsCompleted);
        await recorder.EmitStatusAsync(new DaemonStatus
        {
            Status = success ? "logged-in" : "awaiting-login",
            Message = success ? "Authenticated" : "Auto-login failed: Refresh token expired"
        });
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        if (success)
        {
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            Assert.Equal("logged-in", Assert.IsType<PersistentLoginStatusResponse>(ok.Value).Status);
        }
        else
        {
            var failure = Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Contains("Refresh token expired", System.Text.Json.JsonSerializer.Serialize(failure.Value), StringComparison.Ordinal);
        }
        Assert.Equal(0, recorder.StatusSubscribers);
    }

    [Fact]
    public async Task ReuseSavedLogin_UuidCancelReturnsBeforeAuthenticationAndFencesLateStatus()
    {
        var accountId = Guid.NewGuid();
        var (controller, daemon, recorder) = CreateControllerWithActiveSession("session-B", accountId);
        daemon.SaveLogin(accountId, "saved-account");
        daemon.CompleteLogin = false;
        var loginId = Guid.NewGuid();
        var start = controller.StartLoginAsync(new PersistentLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            ReuseIntegration = true,
            LoginId = loginId
        }, CancellationToken.None);
        Assert.False(start.IsCompleted);
        var cancelsBefore = recorder.InvokedMethods.Count(method => method == nameof(IDaemonClient.CancelLoginWithOutcomeAsync));

        var cancel = await controller.CancelLoginAsync(new PersistentCancelLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            LoginId = loginId
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<OkObjectResult>(cancel.Result);
        Assert.False(start.IsCompleted);
        Assert.Equal(cancelsBefore + 1,
            recorder.InvokedMethods.Count(method => method == nameof(IDaemonClient.CancelLoginWithOutcomeAsync)));

        await recorder.EmitStatusAsync(new DaemonStatus { Status = "logged-in" });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(DaemonAuthState.NotAuthenticated, daemon.GetSession("session-B")!.AuthState);
    }

    [Fact]
    public async Task ReuseSavedLogin_CancellationStopsPendingDaemonLoginAsync()
    {
        var accountId = Guid.NewGuid();
        var (controller, daemon, recorder) = CreateControllerWithActiveSession("session-B", accountId);
        daemon.SaveLogin(accountId, "saved-account");
        daemon.CompleteLogin = false;
        using var cancellation = new CancellationTokenSource();
        var pending = controller.StartLoginAsync(new PersistentLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            ReuseIntegration = true
        }, cancellation.Token);
        Assert.False(pending.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.Contains(nameof(IDaemonClient.CancelLoginWithOutcomeAsync), recorder.InvokedMethods);
        Assert.Equal(0, recorder.StatusSubscribers);
    }

    [Fact]
    public async Task ReuseSavedLogin_WithoutStableAccountReturns403Async()
    {
        var (controller, daemon, _) = CreateControllerWithActiveSession("session-B", accountId: null);

        var error = await Assert.ThrowsAsync<ForbiddenException>(() => controller.StartLoginAsync(new PersistentLoginRequest
        {
            Service = PrefillPlatform.Steam,
            SessionId = "session-B",
            ReuseIntegration = true
        }, CancellationToken.None));

        Assert.Equal("errors.integration.accountRequired", error.StageKey);
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
        var accountId = Guid.NewGuid();
        var (controller, daemon, client) = CreateControllerWithActiveSession("session-B", accountId);
        daemon.SaveLogin(accountId, "saved-account");
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

    // ---- Persistent list: a failed status read is unknown, never "not signed in" ----------------

    [Fact]
    public async Task List_StatusReadAnswersNothing_ReportsSignInUnknown()
    {
        var (controller, _, client) = CreateControllerWithActiveSession("session-A");
        client.StatusRead = () => Task.FromResult<DaemonStatus?>(null);

        var row = await ListSingleRowAsync(controller);

        Assert.True(ListFlag(row, "authUnknown"));
        Assert.False(ListFlag(row, "isAuthenticated"));
    }

    [Fact]
    public async Task List_StatusReadThrows_ReportsSignInUnknown()
    {
        var (controller, _, client) = CreateControllerWithActiveSession("session-A");
        client.StatusRead = () => Task.FromException<DaemonStatus?>(new IOException("status read failed"));

        var row = await ListSingleRowAsync(controller);

        Assert.True(ListFlag(row, "authUnknown"));
        Assert.False(ListFlag(row, "isAuthenticated"));
    }

    [Theory]
    [InlineData("logged-in", true)]
    [InlineData("awaiting-login", false)]
    public async Task List_StatusReadAnswers_IsNeverUnknown(string daemonStatus, bool expectedSignedIn)
    {
        var (controller, _, client) = CreateControllerWithActiveSession("session-A");
        client.LiveStatus = daemonStatus;

        var row = await ListSingleRowAsync(controller);

        Assert.False(ListFlag(row, "authUnknown"));
        Assert.Equal(expectedSignedIn, ListFlag(row, "isAuthenticated"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThePersistentListSendsNoSingleRunFields(bool protocolTwo)
    {
        var (controller, _, client) = CreateControllerWithActiveSession("session-A");
        // The list reads the daemon's status first, so the capabilities come from what the daemon answers.
        if (protocolTwo)
        {
            var status = new DaemonStatus
            {
                Status = "logged-in",
                ProtocolVersion = 2,
                DaemonInstanceId = Guid.NewGuid().ToString(),
                MaxConcurrentRuns = 3,
                MaxConcurrentRequests = 1,
                Features = ["concurrentPrefill", "operationProgress", "targetedCancel", "inlineSelection", "activeOperations"],
                ActiveOperations = [],
                RecentOperations = []
            };
            client.StatusRead = () => Task.FromResult<DaemonStatus?>(status);
        }

        var row = await ListSingleRowAsync(controller);

        foreach (var name in new[] { "isPrefilling", "runId", "totalBytesTransferred", "currentAppName" })
            Assert.False(row.TryGetProperty(name, out _), $"the list row still carries '{name}'");
        var limit = row.GetProperty("maxConcurrentRuns");
        if (protocolTwo)
            Assert.Equal(3, limit.GetInt32());
        else
            Assert.Equal(JsonValueKind.Null, limit.ValueKind);
        Assert.Equal(0, row.GetProperty("activeRunCount").GetInt32());
    }

    private static async Task<JsonElement> ListSingleRowAsync(PersistentPrefillController controller)
    {
        var answer = Assert.IsType<OkObjectResult>((await controller.ListAsync(CancellationToken.None)).Result);
        var rows = Assert.IsType<List<PersistentPrefillSessionDto>>(answer.Value);
        return JsonSerializer.SerializeToElement(Assert.Single(rows), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static bool ListFlag(JsonElement row, string name)
    {
        Assert.True(row.TryGetProperty(name, out var value), $"the list row carries no '{name}' field");
        return value.GetBoolean();
    }

    // ---- Fixtures -------------------------------------------------------------------------------

    private static (PersistentPrefillController Controller, TestableSteamDaemonService Daemon, RecordingDaemonClientProxy ActiveClient)
        CreateControllerWithActiveSession(
            string activeSessionId,
            Guid? accountId = null,
            bool authenticationEnabled = true,
            TimeProvider? cacheStatusClock = null,
            Action<InMemoryDbContextFactory>? configureCache = null)
    {
        var daemon = CreateDaemon(cacheStatusClock);

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
        configureCache?.Invoke(dbFactory);
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        var provider = new SingleDaemonServiceProvider(daemon);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:EnableAuthentication"] = authenticationEnabled.ToString()
            })
            .Build();

        var controller = new PersistentPrefillController(
            provider, stateService, cacheService, NullLogger<PersistentPrefillController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection()
                    .AddSingleton<IConfiguration>(configuration)
                    .AddSingleton<IDbContextFactory<AppDbContext>>(dbFactory)
                    .BuildServiceProvider()
            }
        };
        controller.HttpContext.Items["Session"] = new UserSession
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            SessionType = SessionType.Admin
        };

        return (controller, daemon, recorder);
    }

    private static TestableSteamDaemonService CreateDaemon(TimeProvider? cacheStatusClock = null)
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
            stateService, sessionService, cacheService, networkOptions, cacheStatusClock);
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

    private static XboxPrefillDaemonService CreateXboxDaemon(DaemonSession session)
    {
        var database = new InMemoryDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"session_pinning_xbox_{Guid.NewGuid():N}")
            .Options);
        var daemon = new XboxPrefillDaemonService(
            NullLogger<XboxPrefillDaemonService>.Instance,
            (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            new ConfigurationBuilder().Build(),
            (IPathResolver)DispatchProxy.Create<IPathResolver, NullReturningProxy>(),
            (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>(),
            new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance),
            new PrefillCacheService(database, NullLogger<PrefillCacheService>.Instance),
            null!,
            new StaticOptionsMonitor<PrefillNetworkOptions>(new PrefillNetworkOptions()),
            new TestLancacheServerLocator(),
            new UnavailableContainerGatewayFactory());
        var sessions = (ConcurrentDictionary<string, DaemonSession>)typeof(PrefillDaemonServiceBase)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(daemon)!;
        sessions[session.Id] = session;
        return daemon;
    }

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
            TimeProvider? cacheStatusClock = null)
            : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService,
                networkOptions, new TestLancacheServerLocator(), new UnavailableContainerGatewayFactory(),
                cacheStatusClock: cacheStatusClock)
        {
        }

        public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;

        public Func<Task<CacheStatusResult>>? CacheStatus { get; set; }
        public DateTimeOffset? CacheStatusExpiresAtUtc { get; private set; }

        public override Task<CacheStatusResult> GetCacheStatusAsync(string sessionId, List<string> appIds,
            DateTimeOffset expiresAtUtc,
            CancellationToken cancellationToken = default)
        {
            CacheStatusExpiresAtUtc = expiresAtUtc;
            return CacheStatus is null
                ? base.GetCacheStatusAsync(sessionId, appIds, expiresAtUtc, cancellationToken)
                : CacheStatus();
        }

        public int AuthVolumeClears { get; private set; }

        public override Task<PersistentVolumeClearResult> ClearPersistentAuthVolumeAsync(CancellationToken cancellationToken = default)
        {
            AuthVolumeClears++;
            return Task.FromResult(PersistentVolumeClearResult.Removed);
        }

        public Guid? AvailabilityAccountId { get; private set; }
        public Guid? ReuseAccountId { get; private set; }
        public bool CompleteLogin { get; set; } = true;
        public bool EmitChallengeDuringReuse { get; set; }
        public bool ReuseTimesOut { get; set; }
        private string? SharedAccount { get; set; }
        private Dictionary<Guid, string> SavedAccounts { get; } = [];

        public void SaveLogin(Guid accountId, string account) => SavedAccounts[accountId] = account;
        public void SaveSharedLogin(string account) => SharedAccount = account;

        public override IntegrationLoginAvailability GetIntegrationLoginAvailability(Guid? accountId, IntegrationCaller? caller = null)
        {
            AvailabilityAccountId = accountId;
            if (accountId is null)
            {
                if (caller?.AuthenticationEnabled == false && SharedAccount is not null)
                {
                    return new IntegrationLoginAvailability(true, SharedAccount, null);
                }
                return new IntegrationLoginAvailability(false, null, "account-required");
            }

            return SavedAccounts.TryGetValue(accountId.Value, out var account)
                ? new IntegrationLoginAvailability(true, account, null)
                : new IntegrationLoginAvailability(false, null, "no-saved-login");
        }

        protected override async Task<bool> ReuseIntegrationLoginAsync(
            DaemonSession session,
            Guid? accountId,
            Action onCommandDispatched,
            CancellationToken cancellationToken,
            IntegrationLease? lease = null)
        {
            ReuseAccountId = accountId;
            if (ReuseTimesOut) throw new TimeoutException();
            onCommandDispatched();
            if (EmitChallengeDuringReuse)
            {
                await InvokeOnCredentialChallengeAsync(session, new CredentialChallenge
                {
                    ChallengeId = "internal-saved-login",
                    CredentialType = "refresh-token",
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5)
                });
            }
            if (CompleteLogin) ((RecordingDaemonClientProxy)(object)session.Client).LiveStatus = "logged-in";
            return true;
        }

        public override Task<IntegrationLease> AcquireIntegrationLoginAsync(IntegrationCaller caller, CancellationToken cancellationToken = default)
        {
            if (caller.AuthenticationEnabled && caller.AccountId is null) IntegrationLease.Refuse("account-required");
            return Task.FromResult(new IntegrationLease(this, 0, caller, () => { }, () => { }));
        }

        public Task InvokeOnCredentialChallengeAsync(DaemonSession session, CredentialChallenge challenge)
            => OnCredentialChallengeAsync(session, challenge);
    }

    /// <summary>
    /// Minimal <see cref="IServiceProvider"/> that resolves only the one daemon service it holds - the
    /// single platform type the exercised endpoints request via
    /// <see cref="PrefillDaemonServiceBase.ResolveDaemon"/>.
    /// </summary>
    private sealed class SingleDaemonServiceProvider : IServiceProvider
    {
        private readonly PrefillDaemonServiceBase _daemon;

        public SingleDaemonServiceProvider(PrefillDaemonServiceBase daemon) => _daemon = daemon;

        public object? GetService(Type serviceType)
            => serviceType.IsInstanceOfType(_daemon) ? _daemon : null;
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
        public bool HoldLoginDispatch { get; set; }
        public bool HoldLoginResult { get; set; }
        public TaskCompletionSource LoginDispatchEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLoginDispatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LoginResultEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLoginResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool AcknowledgeLoginCancel { get; set; } = true;
        public string LiveStatus { get; set; } = "awaiting-login";
        public CredentialChallenge? LoginChallenge { get; set; }
        public Func<Task<List<OwnedGame>>>? Games { get; set; }
        public Func<CancellationToken, Task<List<OwnedGame>>>? GamesWithToken { get; set; }
        public Func<CancellationToken, Task<LogoutOutcome>>? Logout { get; set; }
        public Func<Task>? CancelPrefill { get; set; }
        public Func<Task<DaemonStatus?>>? StatusRead { get; set; }
        private Func<DaemonStatus, Task>? StatusChanged { get; set; }
        public int StatusSubscribers => StatusChanged?.GetInvocationList().Length ?? 0;

        public Task EmitStatusAsync(DaemonStatus status)
        {
            LiveStatus = status.Status;
            return StatusChanged?.Invoke(status) ?? Task.CompletedTask;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "add_OnStatusUpdate")
            {
                StatusChanged += (Func<DaemonStatus, Task>)args![0]!;
                return null;
            }
            if (targetMethod?.Name == "remove_OnStatusUpdate")
            {
                StatusChanged -= (Func<DaemonStatus, Task>)args![0]!;
                return null;
            }
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
                return RunLoginAsync((Action)args![1]!);
            }

            if (targetMethod?.Name == nameof(IDaemonClient.GetStatusAsync))
            {
                if (StatusRead is not null)
                {
                    return StatusRead();
                }

                return Task.FromResult<DaemonStatus?>(new DaemonStatus { Status = LiveStatus });
            }

            if (targetMethod?.Name == nameof(IDaemonClient.CancelLoginWithOutcomeAsync))
            {
                return Task.FromResult(AcknowledgeLoginCancel);
            }

            if (targetMethod?.Name == nameof(IDaemonClient.LogoutWithReasonAsync)
                && Logout is not null)
            {
                return Logout((CancellationToken)args![0]!);
            }

            // The single-argument overload is the older daemon's cancel of its one run.
            if (targetMethod?.Name == nameof(IDaemonClient.CancelPrefillAsync)
                && targetMethod.GetParameters().Length == 1
                && CancelPrefill is not null)
            {
                return CancelPrefill();
            }

            if (targetMethod?.Name == nameof(IDaemonClient.GetOwnedGamesAsync))
            {
                if (GamesWithToken is not null)
                    return GamesWithToken((CancellationToken)args![0]!);
                if (Games is not null)
                    return Games();
            }

            return DefaultReturnValue(targetMethod);
        }

        private async Task<CredentialChallenge?> RunLoginAsync(Action onCommandDispatched)
        {
            LoginDispatchEntered.TrySetResult();
            if (HoldLoginDispatch) await ReleaseLoginDispatch.Task;
            if (FailLoginDispatch) throw new IOException("Login command failed before dispatch");
            onCommandDispatched();
            LoginResultEntered.TrySetResult();
            if (HoldLoginResult) await ReleaseLoginResult.Task;
            return LoginChallenge;
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

        public Func<CancellationToken, Task>? BeforeCreateAsync { get; set; }

        public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (BeforeCreateAsync is not null)
                await BeforeCreateAsync(cancellationToken);
            return new AppDbContext(_options);
        }
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

/// <summary>
/// The persistent container has no download route of its own: schedules start their downloads in
/// process, so a signed-in caller posting to the old path reaches the API's not-found answer while the
/// container's cancel route beside it still binds its body.
/// </summary>
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class PersistentPrefillDownloadRouteTests
{
    [Fact]
    public async Task PostingToThePersistentDownloadPath_AnswersNotFound()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = await host.CreateAdminClientAsync();

        using var download = await client.PostAsJsonAsync("/api/system/prefill/persistent/prefill", new { });
        using var cancel = await client.PostAsJsonAsync("/api/system/prefill/persistent/cancel-prefill", new { });

        Assert.Equal(System.Net.HttpStatusCode.NotFound, download.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, cancel.StatusCode);
    }
}
