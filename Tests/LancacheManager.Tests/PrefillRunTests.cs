using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class PrefillRunTests
{
    [Fact]
    public async Task EmptyCacheSnapshotReachesTheDaemonAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await fixture.StartAsync("10");
        Assert.NotNull(fixture.Client.CachedDepots);
        Assert.Empty(fixture.Client.CachedDepots);
    }

    [Fact]
    public async Task RunIsolationAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var first = await fixture.StartAsync("10");
        var second = await fixture.StartAsync("20");
        var third = await fixture.StartAsync("30");
        Assert.Equal(3, fixture.Daemon.GetRuns(fixture.Session.Id).Count);
        Assert.Equal("20", Assert.Single(second.Options.AppIds!));

        fixture.Client.Set(first, "downloading", 10, "downloading");
        fixture.Client.Set(second, "downloading", 20, "downloading");
        fixture.Client.Set(third, "downloading", 30, "downloading");
        await fixture.RefreshAsync();
        await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, second.PrefillRunId, true);

        Assert.Equal([second.PrefillRunId], fixture.Client.Canceled);
        Assert.False(first.Completion.Task.IsCompleted);
        Assert.False(third.Completion.Task.IsCompleted);
        Assert.Equal("canceled", (await second.Completion.Task).Snapshot.State);
        Assert.Equal(10, first.Snapshot.BytesTransferred);
        Assert.Equal(30, third.Snapshot.BytesTransferred);
        Assert.Contains(fixture.Session.Runs.Values, run => run.TerminalCompletedFlag != 2);
    }

    [Theory]
    [InlineData("success", 100, true)]
    [InlineData("already_cached", 0, false)]
    [InlineData("canceled", 25, false)]
    [InlineData("failed", 25, false)]
    [InlineData("skipped", 0, false)]
    public async Task OnlySuccessfulDownloadsCreateCacheRecordsAsync(string result, long bytes, bool cached)
    {
        await using var fixture = await RunFixture.CreateAsync();
        var run = await fixture.StartAsync("10");
        fixture.Client.Set(run, "completed", bytes, result);
        var page = fixture.Client.Pages[run.PrefillRunId];
        fixture.Client.Pages[run.PrefillRunId] = page with
        {
            Items = [page.Items[0] with
            {
                Depots = [new DepotManifestProgressInfo { DepotId = 11, ManifestId = 12, TotalBytes = 100 }]
            }]
        };
        await fixture.RefreshAsync();
        await using var context = new AppDbContext(fixture.Options);
        Assert.Equal(cached ? 1 : 0, await context.PrefillCachedApps.CountAsync());
        Assert.Equal(cached ? 1 : 0, await context.PrefillCachedDepots.CountAsync());
    }

    [Theory]
    [InlineData("success", 1)]
    [InlineData("failed", 0)]
    public async Task OnlyANewCacheRecordBroadcastsACacheChangeAsync(string result, int broadcasts)
    {
        await using var fixture = await RunFixture.CreateAsync();
        var run = await fixture.StartAsync("10");
        fixture.Client.Set(run, "completed", 100, result);
        var page = fixture.Client.Pages[run.PrefillRunId];
        fixture.Client.Pages[run.PrefillRunId] = page with
        {
            Items = [page.Items[0] with
            {
                Depots = [new DepotManifestProgressInfo { DepotId = 11, ManifestId = 12, TotalBytes = 100 }]
            }]
        };

        await fixture.RefreshAsync();

        Assert.Equal(broadcasts, fixture.Recorder.Invocations.Count(call =>
            call.Method == nameof(ISignalRNotificationService.NotifyAllAsync)
            && call.Args.Length > 0 && call.Args[0] as string == SignalREvents.PrefillCacheChanged));
    }

    [Fact]
    public async Task ADaemonRunLimitRefusalIsAnAlreadyRunningConflictAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        fixture.Client.StartRefusal = "run-limit";

        await Assert.ThrowsAsync<PrefillAlreadyRunningException>(
            () => fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]));

        var run = Assert.Single(fixture.Session.Runs.Values);
        Assert.Equal("failed", run.Snapshot.State);
        Assert.Equal("run-limit", run.Snapshot.Reason);
    }

    [Fact]
    public async Task CachedResultDoesNotRestoreClearedRecordsAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var cache = new PrefillCacheService(new TestDbContextFactory(fixture.Options),
            NullLogger<PrefillCacheService>.Instance);
        await cache.RecordCachedAppAsync(PrefillPlatform.Steam, "10", "Game", 100, null);
        await using (var seed = new AppDbContext(fixture.Options))
        {
            seed.PrefillCachedDepots.Add(new PrefillCachedDepot
            {
                AppId = 10,
                DepotId = 11,
                ManifestId = 12,
                AppName = "Game",
                TotalBytes = 100,
                CachedAtUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }
        var run = await fixture.StartAsync("10");
        Assert.Single(fixture.Client.CachedDepots!);
        await cache.ClearAppCacheAsync(PrefillPlatform.Steam, "10");
        fixture.Client.Set(run, "completed", 0, "already_cached");
        var page = fixture.Client.Pages[run.PrefillRunId];
        fixture.Client.Pages[run.PrefillRunId] = page with
        {
            Items = [page.Items[0] with
            {
                Depots = [new DepotManifestProgressInfo { DepotId = 11, ManifestId = 12, TotalBytes = 100 }]
            }]
        };
        await fixture.RefreshAsync();
        Assert.Empty(await cache.GetCachedAppsAsync(PrefillPlatform.Steam));
        Assert.Empty(await cache.GetAllCachedDepotsAsync());
    }

    [Fact]
    public async Task RunAdmissionAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var schedule = Guid.NewGuid();
        await fixture.StartAsync("10", schedule);
        await Assert.ThrowsAsync<PrefillAlreadyRunningException>(() => fixture.StartAsync("20", schedule));
        await fixture.StartAsync("20");
        await fixture.StartAsync("30");
        await Assert.ThrowsAsync<PrefillAlreadyRunningException>(() => fixture.StartAsync("40"));
        Assert.Equal(3, fixture.Client.StartCount);
    }

    [Fact]
    public async Task AmbiguousCancelAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await fixture.StartAsync("10");
        await fixture.StartAsync("20");
        var error = await Assert.ThrowsAsync<DaemonCommandException>(() => fixture.Daemon.CancelPrefillAsync(fixture.Session.Id));
        Assert.Equal("ambiguous-operation", error.ErrorCode);
        Assert.Empty(fixture.Client.Canceled);
    }

    [Fact]
    public async Task TerminalReplayAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var first = await fixture.StartAsync("10");
        var second = await fixture.StartAsync("20");
        fixture.Client.Set(first, "completed", 100, "success");
        await Task.WhenAll(fixture.RefreshAsync(), fixture.RefreshAsync());
        await fixture.RefreshAsync();
        Assert.Equal("completed", (await first.Completion.Task).Snapshot.State);
        fixture.Client.Set(first, "failed", 1000, "failed");
        await fixture.RefreshAsync();
        Assert.Equal(100, first.Snapshot.BytesTransferred);
        Assert.False(second.Completion.Task.IsCompleted);
        await using var context = new AppDbContext(fixture.Options);
        var row = await context.PrefillHistoryEntries.SingleAsync(entry => entry.RunId == first.PrefillRunId);
        Assert.Equal(PrefillHistoryEntryStatus.Completed, row.Status);
        Assert.Equal(100, row.BytesDownloaded);
    }

    [Fact]
    public async Task ARunThatDownloadedAGameRunsTheNameMatchOnce()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var daemon = (TestableSteamDaemonService)fixture.Daemon;

        async Task EndRunAsync(string appId, string itemResult)
        {
            var run = await fixture.StartAsync(appId);
            fixture.Client.Set(run, "completed", 100, itemResult);
            await fixture.RefreshAsync();
            await run.Completion.Task;
        }

        await EndRunAsync("10", "already_cached");
        await EndRunAsync("20", "skipped");
        Assert.Equal(0, daemon.RunDownloadedCalls);
        await EndRunAsync("30", "success");
        Assert.Equal(1, daemon.RunDownloadedCalls);
    }

    [Fact]
    public async Task ANameMatchThatThrowsLeavesTheRunEnded()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var daemon = (TestableSteamDaemonService)fixture.Daemon;

        async Task<(DaemonRun Run, List<(string Method, object? First)> Calls)> EndRunAsync(string appId)
        {
            var before = fixture.Recorder.Invocations.Count;
            var run = await fixture.StartAsync(appId);
            fixture.Client.Set(run, "completed", 100, "success");
            await fixture.RefreshAsync();
            await run.Completion.Task;
            var calls = fixture.Recorder.Invocations.GetRange(before, fixture.Recorder.Invocations.Count - before)
                .Select(call => (call.Method, call.Args[0])).ToList();
            return (run, calls);
        }

        var first = await EndRunAsync("10");
        daemon.RunDownloaded = () => throw new InvalidOperationException("The name match failed.");
        var second = await EndRunAsync("20");

        Assert.Equal(2, daemon.RunDownloadedCalls);
        Assert.Equal("completed", first.Run.Snapshot.State);
        Assert.Equal(first.Run.Snapshot.State, second.Run.Snapshot.State);
        Assert.Equal(2, first.Run.TerminalCompletedFlag);
        Assert.Equal(2, second.Run.TerminalCompletedFlag);
        Assert.NotEmpty(first.Calls);
        Assert.Equal(first.Calls, second.Calls);
    }

    [Fact]
    public async Task StalledRunDrainAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var first = await fixture.StartAsync("10");
        var second = await fixture.StartAsync("20");
        first.LastProgressTicksUtc = DateTime.UtcNow.AddMinutes(-10).Ticks;
        fixture.Client.CompleteCancellation = false;
        Assert.True(await fixture.Daemon.FailStalledSessionAsync(fixture.Session, first.PrefillRunId,
            DateTime.UtcNow, TimeSpan.FromMinutes(3)));
        Assert.False(first.Completion.Task.IsCompleted);
        Assert.False(second.CancelRequested);
        fixture.Client.Set(first, "canceled", 5, "canceled");
        await fixture.RefreshAsync();
        Assert.Equal("failed", (await first.Completion.Task).Snapshot.State);
        Assert.Equal("stalled", first.Snapshot.Reason);
        Assert.Equal("signalr.scheduledPrefill.failedStalled", first.ErrorStageKey);
    }

    [Fact]
    public async Task WatchdogOwnershipAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var run = await fixture.StartAsync("10");
        await run.PrefillWork.WaitAsync();
        run.LastProgressTicksUtc = DateTime.UtcNow.AddMinutes(-10).Ticks;
        var stalled = fixture.Daemon.FailStalledSessionAsync(fixture.Session, run.PrefillRunId,
            DateTime.UtcNow, TimeSpan.FromMinutes(3));
        Assert.False(stalled.IsCompleted);
        run.LastProgressTicksUtc = DateTime.UtcNow.Ticks;
        run.PrefillWork.Release();
        Assert.False(await stalled);
        Assert.Empty(fixture.Client.Canceled);
        Assert.False(run.CancelRequested);
    }

    [Fact]
    public async Task AuthenticationFanoutAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var first = await fixture.StartAsync("10");
        var second = await fixture.StartAsync("20");
        var third = await fixture.StartAsync("30");
        await DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "ApplyStatusAsync", fixture.Session,
            new DaemonStatus { Status = "awaiting-login" }, null);
        Assert.Equal(3, fixture.Client.Canceled.Count);
        foreach (var run in new[] { first, second, third })
        {
            Assert.Equal("failed", (await run.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5))).Snapshot.State);
            Assert.Equal("auth-lost", run.Snapshot.Reason);
            Assert.True(run.LastProgress!.RequiresLogin);
        }
        Assert.DoesNotContain(fixture.Session.Runs.Values, run => run.TerminalCompletedFlag != 2);
    }

    [Fact]
    public async Task UserCancelStaysCanceledWhenSignOutLandsDuringCancelAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var run = await fixture.StartAsync("10");
        fixture.Client.CompleteCancellation = false;
        await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, true);
        await DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "ApplyStatusAsync", fixture.Session,
            new DaemonStatus { Status = "awaiting-login" }, null);
        Assert.Null(run.CancelReason);
        fixture.Client.Set(run, "canceled", 5, "canceled");
        await fixture.RefreshAsync();
        Assert.Equal("canceled", (await run.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5))).Snapshot.State);
        Assert.Null(run.Snapshot.Reason);
        await using var context = new AppDbContext(fixture.Options);
        var saved = await context.PrefillRuns.SingleAsync(row => row.Id == run.PrefillRunId);
        Assert.Equal("canceled", saved.State);
        Assert.Null(saved.Reason);
    }

    [Fact]
    public async Task EachHistorySaveThatChangedAGameSendsTheHistoryUpdate()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var run = await fixture.StartAsync("10");
        List<(string Method, object?[] Args)> Updates() => fixture.Recorder.Invocations.Where(call =>
            call.Method == nameof(ISignalRNotificationService.NotifyAllAsync)
            && call.Args.Length > 1 && call.Args[0] as string == SignalREvents.PrefillHistoryUpdated).ToList();
        var before = Updates().Count;

        fixture.Client.Set(run, "downloading", 50, "downloading");
        await fixture.RefreshAsync();
        var afterChange = Updates();
        Assert.Equal(before + 1, afterChange.Count);
        var historyEvent = afterChange[^1].Args[1]!;
        Assert.Equal(fixture.Session.Id, historyEvent.GetType().GetProperty("sessionId")!.GetValue(historyEvent));

        await fixture.RefreshAsync();
        Assert.Equal(before + 1, Updates().Count);

        fixture.Client.Set(run, "completed", 100, "success");
        await fixture.RefreshAsync();
        Assert.Equal(before + 2, Updates().Count);
    }

    [Fact]
    public async Task AStatusWithoutProtocolTwoRefusesStartWithTheOutdatedKey()
    {
        await using var fixture = await RunFixture.CreateAsync();
        fixture.Session.Capabilities = null;
        fixture.Client.Status = new DaemonStatus { Status = "logged-in" };

        var refusal = await Assert.ThrowsAsync<DaemonCommandException>(
            () => fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]));

        Assert.Equal("daemon-outdated", refusal.ErrorCode);
        Assert.Equal("errors.prefill.daemonOutdated", refusal.StageKey);
        Assert.Empty(fixture.Session.Runs);
        Assert.Equal(DaemonSessionStatus.Active, fixture.Session.Status);
        Assert.Equal(0, fixture.Client.StartCount);
    }

    [Fact]
    public async Task AFailedFirstStatusReadRefusesStartWithoutARun()
    {
        await using var fixture = await RunFixture.CreateAsync();
        fixture.Session.Capabilities = null;
        fixture.Client.Offline = true;

        var refusal = await Assert.ThrowsAsync<DaemonCommandException>(
            () => fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]));

        Assert.Equal("errors.prefill.requestFailed", refusal.StageKey);
        Assert.Empty(fixture.Session.Runs);
        Assert.Equal(0, fixture.Client.StartCount);
    }

    [Fact]
    public async Task AnOldStatusOnceReadRefusesLaterStartsWithoutReadingAgain()
    {
        await using var fixture = await RunFixture.CreateAsync();
        fixture.Session.Capabilities = null;
        await fixture.Daemon.RecoverRunsAsync(fixture.Session, new DaemonStatus { Status = "logged-in" }, CancellationToken.None);
        var reads = 0;
        fixture.Client.StatusRead = () =>
        {
            reads++;
            return Task.FromResult<DaemonStatus?>(FreshStatus(fixture));
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var refusal = await Assert.ThrowsAsync<DaemonCommandException>(
                () => fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]));
            Assert.Equal("daemon-outdated", refusal.ErrorCode);
        }

        Assert.Equal(0, reads);
        Assert.False(fixture.Session.Recovering);
        Assert.Empty(fixture.Session.Runs);
    }

    [Fact]
    public async Task AFirstStartBeforeAnyStatusReadStartsARun()
    {
        await using var fixture = await RunFixture.CreateAsync();
        fixture.Session.Capabilities = null;
        // A started run schedules its own background refresh, so only the reads made before the run exists count.
        var readsBeforeTheRun = 0;
        fixture.Client.StatusRead = () =>
        {
            if (fixture.Session.Runs.IsEmpty) readsBeforeTheRun++;
            return Task.FromResult<DaemonStatus?>(FreshStatus(fixture));
        };

        var started = await fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);

        Assert.Equal(1, readsBeforeTheRun);
        Assert.NotNull(fixture.Session.Capabilities);
        Assert.Equal(started.RunId, Assert.Single(fixture.Session.Runs.Values).PrefillRunId);
    }

    [Fact]
    public async Task AFirstStartWhoseReadLeavesNoCapabilitiesFailsAsARequestFailure()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var session = fixture.Session;
        session.Capabilities = null;
        var answer = new TaskCompletionSource<DaemonStatus?>();
        fixture.Client.StatusRead = () => answer.Task;
        await session.RecoveryWork.WaitAsync();
        Task<PrefillResult> start;
        try
        {
            start = fixture.Daemon.PrefillAsync(session.Id, appIds: ["10"]);
            answer.SetResult(FreshStatus(fixture));

            // Another pass holds the lease and applies its own read, so this Start has to wait for it.
            Assert.False(start.IsCompleted);
        }
        finally { session.RecoveryWork.Release(); }

        var refusal = await Assert.ThrowsAsync<DaemonCommandException>(() => start.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal("errors.prefill.requestFailed", refusal.StageKey);
        Assert.Empty(session.Runs);
    }

    [Fact]
    public async Task AFirstStartAfterAnImageReplaceClosedAdmissionIsBusy()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var session = fixture.Session;
        session.Capabilities = null;
        await session.RecoveryWork.WaitAsync();
        Task<PrefillResult> start;
        try
        {
            start = fixture.Daemon.PrefillAsync(session.Id, appIds: ["10"]);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!session.RecoveryRequested && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.True(session.RecoveryRequested, "The Start never reached its wait for the recovery lease.");
            // An image replace closes admission while it holds the lease, and releases it without a read.
            session.AdmissionClosed = true;
        }
        finally { session.RecoveryWork.Release(); }

        var refusal = await Assert.ThrowsAsync<DaemonCommandException>(() => start.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal("outcome-unknown", refusal.ErrorCode);
        Assert.Empty(session.Runs);
    }

    [Fact]
    public async Task AFirstStartAppliesTheSignInStateItReads()
    {
        await using var fixture = await RunFixture.CreateAsync();
        fixture.Session.AuthState = DaemonAuthState.NotAuthenticated;
        fixture.Session.Capabilities = null;

        await fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);

        Assert.Single(fixture.Session.Runs);
        Assert.Equal(DaemonAuthState.Authenticated, fixture.Session.AuthState);
    }

    [Fact]
    public async Task AFailedStatusReadOnAnIdleSessionLeavesItReady()
    {
        await using var fixture = await RunFixture.CreateAsync();
        fixture.Client.Offline = true;

        Assert.Null(await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id));

        Assert.False(fixture.Session.Recovering);
        fixture.Client.Offline = false;
        Assert.NotNull((await fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"])).RunId);
    }

    private static DaemonStatus FreshStatus(RunFixture fixture)
    {
        var status = RunClient.Capabilities(fixture.Client.InstanceId);
        status.ReadStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        return status;
    }

    [Theory]
    [InlineData("legacy", false, null)]
    [InlineData("partial", false, null)]
    [InlineData("malformed-instance", false, null)]
    [InlineData("missing-instance", false, null)]
    [InlineData("missing-inventory", false, null)]
    [InlineData("invalid-run-limit", false, null)]
    [InlineData("invalid-request-limit", false, null)]
    [InlineData("valid-limit-one", true, 1)]
    [InlineData("valid", true, 3)]
    public async Task SessionCapabilitiesAsync(string scenario, bool concurrent, int? expectedLimit)
    {
        foreach (var persistent in new[] { false, true })
        {
            await using var fixture = await RunFixture.CreateAsync(persistent: persistent);
            var status = RunClient.Capabilities(fixture.Client.InstanceId);
            switch (scenario)
            {
                case "legacy": status = new DaemonStatus(); break;
                case "partial": status.Features!.Remove("targetedCancel"); break;
                case "malformed-instance": status.DaemonInstanceId = "not-a-guid"; break;
                case "missing-instance": status.DaemonInstanceId = null; break;
                case "missing-inventory": status.ActiveOperations = null; break;
                case "invalid-run-limit": status.MaxConcurrentRuns = 17; break;
                case "invalid-request-limit": status.MaxConcurrentRequests = 0; break;
                case "valid-limit-one": status.MaxConcurrentRuns = 1; break;
            }
            fixture.Session.Capabilities = status;
            fixture.Client.Status = status;
            Assert.Equal(concurrent, status.SupportsConcurrentPrefill);

            IReadOnlyList<string> features;
            int? limit;
            if (persistent)
            {
                using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
                var controller = new PersistentPrefillController(services, null!, null!,
                    NullLogger<PersistentPrefillController>.Instance);
                var result = await controller.ListAsync(CancellationToken.None);
                var response = Assert.Single(Assert.IsType<List<PersistentPrefillSessionDto>>(
                    Assert.IsType<OkObjectResult>(result.Result).Value));
                features = response.Features;
                limit = response.MaxConcurrentRuns;
            }
            else
            {
                var response = DaemonSessionDto.FromSession(fixture.Session);
                features = response.Features;
                limit = response.MaxConcurrentRuns;
            }
            Assert.Equal(expectedLimit, limit);
            if (concurrent)
            {
                Assert.Equal(status.Features, features);
                Assert.NotSame(status.Features, features);
            }
            else
                Assert.Empty(features);
        }
    }

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ARefusedStartNamesItsReasonByKeyAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        // Every run slot the daemon offers is taken.
        await fixture.StartAsync("10");
        await fixture.StartAsync("20");
        await fixture.StartAsync("30");
        var controller = new SteamDaemonController(fixture.Daemon,
            NullLogger<SteamDaemonController>.Instance, stateService: null!, userPreferencesService: null!);
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Items["Session"] = new UserSession { Id = fixture.Session.UserId, SessionType = SessionType.Admin };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var error = await Assert.ThrowsAsync<LancacheManager.Middleware.ConflictException>(
            () => controller.StartPrefillAsync(fixture.Session.Id, new StartPrefillRequest { All = true }));

        Assert.Equal("prefill.log.alreadyRunning", error.StageKey);
    }

    [Fact]
    public async Task ABrowserCancelAnswersWhileTheDaemonsOperationReadIsHeldAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var client = Script(fixture);
        var run = await fixture.StartAsync("10");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PageHold = hold;

        Assert.True(await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, false).WaitAsync(Bound));
        Assert.False(run.Completion.Task.IsCompleted);

        hold.SetResult();
        Assert.Equal("canceled", (await run.Completion.Task.WaitAsync(Bound)).Snapshot.State);
    }

    [Fact]
    public async Task ABrowserCancelDuringShutdownAnswersTrueWhenTheSessionSourceIsDisposedAsync()
    {
        // Not disposed at the end: the test disposes the session's own token source, as the shutdown does.
        var fixture = await RunFixture.CreateAsync();
        var client = Script(fixture);
        var run = await fixture.StartAsync("10");
        client.AfterCancel = _ =>
        {
            fixture.Session.CancellationTokenSource.Dispose();
            return Task.CompletedTask;
        };

        Assert.True(await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, false).WaitAsync(Bound));
    }

    [Fact]
    public async Task ALogoutWhoseOperationReadFailsIsNotConfirmedAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var client = Script(fixture);
        await fixture.StartAsync("10");
        client.ReadPage = (_, _) => throw new JsonException("Operation page has a different run identity.");

        var result = await fixture.Daemon.LogoutPersistentSessionAsync(fixture.Session.Id).WaitAsync(Bound);

        Assert.False(result.LoggedOut);
        Assert.Equal(0, client.Logouts);
    }

    [Fact]
    public async Task ALogoutWhileTheSessionRecoversAnswersNotConfirmedAtOnceAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var client = Script(fixture);
        var run = await fixture.StartAsync("10");
        fixture.Session.Recovering = true;

        var result = await fixture.Daemon.LogoutPersistentSessionAsync(fixture.Session.Id).WaitAsync(Bound);

        Assert.False(result.LoggedOut);
        Assert.Equal(0, client.Logouts);
        Assert.Empty(client.Canceled);
        Assert.False(run.Completion.Task.IsCompleted);
    }

    [Fact]
    public async Task ALogoutAnswersNotConfirmedWhenTheDaemonDropsWhileItWaitsForTheRunAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var client = Script(fixture);
        client.CompleteCancellation = false;
        await fixture.StartAsync("10");
        var cancelSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.AfterCancel = _ =>
        {
            cancelSent.TrySetResult();
            return Task.CompletedTask;
        };

        var logout = fixture.Daemon.LogoutPersistentSessionAsync(fixture.Session.Id);
        await cancelSent.Task.WaitAsync(Bound);
        fixture.Session.Recovering = true;

        Assert.False((await logout.WaitAsync(TimeSpan.FromSeconds(5))).LoggedOut);
        Assert.Equal(0, client.Logouts);
    }

    [Fact]
    public async Task ALogoutDuringAResetThatRemovedAFinishedRunStillSignsOutAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var client = Script(fixture);
        await fixture.StartAsync("10");
        await fixture.StartAsync("20");
        // The logout lists runs in this order, so the reset lands between its listing and the second run's cancel.
        var listed = fixture.Session.Runs.Values.ToArray();
        var removed = listed[1];
        // Its terminal row is saved and its finish is still being published, which is when a reset deletes it.
        removed.TerminalCompletedFlag = 1;
        client.AfterCancel = async id =>
        {
            if (id == listed[0].PrefillRunId)
                await fixture.Daemon.ForgetFinishedRunsAsync([removed.PrefillRunId]);
        };

        var result = await fixture.Daemon.LogoutPersistentSessionAsync(fixture.Session.Id).WaitAsync(Bound);

        Assert.True(result.LoggedOut);
        Assert.Equal(1, client.Logouts);
    }

    [Fact]
    public async Task ARecordedCancelReachesTheDaemonWhenItLandedBeforeTheStartWasSentAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        Assert.True(fixture.Daemon.PersistentChangeGate.TryEnterChange(out var change));
        var start = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
        Assert.True(SpinWait.SpinUntil(() => !fixture.Session.Runs.IsEmpty, Bound));
        var run = Assert.Single(fixture.Session.Runs.Values);
        // What a cancel that claimed the run's work before its Start leaves: the cancel recorded on the run, and
        // the daemon, which had no such run yet, refused it.
        run.CancelRequested = true;
        await change!.DisposeAsync();

        await start.WaitAsync(Bound);
        await fixture.RefreshAsync();
        await fixture.RefreshAsync();

        Assert.Contains(run.PrefillRunId, fixture.Client.Canceled);
        Assert.Equal("canceled", (await run.Completion.Task.WaitAsync(Bound)).Snapshot.State);
    }

    [Fact]
    public async Task ACancelRecordedWhileTheDaemonReportsNoConcurrentRunsIsSentOnTheNextReadAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var run = await fixture.StartAsync("10");
        var reduced = RunClient.Capabilities(fixture.Client.InstanceId);
        reduced.Features = ["operationProgress", "inlineSelection", "targetedCancel", "activeOperations"];
        // A read taken now, so the recovery pass does not discard it as older than the one it applied.
        reduced.ReadStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        fixture.Client.Status = reduced;
        await fixture.RefreshAsync();

        Assert.False(await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, true,
            reason: "runtime-exceeded"));
        Assert.Empty(fixture.Client.Canceled);

        fixture.Client.Status = null;
        await fixture.RefreshAsync();
        await fixture.RefreshAsync();

        Assert.Equal([run.PrefillRunId], fixture.Client.Canceled);
        Assert.Equal("failed", (await run.Completion.Task.WaitAsync(Bound)).Snapshot.State);
        Assert.False(fixture.Session.Recovering);
    }

    [Fact]
    public async Task ACancelWithoutARunIdRacingAStartOnAnIdleSessionAnswersBothAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        Task<PrefillResult>? start = null;
        // The cancel finds no run, takes the change gate, and reads the daemon again; a Start lands right then.
        var services = new HookedDaemonLookup(fixture.Daemon, lookup =>
        {
            if (lookup != 2) return;
            start = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
            Assert.True(SpinWait.SpinUntil(() => fixture.Session.Runs.Values.Any(run => run.PrefillWork.CurrentCount == 0), Bound));
        });
        var controller = new PersistentPrefillController(services,
            (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>(),
            new PrefillCacheService(new TestDbContextFactory(fixture.Options), NullLogger<PrefillCacheService>.Instance),
            NullLogger<PersistentPrefillController>.Instance);

        var cancel = controller.CancelPrefillAsync(new PersistentServiceRequest { Service = PrefillPlatform.Steam },
            CancellationToken.None);

        Assert.IsType<OkResult>(await cancel.WaitAsync(Bound));
        await start!.WaitAsync(Bound);
        Assert.True(fixture.Daemon.PersistentChangeGate.TryEnterChange(out var free));
        await free!.DisposeAsync();
    }

    [Fact]
    public async Task AStartWaitingBehindASignInWhenTheDaemonReportsSignedOutAnswersAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        Assert.True(fixture.Daemon.PersistentChangeGate.TryEnterChange(out var change));
        var start = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
        Assert.True(SpinWait.SpinUntil(() => fixture.Session.Runs.Values.Any(run => run.PrefillWork.CurrentCount == 0), Bound));
        var signedOut = RunClient.Capabilities(fixture.Client.InstanceId);
        signedOut.Status = "not-logged-in";
        fixture.Client.Status = signedOut;

        // The sign-in route reads the daemon while it holds the gate (PersistentPrefillController StartLoginAsync).
        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await change!.DisposeAsync();

        var refused = await Assert.ThrowsAsync<DaemonCommandException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("auth-lost", refused.ErrorCode);
        Assert.True(fixture.Daemon.PersistentChangeGate.TryEnterChange(out var free));
        await free!.DisposeAsync();
    }

    [Fact]
    public async Task ALogoutWhoseRunNeverEndsAnswersNotConfirmedAfterTheStallTimeoutAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        UseStallTimeout(fixture.Daemon, 2);
        var client = Script(fixture);
        client.CompleteCancellation = false;
        var run = await fixture.StartAsync("10");
        var clock = Stopwatch.StartNew();

        var result = await fixture.Daemon.LogoutPersistentSessionAsync(fixture.Session.Id).WaitAsync(Bound);

        Assert.False(result.LoggedOut);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        Assert.Equal(0, client.Logouts);
        Assert.Single(client.Canceled);
        Assert.False(run.Completion.Task.IsCompleted);
        Assert.True(fixture.Daemon.IsSessionLive(fixture.Session));
        Assert.False(fixture.Session.AdmissionClosed);
    }

    [Fact]
    public async Task ALogoutWaitsForACanceledRunThatKeepsDownloadingAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        UseStallTimeout(fixture.Daemon, 2);
        var client = Script(fixture);
        client.CompleteCancellation = false;
        var run = await fixture.StartAsync("10");

        var logout = fixture.Daemon.LogoutPersistentSessionAsync(fixture.Session.Id);
        for (var bytes = 10; bytes <= 40; bytes += 10)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.False(logout.IsCompleted);
            client.Set(run, "downloading", bytes, "downloading");
            await fixture.RefreshAsync();
        }
        client.Set(run, "canceled", 40, "canceled");
        await fixture.RefreshAsync();

        Assert.True((await logout.WaitAsync(Bound)).LoggedOut);
        Assert.Equal(1, client.Logouts);
    }

    [Fact]
    public async Task ALogoutWaitsForARunEndThatIsStillBeingPublishedAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        UseStallTimeout(fixture.Daemon, 2);
        var client = Script(fixture);
        client.CompleteCancellation = false;
        var run = await fixture.StartAsync("10");
        // The end of a run that downloaded a game records the game first, so a held cache write holds the publish.
        var cache = new BlockingDbContexts(fixture.Options);
        SetField(fixture.Daemon, "_cacheService", new PrefillCacheService(cache, NullLogger<PrefillCacheService>.Instance));
        var clock = Stopwatch.StartNew();
        var logout = fixture.Daemon.LogoutPersistentSessionAsync(fixture.Session.Id);
        Assert.True(SpinWait.SpinUntil(() => client.Canceled.Count > 0, Bound));

        client.Set(run, "completed", 100, "success");
        var page = client.Pages[run.PrefillRunId];
        client.Pages[run.PrefillRunId] = page with
        {
            Items = [page.Items[0] with
            {
                Depots = [new DepotManifestProgressInfo { DepotId = 11, ManifestId = 12, TotalBytes = 100 }]
            }]
        };
        var end = fixture.RefreshAsync();
        try
        {
            await cache.FirstCreateStarted.Task.WaitAsync(Bound);
            Assert.Equal(1, Volatile.Read(ref run.TerminalCompletedFlag));
            await Task.Delay(TimeSpan.FromSeconds(4) - clock.Elapsed);
            Assert.False(logout.IsCompleted);
        }
        finally
        {
            // The held write keeps the session's recovery lock, which the fixture's dispose waits for.
            cache.ReleaseFirstCreate.TrySetResult(true);
        }
        await end.WaitAsync(Bound);

        Assert.True((await logout.WaitAsync(Bound)).LoggedOut);
        Assert.Equal(1, client.Logouts);
    }

    [Fact]
    public async Task ALogoutWaitsForARunEndThatIsStillBeingSavedAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        UseStallTimeout(fixture.Daemon, 2);
        var client = Script(fixture);
        client.CompleteCancellation = false;
        var run = await fixture.StartAsync("10");
        var history = new HeldRunEndSave(fixture.Options, run);
        SetField(fixture.Daemon, "_sessionService", new PrefillSessionService(history, NullLogger<PrefillSessionService>.Instance));
        var clock = Stopwatch.StartNew();
        var logout = fixture.Daemon.LogoutPersistentSessionAsync(fixture.Session.Id);
        Assert.True(SpinWait.SpinUntil(() => client.Canceled.Count > 0, Bound));

        client.Set(run, "canceled", 0, "canceled");
        var end = fixture.RefreshAsync();
        try
        {
            await history.Entered.Task.WaitAsync(Bound);
            await Task.Delay(TimeSpan.FromSeconds(4) - clock.Elapsed);
            Assert.False(logout.IsCompleted);
        }
        finally
        {
            // The held save keeps the session's recovery lock, which the fixture's dispose waits for.
            history.Release.TrySetResult();
        }
        await end.WaitAsync(Bound);

        Assert.True((await logout.WaitAsync(Bound)).LoggedOut);
        Assert.True(run.Completion.Task.IsCompleted);
        Assert.Equal(1, client.Logouts);
    }

    [Fact]
    public async Task ALogoutWhoseRunEndSaveIsNotAppliedTakesTheStallExitAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        UseStallTimeout(fixture.Daemon, 2);
        var client = Script(fixture);
        client.CompleteCancellation = false;
        var run = await fixture.StartAsync("10");
        var logout = fixture.Daemon.LogoutPersistentSessionAsync(fixture.Session.Id);
        Assert.True(SpinWait.SpinUntil(() => client.Canceled.Count > 0, Bound));
        // A stored row that already holds an end makes the terminal save answer not applied.
        await using (var context = new AppDbContext(fixture.Options))
        {
            var row = await context.PrefillRuns.SingleAsync(saved => saved.Id == run.PrefillRunId);
            row.CompletedAtUtc = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        client.Set(run, "canceled", 0, "canceled");
        await fixture.RefreshAsync();

        Assert.Equal(0, Volatile.Read(ref run.TerminalSavePending));
        Assert.False(run.Completion.Task.IsCompleted);
        Assert.False((await logout.WaitAsync(Bound)).LoggedOut);
        Assert.Equal(0, client.Logouts);
    }

    [Fact]
    public async Task ALogoutWhoseSessionIsReadoptedLeavesTheNewSessionOpenAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        UseStallTimeout(fixture.Daemon, 2);
        var client = Script(fixture);
        client.CompleteCancellation = false;
        await fixture.StartAsync("10");
        var old = fixture.Session;
        var logout = fixture.Daemon.LogoutPersistentSessionAsync(old.Id);
        Assert.True(SpinWait.SpinUntil(() => client.Canceled.Count > 0, Bound));

        // A re-adopt registers a new session object under the same id.
        var readoptedClient = (RunClient)(object)DispatchProxy.Create<IDaemonClient, RunClient>();
        var readopted = new DaemonSession
        {
            Id = old.Id,
            UserId = old.UserId,
            IsPersistent = true,
            Status = DaemonSessionStatus.Active,
            AuthState = DaemonAuthState.Authenticated,
            CreatedAt = old.CreatedAt,
            ExpiresAt = old.ExpiresAt,
            Client = (IDaemonClient)(object)readoptedClient,
            Capabilities = RunClient.Capabilities(readoptedClient.InstanceId)
        };
        ((TestableSteamDaemonService)fixture.Daemon).InjectSession(readopted);

        Assert.False((await logout.WaitAsync(Bound)).LoggedOut);
        Assert.True(old.AdmissionClosed);
        Assert.False(old.LoginSettled);
        var started = await fixture.Daemon.PrefillAsync(old.Id, appIds: ["20"], notificationMode: "silent").WaitAsync(Bound);
        Assert.NotNull(started.RunId);
        Assert.Equal(1, readoptedClient.StartCount);
    }

    [Fact]
    public async Task ACanceledRunsEndThatArrivesDuringTheCancelsReadEndsTheRunAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var client = Script(fixture);
        var run = await fixture.StartAsync("10");
        client.CompleteCancellation = false;
        client.Set(run, "canceling", 0, "downloading");
        var canceling = client.Pages[run.PrefillRunId];
        client.Set(run, "canceled", 0, "canceled");
        var canceled = client.Pages[run.PrefillRunId];
        var reads = 0;
        client.ReadPage = (_, _) => Interlocked.Increment(ref reads) == 1 ? canceling : canceled;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PageHold = hold;

        Assert.True(await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, false).WaitAsync(Bound));
        Assert.True(SpinWait.SpinUntil(() => run.PrefillWork.CurrentCount == 0, Bound));
        await RaiseProgressAsync(fixture, run, canceled.Operation.Sequence);
        hold.SetResult();

        Assert.Equal("canceled", (await run.Completion.Task.WaitAsync(Bound)).Snapshot.State);
    }

    [Fact]
    public async Task ARunEndThatArrivesDuringAStallCheckOfAFreshRunEndsTheRunAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var run = await fixture.StartAsync("10");
        await run.PrefillWork.WaitAsync();
        fixture.Client.Set(run, "canceled", 0, "canceled");
        await RaiseProgressAsync(fixture, run, fixture.Client.Pages[run.PrefillRunId].Operation.Sequence);
        var stalled = fixture.Daemon.FailStalledSessionAsync(fixture.Session, run.PrefillRunId, DateTime.UtcNow,
            TimeSpan.FromHours(1));
        run.PrefillWork.Release();

        Assert.False(await stalled.WaitAsync(Bound));
        Assert.Equal("canceled", (await run.Completion.Task.WaitAsync(Bound)).Snapshot.State);
    }

    [Fact]
    public async Task ARunEndThatArrivesDuringACancelWhoseRecordFailsEndsTheRunAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var client = Script(fixture);
        var run = await fixture.StartAsync("10");
        var switchable = new SwitchableDbContextFactory();
        switchable.Recover(new TestDbContextFactory(fixture.Options));
        SetField(fixture.Daemon, "_sessionService", new PrefillSessionService(switchable, NullLogger<PrefillSessionService>.Instance));
        await run.PrefillWork.WaitAsync();
        client.Set(run, "canceled", 0, "canceled");
        await RaiseProgressAsync(fixture, run, client.Pages[run.PrefillRunId].Operation.Sequence);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PageHold = hold;
        switchable.Fail();

        var cancel = fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, false);
        run.PrefillWork.Release();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cancel.WaitAsync(Bound));
        switchable.Recover(new TestDbContextFactory(fixture.Options));
        hold.SetResult();

        Assert.Equal("canceled", (await run.Completion.Task.WaitAsync(Bound)).Snapshot.State);
    }

    [Fact]
    public async Task ACancelDuringAdmissionAnswersAtOnceAndIsSentOnceAdmittedAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var client = Script(fixture);
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PrefillHold = admission;
        var start = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
        await client.PrefillEntered.Task.WaitAsync(Bound);
        var run = Assert.Single(fixture.Session.Runs.Values);
        using var stopWaiting = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        Assert.False(await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, false,
            stopWaiting.Token).WaitAsync(Bound));
        // A later cancel with its own reason must not replace the first one's.
        Assert.False(await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, false,
            CancellationToken.None, "runtime-exceeded").WaitAsync(Bound));
        // A recovery pass resends every recorded cancel, so the one that follows the Start is held off to count
        // only the cancel the Start sends.
        await fixture.Session.RecoveryWork.WaitAsync();
        try
        {
            admission.SetResult();
            await start.WaitAsync(Bound);

            Assert.Equal("canceled", (await run.Completion.Task.WaitAsync(Bound)).Snapshot.State);
        }
        finally { fixture.Session.RecoveryWork.Release(); }
        Assert.Equal([run.PrefillRunId], client.Canceled);
        Assert.Null(run.CancelReason);
    }

    [Fact]
    public async Task ACancelDuringAdmissionWhoseRecordFailsStillReachesTheDaemonAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var client = Script(fixture);
        var switchable = new SwitchableDbContextFactory();
        switchable.Recover(new TestDbContextFactory(fixture.Options));
        SetField(fixture.Daemon, "_sessionService", new PrefillSessionService(switchable, NullLogger<PrefillSessionService>.Instance));
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PrefillHold = admission;
        var start = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
        // The run's row is saved before the daemon is asked to admit it.
        await client.PrefillEntered.Task.WaitAsync(Bound);
        var run = Assert.Single(fixture.Session.Runs.Values);

        Assert.False(await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, false).WaitAsync(Bound));
        switchable.Fail();
        // Held off, the recovery pass that follows the Start neither fails on the outage nor sends the cancel itself.
        await fixture.Session.RecoveryWork.WaitAsync();
        try
        {
            admission.SetResult();
            await start.WaitAsync(Bound);
            Assert.True(SpinWait.SpinUntil(() => client.Canceled.Count > 0, Bound));
        }
        finally { fixture.Session.RecoveryWork.Release(); }
        switchable.Recover(new TestDbContextFactory(fixture.Options));

        Assert.Equal([run.PrefillRunId], client.Canceled);
        Assert.True(run.CancelRequested);
    }

    [Fact]
    public async Task ACancelDuringAdmissionOfAStartThatEndsBeforeItsRunIsSavedDropsTheRunAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        Assert.True(fixture.Daemon.PersistentChangeGate.TryEnterChange(out var change));
        var start = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
        Assert.True(SpinWait.SpinUntil(() => fixture.Session.Runs.Values.Any(run => run.PrefillWork.CurrentCount == 0), Bound));
        var run = Assert.Single(fixture.Session.Runs.Values);

        Assert.False(await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, false).WaitAsync(Bound));
        fixture.Session.AuthState = DaemonAuthState.NotAuthenticated;
        await change!.DisposeAsync();

        var refused = await Assert.ThrowsAsync<DaemonCommandException>(() => start.WaitAsync(Bound));
        Assert.Equal("auth-lost", refused.ErrorCode);
        Assert.Equal("canceled", (await run.Completion.Task.WaitAsync(Bound)).Snapshot.State);
        Assert.False(fixture.Session.Runs.ContainsKey(run.PrefillRunId));
        bool EndedPush((string Method, object?[] Args) call) =>
            call.Method == nameof(ISignalRNotificationService.NotifyAdminAsync)
            && call.Args.Length > 1 && call.Args[1] is DaemonSessionDto session
            && session.Runs.Any(listed => listed.RunId == run.PrefillRunId && listed.Snapshot.State == "canceled");
        Assert.True(SpinWait.SpinUntil(() => fixture.Recorder.Invocations.ToArray().Any(EndedPush), Bound));
        Assert.Single(fixture.Recorder.Invocations.ToArray(), EndedPush);
        Assert.Empty(fixture.Client.Canceled);
    }

    [Fact]
    public async Task ALogoutDuringAdmissionOfAStartThatEndsBeforeItsRunIsSavedSignsOutAtOnceAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var client = Script(fixture);
        Assert.True(fixture.Daemon.PersistentChangeGate.TryEnterChange(out var change));
        var start = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
        Assert.True(SpinWait.SpinUntil(() => fixture.Session.Runs.Values.Any(run => run.PrefillWork.CurrentCount == 0), Bound));
        var run = Assert.Single(fixture.Session.Runs.Values);

        var logout = fixture.Daemon.LogoutPersistentSessionAsync(fixture.Session.Id);
        Assert.True(SpinWait.SpinUntil(() => run.CancelAfterAdmission, Bound));
        await change!.DisposeAsync();

        var refused = await Assert.ThrowsAsync<DaemonCommandException>(() => start.WaitAsync(Bound));
        Assert.Equal("instance-changed", refused.ErrorCode);
        Assert.True((await logout.WaitAsync(Bound)).LoggedOut);
        Assert.Equal(1, client.Logouts);
    }

    [Theory]
    [InlineData(null, "outcome-unknown")]
    [InlineData("runtime-exceeded", "runtime-exceeded")]
    [InlineData("stalled", "stalled")]
    public async Task ACancelTheDaemonNeverEndsEndsOneStallTimeoutLaterAsync(string? reason, string expected)
    {
        await using var fixture = await RunFixture.CreateAsync();
        UseStallTimeout(fixture.Daemon, 120);
        fixture.Client.CompleteCancellation = false;
        var run = await fixture.StartAsync("10");

        await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, true, reason: reason);
        await fixture.Daemon.ProcessSessionExpiryAsync(DateTime.UtcNow);
        Assert.False(run.Completion.Task.IsCompleted);
        await fixture.Daemon.ProcessSessionExpiryAsync(DateTime.UtcNow + TimeSpan.FromSeconds(120) + TimeSpan.FromMinutes(1));

        var ended = await run.Completion.Task.WaitAsync(Bound);
        Assert.Equal("failed", ended.Snapshot.State);
        Assert.Equal(expected, ended.Snapshot.Reason);
    }

    [Fact]
    public async Task ACancelRestoredAfterARestartThatTheDaemonNeverEndsEndsOneStallTimeoutLaterAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        UseStallTimeout(fixture.Daemon, 120);
        fixture.Client.CompleteCancellation = false;
        var run = await fixture.StartAsync("10");
        await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, true);
        fixture.Client.Set(run, "canceling", 0, "downloading");
        // A restart keeps only the saved row, which holds the cancel and no end.
        Assert.True(fixture.Session.Runs.TryRemove(run.PrefillRunId, out _));

        await fixture.Daemon.RecoverRunsAsync(fixture.Session, FreshStatus(fixture), CancellationToken.None);
        var restored = Assert.IsType<DaemonRun>(fixture.Daemon.GetRun(fixture.Session.Id, run.PrefillRunId));
        Assert.NotSame(run, restored);
        Assert.True(restored.CancelRequested);
        await fixture.Daemon.ProcessSessionExpiryAsync(DateTime.UtcNow + TimeSpan.FromSeconds(120) + TimeSpan.FromMinutes(1));

        var ended = await restored.Completion.Task.WaitAsync(Bound);
        Assert.Equal("failed", ended.Snapshot.State);
        Assert.Equal("outcome-unknown", ended.Snapshot.Reason);
    }

    [Fact]
    public async Task ARefusedStartWhoseSaveFailsStillAnswersWithTheRefusalAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var client = Script(fixture);
        client.StartRefusal = "operation-conflict";
        var switchable = new SwitchableDbContextFactory();
        switchable.Recover(new TestDbContextFactory(fixture.Options));
        SetField(fixture.Daemon, "_sessionService", new PrefillSessionService(switchable, NullLogger<PrefillSessionService>.Instance));
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PrefillHold = admission;
        var start = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
        // The run's row is saved before the daemon is asked to admit it.
        Assert.True(SpinWait.SpinUntil(() => switchable.CreateCount > 0, Bound));

        switchable.Fail();
        admission.SetResult();
        var refused = await Assert.ThrowsAsync<DaemonCommandException>(() => start.WaitAsync(Bound));
        switchable.Recover(new TestDbContextFactory(fixture.Options));

        Assert.Equal("operation-conflict", refused.ErrorCode);
    }

    [Fact]
    public async Task ACancelDuringAdmissionOfARefusedStartIsNeverSentAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var client = Script(fixture);
        client.StartRefusal = "operation-conflict";
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PrefillHold = admission;
        var start = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
        await client.PrefillEntered.Task.WaitAsync(Bound);
        var run = Assert.Single(fixture.Session.Runs.Values);

        Assert.False(await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, false).WaitAsync(Bound));
        admission.SetResult();

        await Assert.ThrowsAsync<DaemonCommandException>(() => start.WaitAsync(Bound));
        await fixture.RefreshAsync();
        Assert.Empty(client.Canceled);
    }

    [Fact]
    public async Task ACancelThatNamesOneRunCancelsOnlyThatRunAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        Task<PrefillResult>? first = null;
        Task<PrefillResult>? second = null;
        // Both Starts land while the cancel holds the change gate, so both runs are active when it is sent.
        var services = new HookedDaemonLookup(fixture.Daemon, lookup =>
        {
            if (lookup != 2) return;
            first = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"], operationId: firstId);
            second = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["20"], operationId: secondId);
            Assert.True(SpinWait.SpinUntil(() => fixture.Session.Runs.Values.Count(run => run.PrefillWork.CurrentCount == 0) == 2, Bound));
        });
        var controller = new PersistentPrefillController(services,
            (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>(),
            new PrefillCacheService(new TestDbContextFactory(fixture.Options), NullLogger<PrefillCacheService>.Instance),
            NullLogger<PersistentPrefillController>.Instance);

        // A recovery pass resends every recorded cancel, so the ones that follow the Starts are held off to count
        // only what the Starts send.
        await fixture.Session.RecoveryWork.WaitAsync();
        try
        {
            var cancel = controller.CancelPrefillAsync(
                new PersistentServiceRequest { Service = PrefillPlatform.Steam, RunId = secondId }, CancellationToken.None);

            Assert.IsType<OkResult>(await cancel.WaitAsync(Bound));
            await first!.WaitAsync(Bound);
            await second!.WaitAsync(Bound);
            var canceled = fixture.Daemon.GetRun(fixture.Session.Id, secondId)!;
            Assert.Equal("canceled", (await canceled.Completion.Task.WaitAsync(Bound)).Snapshot.State);
        }
        finally { fixture.Session.RecoveryWork.Release(); }
        // The cancel landed while the second Start waited for the gate, so that Start ends before reaching the daemon.
        Assert.Equal(1, fixture.Client.StartCount);
        Assert.Empty(fixture.Client.Canceled);
        Assert.False(fixture.Daemon.GetRun(fixture.Session.Id, firstId)!.Completion.Task.IsCompleted);
    }

    [Fact]
    public async Task ACancelWhoseDaemonWasUnreachablePastItsDeadlineWaitsForTheDaemonAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        fixture.Client.CompleteCancellation = false;
        var run = await fixture.StartAsync("10");
        await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, true);
        // The daemon could not be reached until after the cancel's deadline, and it is still ending the run.
        run.Recovering = true;
        fixture.Session.Recovering = true;
        run.CancelDeadlineUtc = DateTime.UtcNow - TimeSpan.FromSeconds(1);
        fixture.Client.Set(run, "canceling", 0, "downloading");
        fixture.Session.NextRecoveryAtUtc = DateTime.MinValue;

        await fixture.Daemon.ProcessSessionExpiryAsync(DateTime.UtcNow);

        Assert.Equal(0, run.TerminalCompletedFlag);
        fixture.Client.Set(run, "canceled", 0, "canceled");
        await fixture.RefreshAsync();
        Assert.Equal("canceled", (await run.Completion.Task.WaitAsync(Bound)).Snapshot.State);
    }

    [Fact]
    public async Task AnEndThatArrivesWhileTheAdmissionFallbackCancelHoldsTheRunEndsItAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var client = Script(fixture);
        var switchable = new SwitchableDbContextFactory();
        switchable.Recover(new TestDbContextFactory(fixture.Options));
        SetField(fixture.Daemon, "_sessionService", new PrefillSessionService(switchable, NullLogger<PrefillSessionService>.Instance));
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PrefillHold = admission;
        var start = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
        await client.PrefillEntered.Task.WaitAsync(Bound);
        var run = Assert.Single(fixture.Session.Runs.Values);
        // The daemon's end event arrives while the cancel sent after the failed record still holds the run's work.
        client.AfterCancel = async _ =>
        {
            switchable.Recover(new TestDbContextFactory(fixture.Options));
            await RaiseProgressAsync(fixture, run, 2);
        };

        Assert.False(await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, false).WaitAsync(Bound));
        switchable.Fail();
        // Held off, the recovery pass that follows the Start cannot end the run in place of that event.
        await fixture.Session.RecoveryWork.WaitAsync();
        try
        {
            admission.SetResult();
            await start.WaitAsync(Bound);
            Assert.True(SpinWait.SpinUntil(() => run.Completion.Task.IsCompleted, Bound));
        }
        finally { fixture.Session.RecoveryWork.Release(); }
        Assert.Equal("canceled", (await run.Completion.Task).Snapshot.State);
    }

    [Fact]
    public async Task AnOverdueCancelWhoseDaemonAlreadyEndedTheRunKeepsTheDaemonsEndAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        fixture.Client.CompleteCancellation = false;
        var run = await fixture.StartAsync("10");
        await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, true);
        // The daemon ended the run and its end event was lost.
        fixture.Client.Set(run, "canceled", 0, "canceled");
        run.CancelDeadlineUtc = DateTime.UtcNow - TimeSpan.FromSeconds(1);
        fixture.Session.NextRecoveryAtUtc = DateTime.MaxValue;

        await fixture.Daemon.ProcessSessionExpiryAsync(DateTime.UtcNow);

        var ended = await run.Completion.Task.WaitAsync(Bound);
        Assert.Equal("canceled", ended.Snapshot.State);
        Assert.Equal("canceled", run.Items["10"].Result);
        Assert.False(run.HistoryIncomplete);
    }

    [Fact]
    public async Task AnOverdueCancelWhoseDaemonDoesNotAnswerTheReadStillEndsAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var client = Script(fixture);
        client.CompleteCancellation = false;
        var run = await fixture.StartAsync("10");
        await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, true);
        // The daemon's operation read runs out its command limit.
        var unanswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        unanswered.SetCanceled();
        client.PageHold = unanswered;
        run.CancelDeadlineUtc = DateTime.UtcNow - TimeSpan.FromSeconds(1);
        fixture.Session.NextRecoveryAtUtc = DateTime.MaxValue;

        await fixture.Daemon.ProcessSessionExpiryAsync(DateTime.UtcNow);

        var ended = await run.Completion.Task.WaitAsync(Bound);
        Assert.Equal("failed", ended.Snapshot.State);
        Assert.Equal("outcome-unknown", ended.Snapshot.Reason);
    }

    [Fact]
    public async Task AnOverdueCancelReadStopsWithTheHostAndLeavesTheRunOpenAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var client = Script(fixture);
        client.CompleteCancellation = false;
        var run = await fixture.StartAsync("10");
        await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, true);
        // The daemon never answers the operation read.
        var unanswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PageHold = unanswered;
        run.CancelDeadlineUtc = DateTime.UtcNow - TimeSpan.FromSeconds(1);
        fixture.Session.NextRecoveryAtUtc = DateTime.MaxValue;
        using var hostStop = new CancellationTokenSource();
        hostStop.Cancel();
        var logger = new CapturingLogger<SteamDaemonService>();
        SetField(fixture.Daemon, "_logger", logger);

        try
        {
            var pass = fixture.Daemon.ProcessSessionExpiryAsync(DateTime.UtcNow, hostStop.Token);
            Assert.True(await Task.WhenAny(pass, Task.Delay(Bound)) == pass, "The expiry pass still waits on the daemon after the host stopped.");
            // The stop reaches the host loop as a stop, not as a run the sweep could not end.
            var stopped = await Record.ExceptionAsync(() => pass);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning
                && entry.Message.Contains("whose cancel went unanswered", StringComparison.Ordinal));
            Assert.IsAssignableFrom<OperationCanceledException>(stopped);
            // The next start's recovery reads the run's end; the stop does not decide it.
            Assert.False(run.Completion.Task.IsCompleted);
        }
        finally { unanswered.TrySetCanceled(); }
    }

    [Fact]
    public async Task AStartTheClientRefusesBeforeSendingEndsWithThatReasonAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var client = Script(fixture);
        // The client refuses a Start for a daemon instance it no longer talks to, before anything is sent.
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        admission.SetException(new DaemonCommandException("instance-changed"));
        client.PrefillHold = admission;

        var refused = await Assert.ThrowsAsync<DaemonCommandException>(
            () => fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]).WaitAsync(Bound));
        var run = Assert.Single(fixture.Session.Runs.Values);

        Assert.Equal("instance-changed", refused.ErrorCode);
        var ended = await run.Completion.Task.WaitAsync(Bound);
        Assert.Equal("failed", ended.Snapshot.State);
        Assert.Equal("instance-changed", ended.Snapshot.Reason);
    }

    [Fact]
    public async Task ARefusedStartWhoseSaveFailsKeepsItsReasonAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var client = Script(fixture);
        client.StartRefusal = "operation-conflict";
        var switchable = new SwitchableDbContextFactory();
        switchable.Recover(new TestDbContextFactory(fixture.Options));
        SetField(fixture.Daemon, "_sessionService", new PrefillSessionService(switchable, NullLogger<PrefillSessionService>.Instance));
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PrefillHold = admission;
        var start = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
        await client.PrefillEntered.Task.WaitAsync(Bound);
        var run = Assert.Single(fixture.Session.Runs.Values);

        switchable.Fail();
        // Held off, the recovery pass that follows the Start runs only once the database is back.
        await fixture.Session.RecoveryWork.WaitAsync();
        try
        {
            admission.SetResult();
            await Assert.ThrowsAsync<DaemonCommandException>(() => start.WaitAsync(Bound));
            switchable.Recover(new TestDbContextFactory(fixture.Options));
        }
        finally { fixture.Session.RecoveryWork.Release(); }
        await fixture.RefreshAsync();

        var ended = await run.Completion.Task.WaitAsync(Bound);
        Assert.Equal("failed", ended.Snapshot.State);
        Assert.Equal("operation-conflict", ended.Snapshot.Reason);
        // The daemon never had the run, so no item history was lost.
        Assert.False(run.HistoryIncomplete);
    }

    [Fact]
    public async Task AnUnsentStartWhoseEndWasNotSavedKeepsItsHistoryWhenTheInstanceChangesAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var client = Script(fixture);
        var switchable = new SwitchableDbContextFactory();
        switchable.Recover(new TestDbContextFactory(fixture.Options));
        SetField(fixture.Daemon, "_sessionService", new PrefillSessionService(switchable, NullLogger<PrefillSessionService>.Instance));
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PrefillHold = admission;
        var start = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
        await client.PrefillEntered.Task.WaitAsync(Bound);
        var run = Assert.Single(fixture.Session.Runs.Values);

        switchable.Fail();
        await fixture.Session.RecoveryWork.WaitAsync();
        try
        {
            // The client refuses the Start before sending it, and the refused end cannot be saved.
            admission.SetException(new DaemonCommandException("instance-changed"));
            await Assert.ThrowsAsync<DaemonCommandException>(() => start.WaitAsync(Bound));
            switchable.Recover(new TestDbContextFactory(fixture.Options));
            client.InstanceId = Guid.NewGuid().ToString();
        }
        finally { fixture.Session.RecoveryWork.Release(); }
        await fixture.RefreshAsync();

        var ended = await run.Completion.Task.WaitAsync(Bound);
        Assert.Equal("failed", ended.Snapshot.State);
        Assert.Equal("instance-changed", ended.Snapshot.Reason);
        Assert.False(run.HistoryIncomplete);
    }

    [Fact]
    public async Task AnUnsentStartWhoseEndWasNotSavedKeepsItsReasonWhenTheSessionStopsAsync()
    {
        // Not disposed: the stop already disposed the session's token source, and the database is in memory.
        var fixture = await RunFixture.CreateAsync();
        var client = Script(fixture);
        var switchable = new SwitchableDbContextFactory();
        switchable.Recover(new TestDbContextFactory(fixture.Options));
        SetField(fixture.Daemon, "_sessionService", new PrefillSessionService(switchable, NullLogger<PrefillSessionService>.Instance));
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PrefillHold = admission;
        var start = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
        await client.PrefillEntered.Task.WaitAsync(Bound);
        var run = Assert.Single(fixture.Session.Runs.Values);

        switchable.Fail();
        // Held across the stop, so the waiting refresh cannot save the kept end before the stop reaches the run.
        await fixture.Session.RecoveryWork.WaitAsync();
        try
        {
            admission.SetException(new DaemonCommandException("instance-changed"));
            await Assert.ThrowsAsync<DaemonCommandException>(() => start.WaitAsync(Bound));
            switchable.Recover(new TestDbContextFactory(fixture.Options));
            await fixture.Daemon.TerminateSessionAsync(fixture.Session.Id).WaitAsync(Bound);
        }
        finally { fixture.Session.RecoveryWork.Release(); }

        var ended = await run.Completion.Task.WaitAsync(Bound);
        Assert.Equal("failed", ended.Snapshot.State);
        Assert.Equal("instance-changed", ended.Snapshot.Reason);
        Assert.False(run.HistoryIncomplete);
    }

    [Fact]
    public async Task ACancelDuringThePersistentGateWaitStopsTheStartBeforeTheDaemonAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        Assert.True(fixture.Daemon.PersistentChangeGate.TryEnterChange(out var change));
        var start = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
        Assert.True(SpinWait.SpinUntil(() => fixture.Session.Runs.Values.Any(run => run.PrefillWork.CurrentCount == 0), Bound));
        var run = Assert.Single(fixture.Session.Runs.Values);

        Assert.False(await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, false).WaitAsync(Bound));
        await change!.DisposeAsync();

        Assert.Equal("canceled", (await start.WaitAsync(Bound)).State);
        Assert.Equal("canceled", (await run.Completion.Task.WaitAsync(Bound)).Snapshot.State);
        Assert.Equal(0, fixture.Client.StartCount);
        Assert.Empty(fixture.Client.Canceled);
    }

    [Fact]
    public async Task AStartThatEndsBeforeItsRunIsSavedNeverListsTheRunAfterAnUpdateWithoutItAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        Assert.True(fixture.Daemon.PersistentChangeGate.TryEnterChange(out var change));
        var start = fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
        Assert.True(SpinWait.SpinUntil(() => fixture.Session.Runs.Values.Any(run => run.PrefillWork.CurrentCount == 0), Bound));
        var run = Assert.Single(fixture.Session.Runs.Values);
        Assert.False(await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, false).WaitAsync(Bound));
        fixture.Session.AuthState = DaemonAuthState.NotAuthenticated;
        // Another session update is waiting its turn to send when the Start ends.
        await fixture.Session.SessionUpdateSend.WaitAsync();
        fixture.Recorder.Invocations.Clear();
        var other = DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "NotifyHubAsync",
            fixture.Session, SignalREvents.DaemonSessionUpdated, DaemonSessionDto.FromSession(fixture.Session));
        await change!.DisposeAsync();
        SpinWait.SpinUntil(() => start.IsCompleted, TimeSpan.FromSeconds(1));
        fixture.Session.SessionUpdateSend.Release();

        await Assert.ThrowsAsync<DaemonCommandException>(() => start.WaitAsync(Bound));
        await other.WaitAsync(Bound);
        (string Method, object?[] Args)[] Updates() => fixture.Recorder.Invocations.ToArray()
            .Where(call => call.Method == nameof(ISignalRNotificationService.NotifyAdminAsync)
                && Equals(call.Args.FirstOrDefault(), SignalREvents.DaemonSessionUpdated)).ToArray();
        bool Lists((string Method, object?[] Args) call) =>
            ((DaemonSessionDto)call.Args[1]!).Runs.Any(listed => listed.RunId == run.PrefillRunId);
        bool Ended((string Method, object?[] Args) call) =>
            ((DaemonSessionDto)call.Args[1]!).Runs.Any(listed => listed.RunId == run.PrefillRunId
                && listed.Snapshot.State == "canceled");
        Assert.True(SpinWait.SpinUntil(() => Updates().Any(Ended), Bound));

        var updates = Updates();
        var firstWithout = Array.FindIndex(updates, call => !Lists(call));
        Assert.True(firstWithout < 0 || !updates.Skip(firstWithout).Any(Lists));
    }

    private static Task RaiseProgressAsync(RunFixture fixture, DaemonRun run, long sequence) =>
        DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "OnProgressChangeAsync", fixture.Session,
            new SocketPrefillProgress
            {
                OperationId = run.PrefillRunId.ToString(),
                DaemonInstanceId = run.DaemonInstanceId,
                Sequence = sequence
            });

    private static ScriptedRunClient Script(RunFixture fixture)
    {
        var client = (ScriptedRunClient)(object)DispatchProxy.Create<IDaemonClient, ScriptedRunClient>();
        client.InstanceId = fixture.Client.InstanceId;
        fixture.Session.Client = (IDaemonClient)(object)client;
        return client;
    }

    internal static void UseStallTimeout(PrefillDaemonServiceBase daemon, int seconds) =>
        SetField(daemon, "_configuration", new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Prefill:StallTimeoutSeconds"] = seconds.ToString(CultureInfo.InvariantCulture)
            })
            .Build());

    internal static void SetField(PrefillDaemonServiceBase daemon, string name, object value) =>
        typeof(PrefillDaemonServiceBase).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(daemon, value);
}

