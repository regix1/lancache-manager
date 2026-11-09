using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// An Epic sign-in is saved as soon as its code is accepted and the games are mapped afterwards in the background, so a
/// cancel, a logout, an account reset or the sign-in window ends the mapping without losing the sign-in.
/// </summary>
public sealed class EpicSignInMappingTests
{
    private const string TwoGames =
        """[{"appName":"A","buildVersion":"1","catalogItemId":"ca","namespace":"na"},{"appName":"B","buildVersion":"1","catalogItemId":"cb","namespace":"nb"}]""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADifferentAccountDoesNotInheritTheCollectionTimeOfTheAccountBeforeItAsync(bool resetInsteadOfLogout)
    {
        using var rig = new Rig(r => r.Fixture.State);
        rig.Handler.AccountId = "account-a";
        await rig.SignInAsync();
        await rig.MappingEndsAsync();
        Assert.NotNull(rig.Service.LastCollectionUtc);
        Assert.NotNull(rig.Fixture.State.GetEpicMappingCollectedAt());
        if (resetInsteadOfLogout)
        {
            await rig.Fixture.Epic.ClearAccountLoginsAsync(new HashSet<Guid> { rig.Fixture.Owner.AccountId!.Value }, CancellationToken.None);
        }
        else
        {
            await rig.Service.LogoutAsync(rig.Fixture.Owner);
        }

        // Account B's mapping is canceled after its sign-in is saved and before its library is read.
        rig.Handler.AccountId = "account-b";
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Before = (request, token) => request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/assets/Windows", StringComparison.Ordinal)
            ? held.Task.WaitAsync(token)
            : Task.CompletedTask;
        var start = await rig.SignInAsync();
        Assert.True(await rig.Service.CancelRefreshAsync(rig.Fixture.Owner, start.AttemptId));
        await rig.MappingEndsAsync();

        Assert.Null(rig.Service.LastCollectionUtc);
        Assert.Null(rig.Fixture.State.GetEpicMappingCollectedAt());
        using var restarted = await rig.RestartAsync();
        Assert.True(restarted.IsAuthenticated);
        Assert.Null(restarted.LastCollectionUtc);
    }

    [Fact]
    public async Task CancelingARunWhileItRenewsTheSavedLoginEndsTheRunAtOnceAsync()
    {
        using var rig = new Rig(r => r.Fixture.State);
        await rig.SignInAsync();
        await rig.MappingEndsAsync();

        // The run finds no live tokens, so it renews the saved login before it starts.
        typeof(EpicMappingService).GetField("_currentTokens", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig.Service, null);
        typeof(EpicMappingService).GetField("_isRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig.Service, 1);
        ((TaskCompletionSource<bool>)typeof(EpicMappingService)
            .GetField("_startupAutoReconnectCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Service)!)
            .TrySetResult(true);
        var renewing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Before = async (request, token) =>
        {
            if (request.Method != HttpMethod.Post) return;
            renewing.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        using var run = new CancellationTokenSource();

        var work = (Task)typeof(EpicMappingService)
            .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(rig.Service, [run.Token])!;
        await renewing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await run.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ASecondSignInOfTheSameAccountWhoseLibraryReadFailsKeepsTheEarlierCountAndCollectionTimeAsync()
    {
        using var rig = new Rig(r => r.Fixture.State);
        rig.Handler.Assets = TwoGames;
        await rig.SignInAsync();
        await rig.MappingEndsAsync();
        var collectedAt = rig.Service.LastCollectionUtc;
        Assert.NotNull(collectedAt);
        Assert.Equal(2, rig.Fixture.Epic.GetAuthData().GamesDiscovered);

        rig.Handler.AssetsStatus = HttpStatusCode.InternalServerError;
        await rig.SignInAsync();
        await rig.MappingEndsAsync();

        Assert.Equal("failed", rig.Tracker.GetRuns().Runs.OrderBy(run => run.StartedAt).Last().Status);
        Assert.Equal(collectedAt, rig.Service.LastCollectionUtc);
        Assert.Equal(collectedAt, rig.Fixture.State.GetEpicMappingCollectedAt());
        Assert.Equal(2, rig.Fixture.Epic.GetAuthData().GamesDiscovered);
        Assert.Equal(2, rig.Service.GamesDiscovered);
    }

    [Fact]
    public async Task AMappingThatNeverGetsAnswersEndsWithinTheSignInWindowAndKeepsTheGamesReadAsync()
    {
        using var rig = new Rig();
        rig.Handler.Assets = TwoGames;
        rig.Handler.Before = (request, token) => request.RequestUri!.AbsolutePath.StartsWith("/catalog/", StringComparison.Ordinal)
            && request.RequestUri.Query.Contains("id=cb", StringComparison.Ordinal)
            ? Task.Delay(Timeout.Infinite, token)
            : Task.CompletedTask;
        var start = await rig.BeginAsync();
        var login = rig.Fixture.Epic.ContinueIntegrationLogin(rig.Fixture.Owner, start.AttemptId);
        rig.Fixture.Epic.SetIntegrationLoginExpiry(login, DateTime.UtcNow.AddSeconds(2));
        var submitted = DateTime.UtcNow;

        await rig.SubmitAsync(start.AttemptId);
        await rig.MappingEndsAsync();

        Assert.True(DateTime.UtcNow - submitted < TimeSpan.FromSeconds(10));
        Assert.Equal("cancelled", Assert.Single(rig.Tracker.GetRuns().Runs).Status);
        Assert.True(rig.Service.IsAuthenticated);
        // The game read before the stall is kept under its real title; the one in flight is left out.
        await using var db = await rig.Databases.CreateDbContextAsync();
        var kept = await db.EpicGameMappings.ToListAsync();
        Assert.Equal("Title ca", Assert.Single(kept).Name);
        Assert.Equal(1, rig.Fixture.Epic.GetAuthData().GamesDiscovered);
        Assert.Null(rig.Service.LastCollectionUtc);
        Assert.Equal(1, rig.SessionLock.CurrentCount);
        Assert.Equal(0, rig.ProcessingFlag);
        var next = await rig.BeginAsync();
        Assert.NotEqual(start.AttemptId, next.AttemptId);
    }

    [Fact]
    public async Task ACancelRaisedInsideTheSaveStillEndsWithTheSignInLocksFreeAsync()
    {
        var state = DispatchProxy.Create<IStateService, HookedState>();
        using var rig = new Rig(_ => state);
        var start = await rig.BeginAsync();
        ((HookedState)(object)state).OnWrite = () => rig.Service.CancelRefreshAsync(rig.Fixture.Owner, start.AttemptId).GetAwaiter().GetResult();

        await rig.SubmitAsync(start.AttemptId);
        await rig.MappingEndsAsync();

        Assert.Equal("cancelled", Assert.Single(rig.Tracker.GetRuns().Runs).Status);
        Assert.True(rig.Service.IsAuthenticated);
        Assert.Equal(1, rig.SessionLock.CurrentCount);
        Assert.Equal(0, rig.ProcessingFlag);
        await rig.BeginAsync();
    }

    [Fact]
    public async Task ACancelDuringTheCodeExchangeRecordsItsEndingAsync()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Before = (request, token) =>
        {
            if (request.Method != HttpMethod.Post) return Task.CompletedTask;
            reached.TrySetResult();
            return gate.Task.WaitAsync(token);
        };
        var start = await rig.BeginAsync();
        var submit = rig.SubmitAsync(start.AttemptId);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(await rig.Service.CancelRefreshAsync(rig.Fixture.Owner, start.AttemptId));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit.WaitAsync(TimeSpan.FromSeconds(10)));
        var ending = rig.Service.GetAuthStatus(rig.Fixture.Owner, start.AttemptId).LoginEnding;
        Assert.NotNull(ending);
        Assert.Equal(OperationStatus.Cancelled, ending.Status);
        Assert.Equal("errors.integration.attemptExpired", ending.StageKey);
        Assert.Equal(1, rig.SessionLock.CurrentCount);
        Assert.Equal(0, rig.ProcessingFlag);
    }

    [Fact]
    public async Task AFailureAfterTheSaveLogsAnErrorAndEndsTheRunFailedWithoutRethrowingAsync()
    {
        using var rig = new Rig();
        rig.Handler.AssetsStatus = HttpStatusCode.InternalServerError;

        await rig.SignInAsync();
        await rig.MappingEndsAsync();

        var run = Assert.Single(rig.Tracker.GetRuns().Runs);
        Assert.Equal("failed", run.Status);
        Assert.Equal("signalr.epicMapping.failed", run.Error);
        Assert.True(rig.Service.IsAuthenticated);
        Assert.Contains(rig.Log.Entries, entry => entry.Level == LogLevel.Error
            && entry.Message.StartsWith("Epic sign-in mapping failed after the sign-in was saved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAccountResetRightAfterTheSaveLeavesNoStoredLoginAsync()
    {
        var state = DispatchProxy.Create<IStateService, HookedState>();
        using var rig = new Rig(_ => state);
        var start = await rig.BeginAsync();
        Task? reset = null;
        // The reset waits for the store's lock, which the save holds, and so runs right after the save.
        ((HookedState)(object)state).OnWrite = () => reset = Task.Run(() => rig.Fixture.Epic.ClearAccountLoginsAsync(
            new HashSet<Guid> { rig.Fixture.Owner.AccountId!.Value }, CancellationToken.None));
        ((HookedState)(object)state).Until = DateTime.UtcNow.AddMilliseconds(400);

        await rig.SubmitAsync(start.AttemptId);
        await rig.MappingEndsAsync();
        await reset!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(File.Exists(Path.Combine(rig.Fixture.Epic.GetAuthDirectory(), "credentials.json")));
        Assert.False(File.Exists(Path.Combine(rig.Fixture.Epic.GetAuthDirectory(), "saved", $"{rig.Fixture.Owner.AccountId:N}.json")));
    }

    [Fact]
    public async Task AnAccountResetDuringTheMappingBeforeTheMergeLeavesNoStoredLoginAsync()
    {
        using var rig = new Rig();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Before = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/assets/Windows", StringComparison.Ordinal))
            {
                reached.TrySetResult();
                return gate.Task.WaitAsync(token);
            }
            return Task.CompletedTask;
        };
        await rig.SignInAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await rig.Fixture.Epic.ClearAccountLoginsAsync(new HashSet<Guid> { rig.Fixture.Owner.AccountId!.Value }, CancellationToken.None);
        gate.SetResult();
        await rig.MappingEndsAsync();

        Assert.False(File.Exists(Path.Combine(rig.Fixture.Epic.GetAuthDirectory(), "credentials.json")));
    }

    [Fact]
    public async Task ACancelAfterTheGamesMergeKeepsTheSignInAndTheCountButNotTheCollectionTimeAsync()
    {
        using var rig = new Rig(r => r.Fixture.State);
        rig.Handler.Assets = TwoGames;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assetReads = 0;
        // The second assets request is the CDN step's; the mapping is canceled while it waits.
        rig.Handler.Before = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/assets/Windows", StringComparison.Ordinal)
                && Interlocked.Increment(ref assetReads) == 2)
            {
                reached.TrySetResult();
                return gate.Task.WaitAsync(token);
            }
            return Task.CompletedTask;
        };
        var start = await rig.SignInAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(await rig.Service.CancelRefreshAsync(rig.Fixture.Owner, start.AttemptId));
        await rig.MappingEndsAsync();

        Assert.Equal("cancelled", Assert.Single(rig.Tracker.GetRuns().Runs).Status);
        using var restarted = await rig.RestartAsync();
        Assert.True(restarted.IsAuthenticated);
        Assert.Equal(2, restarted.GamesDiscovered);
        Assert.Null(restarted.LastCollectionUtc);
    }

    [Fact]
    public async Task ALogoutByTheOwnerDuringTheSignInMappingCancelsTheMappingAndSignsOutAsync()
    {
        using var rig = new Rig();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Before = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/catalog/api/", StringComparison.Ordinal))
            {
                reached.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            }
            return Task.CompletedTask;
        };
        await rig.SignInAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await rig.Service.LogoutAsync(rig.Fixture.Owner).WaitAsync(TimeSpan.FromSeconds(10));
        await rig.MappingEndsAsync();

        Assert.Equal("cancelled", Assert.Single(rig.Tracker.GetRuns().Runs).Status);
        Assert.False(rig.Service.IsAuthenticated);
        Assert.True(string.IsNullOrEmpty(rig.Fixture.Epic.GetAuthData().RefreshToken));
    }

    [Fact]
    public async Task TheCountWriteKeepsARefreshTokenSavedDuringTheMappingAsync()
    {
        using var rig = new Rig();
        rig.Handler.Before = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/catalog/", StringComparison.Ordinal))
            {
                rig.Fixture.Epic.SaveAuthData(new EpicAuthData
                {
                    OwnerAccountId = rig.Fixture.Owner.AccountId,
                    RefreshToken = "rotated-during-mapping",
                    DisplayName = "owner",
                    AccountId = "epic"
                }, saveSavedLogin: false);
            }
            return Task.CompletedTask;
        };

        await rig.SignInAsync();
        await rig.MappingEndsAsync();

        var stored = rig.Fixture.Epic.GetAuthData();
        Assert.Equal("rotated-during-mapping", stored.RefreshToken);
        Assert.Equal(1, stored.GamesDiscovered);
    }

    [Fact]
    public async Task ASubmitWaitingForTheSessionLockAlreadyRefusesAPrefillAsync()
    {
        using var rig = new Rig();
        var start = await rig.BeginAsync();
        await rig.SessionLock.WaitAsync();
        var submit = rig.SubmitAsync(start.AttemptId);
        await rig.WaitUntilAsync(() => rig.IsProcessingLogin);

        Assert.Equal("login-in-progress", rig.Service.GetIntegrationLoginReason(rig.Fixture.Owner));
        var refused = await Assert.ThrowsAsync<ConflictException>(() => rig.Service.AcquireIntegrationLoginAsync(rig.Fixture.Owner));
        Assert.Equal("errors.integration.loginInProgress", refused.StageKey);
        Assert.False(rig.Service.GetAuthStatus(rig.Fixture.Owner).CanSignIn);

        rig.SessionLock.Release();
        await submit.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.MappingEndsAsync();
    }

    [Fact]
    public async Task APrefillThatPassesALeaseIsRefusedWhileASignInMappingRunsAsync()
    {
        using var rig = new Rig();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Before = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/assets/Windows", StringComparison.Ordinal))
            {
                reached.TrySetResult();
                return gate.Task.WaitAsync(token);
            }
            return Task.CompletedTask;
        };
        await rig.SignInAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var lease = await rig.Fixture.Epic.AcquireIntegrationLoginAsync(rig.Fixture.Owner);

        var refused = await Assert.ThrowsAsync<ConflictException>(() =>
            rig.Service.CreatePrefillRefreshTokenAsync(rig.Fixture.Owner.AccountId, lease: lease));

        Assert.Equal("errors.integration.loginInProgress", refused.StageKey);
        Assert.False(rig.Service.TryGetSavedLoginAccount(rig.Fixture.Owner.AccountId, out _, rig.Fixture.Owner));
        gate.SetResult();
        await rig.MappingEndsAsync();
    }

    [Fact]
    public async Task TheStatusDuringASignInMappingRefusesASignInWithItsReasonAsync()
    {
        using var rig = new Rig();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Before = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/assets/Windows", StringComparison.Ordinal))
            {
                reached.TrySetResult();
                return gate.Task.WaitAsync(token);
            }
            return Task.CompletedTask;
        };
        await rig.SignInAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var status = rig.Service.GetAuthStatus(rig.Fixture.Owner);

        Assert.False(status.CanSignIn);
        Assert.Equal("login-in-progress", status.OwnershipReason);
        gate.SetResult();
        await rig.MappingEndsAsync();
    }

    [Fact]
    public async Task ABeginWhileAMappingRunsAnswersInProgressAndLeavesNoPendingLoginAsync()
    {
        using var rig = new Rig();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Before = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/assets/Windows", StringComparison.Ordinal))
            {
                reached.TrySetResult();
                return gate.Task.WaitAsync(token);
            }
            return Task.CompletedTask;
        };
        await rig.SignInAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var refused = await Assert.ThrowsAsync<ConflictException>(() => rig.BeginAsync());

        Assert.Equal("errors.epic.authInProgress", refused.StageKey);
        Assert.Null(rig.Service.GetAuthStatus(rig.Fixture.Owner).AttemptId);
        gate.SetResult();
        await rig.MappingEndsAsync();
    }

    [Fact]
    public async Task ASecondSubmitOfTheSameAttemptDuringTheCodeExchangeAnswersInProgressAndTheFirstStillSavesAsync()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Before = (request, token) =>
        {
            if (request.Method != HttpMethod.Post) return Task.CompletedTask;
            reached.TrySetResult();
            return gate.Task.WaitAsync(token);
        };
        var start = await rig.BeginAsync();
        var first = rig.SubmitAsync(start.AttemptId);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var refused = await Assert.ThrowsAsync<ConflictException>(() => rig.SubmitAsync(start.AttemptId));
        gate.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.MappingEndsAsync();

        Assert.Equal("errors.epic.authInProgress", refused.StageKey);
        Assert.True(rig.Service.IsAuthenticated);
        Assert.Equal("completed", Assert.Single(rig.Tracker.GetRuns().Runs).Status);
    }

    [Fact]
    public async Task ACodeSubmitRefusedWhileAScheduledRefreshHoldsTheFlagKeepsItsPendingLoginAsync()
    {
        using var rig = new Rig();
        var start = await rig.BeginAsync();
        var flag = typeof(EpicMappingService).GetField("_isProcessingInt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        flag.SetValue(rig.Service, 1);

        await Assert.ThrowsAsync<ConflictException>(() => rig.SubmitAsync(start.AttemptId));
        Assert.Equal(start.AttemptId, rig.Service.GetAuthStatus(rig.Fixture.Owner).AttemptId);
        flag.SetValue(rig.Service, 0);

        await rig.SubmitAsync(start.AttemptId);
        await rig.MappingEndsAsync();
        Assert.True(rig.Service.IsAuthenticated);
    }

    [Fact]
    public async Task ACodeSubmitWhoseSignInIsCanceledBeforeItIsMarkedProcessingLetsTheNextSignInAndRefreshStartAsync()
    {
        using var rig = new Rig();
        var start = await rig.BeginAsync();
        // The shutdown source is read between the continue check and the processing mark; a disposed one throws there,
        // and the cancel runs at that throw, as a person's cancel can land between those two calls.
        var root = typeof(EpicMappingService).GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var live = root.GetValue(rig.Service);
        var disposed = new CancellationTokenSource();
        disposed.Dispose();
        root.SetValue(rig.Service, disposed);
        var submitting = new AsyncLocal<bool>();
        var canceled = 0;
        void CancelAtTheThrow(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (submitting.Value && args.Exception is ObjectDisposedException && Interlocked.Exchange(ref canceled, 1) == 0)
                rig.Service.CancelRefreshAsync(rig.Fixture.Owner, start.AttemptId).GetAwaiter().GetResult();
        }
        AppDomain.CurrentDomain.FirstChanceException += CancelAtTheThrow;
        try
        {
            submitting.Value = true;
            await Assert.ThrowsAsync<OperationCanceledException>(() => rig.SubmitAsync(start.AttemptId));
        }
        finally
        {
            submitting.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= CancelAtTheThrow;
            root.SetValue(rig.Service, live);
        }

        Assert.Equal(1, canceled);
        Assert.Equal(0, rig.ProcessingFlag);
        Assert.Equal(1, rig.SessionLock.CurrentCount);
        Assert.False(rig.IsProcessingLogin);
        typeof(EpicMappingService).GetField("_isAuthenticated", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig.Service, true);
        typeof(EpicMappingService).GetField("_currentTokens", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig.Service, new EpicOAuthTokens
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            AccountId = "epic",
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        Assert.True(rig.Service.TryStartRefresh());
        await ((Task)typeof(EpicMappingService).GetField("_currentRefreshTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(rig.Service)!).WaitAsync(TimeSpan.FromSeconds(10));
        var next = await rig.BeginAsync();
        Assert.NotEqual(start.AttemptId, next.AttemptId);
        await rig.SubmitAsync(next.AttemptId);
        await rig.MappingEndsAsync();
    }

    [Fact]
    public async Task ACancelWithAnAttemptIdAfterTheSaveStopsTheMappingAndIsNotRefusedAsExpiredAsync()
    {
        using var rig = new Rig();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Before = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/assets/Windows", StringComparison.Ordinal))
            {
                reached.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            }
            return Task.CompletedTask;
        };
        var start = await rig.SignInAsync();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(await rig.Service.CancelRefreshAsync(rig.Fixture.Owner, start.AttemptId));
        await rig.MappingEndsAsync();

        Assert.Equal("cancelled", Assert.Single(rig.Tracker.GetRuns().Runs).Status);
        Assert.True(rig.Service.IsAuthenticated);
    }

    [Fact]
    public async Task ACancelWithNoAttemptIdLeavesARunningScheduledRefreshAloneAsync()
    {
        using var rig = new Rig();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Before = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/assets/Windows", StringComparison.Ordinal))
            {
                reached.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            }
            return Task.CompletedTask;
        };
        typeof(EpicMappingService).GetField("_isAuthenticated", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig.Service, true);
        typeof(EpicMappingService).GetField("_currentTokens", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig.Service, new EpicOAuthTokens
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            AccountId = "epic",
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        Assert.True(rig.Service.TryStartRefresh());
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.BeginAsync();
        var refreshSource = (CancellationTokenSource)typeof(EpicMappingService)
            .GetField("_currentRefreshCts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Service)!;

        await Assert.ThrowsAsync<ConflictException>(() => rig.Service.CancelRefreshAsync(rig.Fixture.Owner));

        Assert.False(refreshSource.IsCancellationRequested);
        refreshSource.Cancel();
        await ((Task)typeof(EpicMappingService).GetField("_currentRefreshTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(rig.Service)!).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ABeginThatWaitedForTheStoreDoesNotCancelASignInWhoseCodeWasSubmittedMeanwhileAsync()
    {
        using var rig = new Rig();
        var first = await rig.BeginAsync();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exchange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Before = (request, token) =>
        {
            if (request.Method != HttpMethod.Post) return Task.CompletedTask;
            reached.TrySetResult();
            return exchange.Task.WaitAsync(token);
        };
        // A prefill lease keeps the store's admission gate; the second begin passes the service's check and waits for it.
        await rig.AdmissionGate.WaitAsync();
        var second = rig.BeginAsync();
        await Task.Delay(200);
        Assert.False(second.IsCompleted);
        var submit = rig.SubmitAsync(first.AttemptId);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        rig.AdmissionGate.Release();

        // The store refuses a begin while any sign-in is pending, so the second begin never touches the first one's login.
        var refused = await Assert.ThrowsAsync<ConflictException>(() => second.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("errors.integration.loginInProgress", refused.StageKey);
        exchange.SetResult();
        await submit.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.MappingEndsAsync();
        Assert.True(rig.Service.IsAuthenticated);
        var ending = rig.Service.GetAuthStatus(rig.Fixture.Owner, first.AttemptId).LoginEnding;
        Assert.NotNull(ending);
        Assert.Equal(OperationStatus.Completed, ending.Status);
        Assert.Equal("completed", Assert.Single(rig.Tracker.GetRuns().Runs).Status);
    }

    [Fact]
    public async Task ABeginThatWaitedForTheStoreUntilTheFirstSignInWasSavedAnswersInProgressAndLeavesNoPendingLoginAsync()
    {
        using var rig = new Rig();
        var first = await rig.BeginAsync();
        var mappingReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mappingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Handler.Before = (request, token) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/assets/Windows", StringComparison.Ordinal)) return Task.CompletedTask;
            mappingReached.TrySetResult();
            return mappingGate.Task.WaitAsync(token);
        };
        // The second begin passes the service's check before any code is sent, then waits for the store's admission gate.
        await rig.AdmissionGate.WaitAsync();
        var second = rig.BeginAsync();
        await Task.Delay(200);
        Assert.False(second.IsCompleted);
        // The first sign-in is saved and starts its mapping while the second begin waits.
        await rig.SubmitAsync(first.AttemptId);
        await mappingReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        rig.AdmissionGate.Release();

        // The begin finds a free store, creates its pending login and is undone under the store's lock.
        var refused = await Assert.ThrowsAsync<ConflictException>(() => second.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("errors.epic.authInProgress", refused.StageKey);
        Assert.Null(rig.Service.GetAuthStatus(rig.Fixture.Owner).AttemptId);
        mappingGate.SetResult();
        await rig.MappingEndsAsync();
        Assert.Equal("completed", Assert.Single(rig.Tracker.GetRuns().Runs).Status);
    }

    private sealed class Rig : IDisposable
    {
        private readonly HttpClient _http;
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        private readonly List<EpicMappingService> _extra = [];

        public Rig(Func<Rig, IStateService>? state = null, TimeSpan? httpTimeout = null)
        {
            Handler = new Handler();
            _http = new HttpClient(Handler) { Timeout = httpTimeout ?? TimeSpan.FromMinutes(5) };
            Tracker = new UnifiedOperationTracker(
                new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);
            Databases = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"epic_sign_in_mapping_{Guid.NewGuid():N}").Options);
            Service = Create(state?.Invoke(this) ?? DispatchProxy.Create<IStateService, NullReturningProxy>());
        }

        public IntegrationFixture Fixture { get; } = new();
        public Handler Handler { get; }
        public UnifiedOperationTracker Tracker { get; }
        public TestDbContextFactory Databases { get; }
        public CapturingLogger<EpicMappingService> Log { get; } = new();
        public EpicMappingService Service { get; }

        public SemaphoreSlim SessionLock => (SemaphoreSlim)typeof(EpicMappingService)
            .GetField("_sessionLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Service)!;

        public SemaphoreSlim AdmissionGate => (SemaphoreSlim)typeof(AuthFileStorageServiceBase<EpicAuthData, PersistedEpicAuthData>)
            .GetField("_admissionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Fixture.Epic)!;

        public int ProcessingFlag => (int)typeof(EpicMappingService)
            .GetField("_isProcessingInt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Service)!;

        public bool IsProcessingLogin => typeof(EpicMappingService)
            .GetField("_processingLogin", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Service) is not null;

        private EpicMappingService Create(IStateService state) => new(
            Log, new EpicApiDirectClient(_http, NullLogger<EpicApiDirectClient>.Instance), Fixture.Epic,
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(), Databases, Tracker,
            _services.GetRequiredService<IServiceScopeFactory>(), state);

        public Task<EpicLoginUrlResponse> BeginAsync() => Service.GetAuthorizationUrl(Fixture.Owner);

        public Task SubmitAsync(Guid attemptId) =>
            Service.OnAuthCodeReceivedAsync("code", caller: Fixture.Owner, attemptId: attemptId);

        public async Task<EpicLoginUrlResponse> SignInAsync()
        {
            var start = await BeginAsync();
            await SubmitAsync(start.AttemptId);
            return start;
        }

        // A new service over the same stored login and state, as after an app restart.
        public async Task<EpicMappingService> RestartAsync()
        {
            var restarted = Create(Fixture.State);
            _extra.Add(restarted);
            await (Task)typeof(EpicMappingService)
                .GetMethod("TryAutoReconnectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(restarted, [CancellationToken.None])!;
            return restarted;
        }

        public Task MappingEndsAsync() => WaitUntilAsync(() => !IsProcessingLogin && SessionLock.CurrentCount == 1);

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "the awaited state was not reached");
                await Task.Delay(20);
            }
        }

        public void Dispose()
        {
            foreach (var extra in _extra) extra.Dispose();
            Service.Dispose();
            _http.Dispose();
            _services.Dispose();
            Fixture.Dispose();
        }
    }

    // Answers the sign-in token, one asset list, a title per catalog item and nothing else.
    private sealed class Handler : HttpMessageHandler
    {
        public string AccountId { get; set; } = "epic";
        public string Assets { get; set; } =
            """[{"appName":"A","buildVersion":"1","catalogItemId":"ca","namespace":"na"}]""";
        public HttpStatusCode AssetsStatus { get; set; } = HttpStatusCode.OK;
        public Func<HttpRequestMessage, CancellationToken, Task>? Before { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Before is not null) await Before(request, cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                return Json(HttpStatusCode.OK,
                    $$"""{"access_token":"access","refresh_token":"refresh","expires_at":"2099-01-01T00:00:00Z","refresh_expires":28800,"expires_in":3600,"displayName":"owner","account_id":"{{AccountId}}"}""");
            }
            if (path.EndsWith("/launcher/api/public/assets/Windows", StringComparison.Ordinal))
            {
                return Json(AssetsStatus, AssetsStatus == HttpStatusCode.OK ? Assets : "{}");
            }
            if (path.StartsWith("/catalog/api/shared/namespace/", StringComparison.Ordinal))
            {
                var id = request.RequestUri.Query.Split("id=")[1].Split('&')[0];
                return Json(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(
                    new Dictionary<string, object> { [id] = new { id, title = $"Title {id}" } }));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    // A state write that runs a callback, and takes as long as a slow disk when asked to.
    private class HookedState : NullReturningProxy
    {
        public DateTime Until { get; set; }
        public Action? OnWrite { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IStateService.SetEpicMappingLastCollection))
            {
                OnWrite?.Invoke();
                while (DateTime.UtcNow <= Until) Thread.Sleep(25);
            }
            return base.Invoke(targetMethod, args);
        }
    }
}