/// <summary>
/// A run client the test steers: it can hold the daemon's operation read, act right after the daemon accepts a
/// cancel, and confirm a sign-out.
/// </summary>
internal class ScriptedRunClient : RunClient
{
    public TaskCompletionSource? PageHold { get; set; }
    /// <summary>When set, a Start waits for the daemon's admission until the test completes it.</summary>
    public TaskCompletionSource? PrefillHold { get; set; }
    /// <summary>Completed once a held Start has reached the daemon.</summary>
    public TaskCompletionSource PrefillEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Func<Guid, Task>? AfterCancel { get; set; }
    public int Logouts { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IDaemonClient.GetOperationAsync) && PageHold is { } hold)
            // The read's own token ends a held read, as it ends the daemon client's wait for an answer.
            return HeldAsync<DaemonOperationPage>(hold, (CancellationToken)args![4]!, targetMethod, args);
        if (targetMethod?.Name == nameof(IDaemonClient.PrefillAsync) && PrefillHold is { } admission)
        {
            PrefillEntered.TrySetResult();
            return HeldAsync<PrefillResult>(admission, CancellationToken.None, targetMethod, args);
        }
        if (targetMethod?.Name == nameof(IDaemonClient.CancelPrefillAsync) && AfterCancel is { } after)
            return CancelThenAsync(after, targetMethod, args);
        if (targetMethod?.Name == nameof(IDaemonClient.LogoutWithReasonAsync))
        {
            Logouts++;
            return Task.FromResult(new LogoutOutcome(true, false));
        }
        return base.Invoke(targetMethod, args);
    }

    private async Task<T> HeldAsync<T>(TaskCompletionSource hold, CancellationToken cancellationToken, MethodInfo method, object?[]? args)
    {
        await hold.Task.WaitAsync(cancellationToken);
        return await (Task<T>)base.Invoke(method, args)!;
    }

    private async Task<DaemonRunSnapshot> CancelThenAsync(Func<Guid, Task> after, MethodInfo method, object?[]? args)
    {
        var answer = await (Task<DaemonRunSnapshot>)base.Invoke(method, args)!;
        await after((Guid)args![0]!);
        return answer;
    }
}

/// <summary>Holds the first database read made while the run's terminal save is marked, until the test releases it.</summary>
internal sealed class HeldRunEndSave(DbContextOptions<AppDbContext> options, DaemonRun run) : IDbContextFactory<AppDbContext>
{
    private int _held;

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AppDbContext CreateDbContext() => new(options);

    public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref run.TerminalSavePending) == 1 && Interlocked.Exchange(ref _held, 1) == 0)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
        return new AppDbContext(options);
    }
}

/// <summary>Answers every daemon lookup with one daemon, and runs a step on each lookup before answering.</summary>
internal sealed class HookedDaemonLookup(SteamDaemonService daemon, Action<int> onLookup) : IServiceProvider
{
    private int _lookups;

    public object? GetService(Type serviceType)
    {
        if (serviceType != typeof(SteamDaemonService)) return null;
        onLookup(++_lookups);
        return daemon;
    }
}

internal sealed class RunFixture : IAsyncDisposable
{
    private readonly TestDatabase? _database;
    public DbContextOptions<AppDbContext> Options { get; }
    public SteamDaemonService Daemon { get; }
    public RecordingNotificationProxy Recorder { get; }
    public DaemonSession Session { get; }
    public RunClient Client { get; }
    public PrefillSessionService History { get; }

    private RunFixture(TestDatabase? database = null, bool persistent = false, TimeProvider? cacheStatusClock = null)
    {
        _database = database;
        Options = database?.Options ?? new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"prefill_runs_{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
        var created = PrefillCacheChangeTests.NewDaemon(Options, cacheStatusClock);
        Daemon = created.Daemon;
        Recorder = created.Recorder;
        Session = persistent ? new DaemonSession
        {
            Id = created.Session.Id,
            UserId = created.Session.UserId,
            IsPersistent = true,
            Status = DaemonSessionStatus.Active,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        } : created.Session;
        created.Daemon.InjectSession(Session);
        Session.AuthState = DaemonAuthState.Authenticated;
        var client = DispatchProxy.Create<IDaemonClient, RunClient>();
        Client = (RunClient)(object)client;
        Session.Client = client;
        Session.Capabilities = RunClient.Capabilities(Client.InstanceId);
        History = new PrefillSessionService(new TestDbContextFactory(Options), NullLogger<PrefillSessionService>.Instance);
    }

    public static async Task<RunFixture> CreateAsync(bool relational = false, bool persistent = false,
        TimeProvider? cacheStatusClock = null)
    {
        var fixture = new RunFixture(relational ? await TestDatabase.CreateAsync() : null, persistent, cacheStatusClock);
        await using var context = new AppDbContext(fixture.Options);
        await context.Database.EnsureCreatedAsync();
        context.PrefillSessions.Add(new PrefillSession
        {
            SessionId = fixture.Session.Id,
            CreatedBySessionId = fixture.Session.UserId,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();
        return fixture;
    }

    public async Task<DaemonRun> StartAsync(string appId, Guid? schedule = null)
    {
        var response = await Daemon.PrefillAsync(Session.Id, appIds: [appId], scheduleId: schedule,
            scheduleName: schedule.HasValue ? "Named schedule" : null, notificationMode: "silent");
        await RefreshAsync();
        return Assert.IsType<DaemonRun>(Daemon.GetRun(Session.Id, response.RunId!.Value));
    }

    public async Task RefreshAsync()
    {
        await Session.RecoveryWork.WaitAsync();
        Session.RecoveryWork.Release();
        await Daemon.RefreshRunsAsync(Session.Id);
    }

    /// <summary>
    /// Gives the sessions running containers, so a status read that gets no answer leaves a temporary session
    /// to recovery instead of ending it as a session whose container is gone.
    /// </summary>
    public void KeepContainersRunning(params DaemonSession[] sessions)
    {
        var containers = new RecordingContainerGateway();
        foreach (var session in sessions)
        {
            session.ContainerId = Guid.NewGuid().ToString("N");
            containers.AddContainer(new FakeContainer { Id = session.ContainerId, Name = session.Id, Running = true });
        }
        PrefillRunTests.SetField(Daemon, "_containerGateway", containers);
    }

    public async ValueTask DisposeAsync()
    {
        await Session.CancellationTokenSource.CancelAsync();
        await Session.RecoveryWork.WaitAsync();
        Session.RecoveryWork.Release();
        if (_database is not null) await _database.DisposeAsync();
    }
}

internal class RunClient : NullReturningProxy
{
    public List<CachedDepotInput>? CachedDepots { get; private set; }
    public DaemonStatus? Status { get; set; }
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();
    public ConcurrentDictionary<Guid, DaemonOperationPage> Pages { get; } = new();
    public List<Guid> Canceled { get; } = [];
    public int StartCount { get; private set; }
    public int StartLoginCalls { get; private set; }

    /// <summary>When set, a status read runs this instead of answering.</summary>
    public Func<Task<DaemonStatus?>>? StatusRead { get; set; }

    /// <summary>When true, a login command makes the daemon report its logged-in capabilities.</summary>
    public bool SignInOnStartLogin { get; set; }
    public bool LoseAcknowledgment { get; set; }

    /// <summary>When set, a Start is refused with this error code.</summary>
    public string? StartRefusal { get; set; }
    public bool OmitInventory { get; set; }
    public bool CompleteCancellation { get; set; } = true;
    public bool Offline { get; set; }
    public bool MissingPages { get; set; }
    public Func<Guid, int, DaemonOperationPage>? ReadPage { get; set; }

    public static DaemonStatus Capabilities(string instance) => new()
    {
        Status = "logged-in",
        ProtocolVersion = 2,
        DaemonInstanceId = instance,
        MaxConcurrentRuns = 3,
        MaxConcurrentRequests = 8,
        Features = ["operationProgress", "inlineSelection", "targetedCancel", "concurrentPrefill", "activeOperations"],
        ActiveOperations = [],
        RecentOperations = []
    };

    public void Set(DaemonRun run, string state, long bytes, string itemResult)
    {
        var current = Pages[run.PrefillRunId];
        var sequence = current.Operation.Sequence + 1;
        var item = new DaemonRunItem
        {
            AppId = run.Options.AppIds![0],
            State = state,
            Result = itemResult,
            BytesTransferred = bytes,
            TotalBytes = 100,
            Sequence = sequence
        };
        Pages[run.PrefillRunId] = current with
        {
            Operation = current.Operation with
            {
                State = state,
                Sequence = sequence,
                UpdatedAt = DateTimeOffset.UtcNow,
                CurrentItem = item,
                BytesTransferred = bytes,
                CompletedApps = itemResult == "success" ? 1 : 0,
                FailedApps = itemResult == "failed" ? 1 : 0,
                CanceledApps = itemResult == "canceled" ? 1 : 0
            },
            Items = [item]
        };
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IDaemonClient.GetStatusAsync))
        {
            if (StatusRead is not null) return StatusRead();
            if (Status is not null) return Task.FromResult<DaemonStatus?>(Status);
            if (Offline) return Task.FromResult<DaemonStatus?>(null);
            var status = Capabilities(InstanceId);
            status.ReadStamp = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!OmitInventory)
            {
                status.ActiveOperations = Pages.Values.Where(page => page.Operation.State is not ("completed" or "failed" or "canceled"))
                    .Select(page => page.Operation).ToList();
                status.RecentOperations = Pages.Values.Where(page => page.Operation.State is "completed" or "failed" or "canceled")
                    .Select(page => page.Operation).ToList();
            }
            return Task.FromResult<DaemonStatus?>(status);
        }
        if (targetMethod?.Name == nameof(IDaemonClient.StartLoginAsync))
        {
            StartLoginCalls++;
            if (SignInOnStartLogin) Status = null;
        }
        if (targetMethod?.Name == nameof(IDaemonClient.PrefillAsync) && args?[0] is Guid id)
        {
            StartCount++;
            if (StartRefusal is not null)
                return Task.FromResult(new PrefillResult { Success = false, ErrorCode = StartRefusal });
            var options = (DaemonRunOptions)args[2]!;
            CachedDepots = (List<CachedDepotInput>?)args[3];
            var snapshot = new DaemonRunSnapshot
            {
                OperationId = id.ToString(),
                DaemonInstanceId = InstanceId,
                Sequence = 1,
                StartedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                State = "downloading",
                SelectionResolved = true,
                TotalApps = options.AppIds!.Count
            };
            Pages[id] = new DaemonOperationPage(snapshot, options, true, options.AppIds.Count,
                options.AppIds.Select(app => new DaemonRunItem { AppId = app, Sequence = 1 }).ToArray(), null);
            return LoseAcknowledgment ? Task.FromException<PrefillResult>(new IOException("Acknowledgment lost"))
                : Task.FromResult(new PrefillResult { Success = true, RunId = id, DaemonInstanceId = InstanceId, State = "started" });
        }
        if (targetMethod?.Name == nameof(IDaemonClient.GetOperationAsync))
        {
            if (Offline) return Task.FromException<DaemonOperationPage>(new IOException("Disconnected"));
            if (MissingPages) return Task.FromException<DaemonOperationPage>(new DaemonCommandException("operation-not-found"));
            if (ReadPage is not null) return Task.FromResult(ReadPage((Guid)args![0]!, (int)args[2]!));
            return Pages.TryGetValue((Guid)args![0]!, out var page) ? Task.FromResult(page)
                : Task.FromException<DaemonOperationPage>(new DaemonCommandException("operation-not-found"));
        }
        if (targetMethod?.Name == nameof(IDaemonClient.CancelPrefillAsync) && args?[0] is Guid canceled)
        {
            Canceled.Add(canceled);
            var page = Pages[canceled];
            if (CompleteCancellation)
            {
                page = page with
                {
                    Operation = page.Operation with
                    {
                        State = "canceled",
                        Sequence = page.Operation.Sequence + 1,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        CanceledApps = page.TotalItems
                    },
                    Items = page.Items.Select(item => item with { State = "canceled", Result = "canceled", Sequence = item.Sequence + 1 }).ToArray()
                };
                Pages[canceled] = page;
            }
            return Task.FromResult(page.Operation);
        }
        return base.Invoke(targetMethod, args);
    }
}
