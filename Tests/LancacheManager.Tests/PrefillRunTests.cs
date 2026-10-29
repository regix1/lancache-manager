using System.Collections.Concurrent;
using System.Reflection;
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
        await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, second.PrefillRunId);

        Assert.Equal([second.PrefillRunId], fixture.Client.Cancelled);
        Assert.False(first.Completion.Task.IsCompleted);
        Assert.False(third.Completion.Task.IsCompleted);
        Assert.Equal("cancelled", (await second.Completion.Task).Snapshot.State);
        Assert.Equal(10, first.Snapshot.BytesTransferred);
        Assert.Equal(30, third.Snapshot.BytesTransferred);
        Assert.Contains(fixture.Session.Runs.Values, run => run.TerminalCompletedFlag != 2);
    }

    [Theory]
    [InlineData("success", 100, true)]
    [InlineData("already_cached", 0, false)]
    [InlineData("cancelled", 25, false)]
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
        Assert.Empty(fixture.Client.Cancelled);
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
        fixture.Client.Set(first, "cancelled", 5, "cancelled");
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
        Assert.Empty(fixture.Client.Cancelled);
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
        Assert.Equal(3, fixture.Client.Cancelled.Count);
        foreach (var run in new[] { first, second, third })
        {
            Assert.Equal("failed", (await run.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5))).Snapshot.State);
            Assert.Equal("auth-lost", run.Snapshot.Reason);
            Assert.True(run.LastProgress!.RequiresLogin);
        }
        Assert.DoesNotContain(fixture.Session.Runs.Values, run => run.TerminalCompletedFlag != 2);
    }

    [Fact]
    public async Task UserCancelStaysCancelledWhenSignOutLandsDuringCancelAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var run = await fixture.StartAsync("10");
        fixture.Client.CompleteCancellation = false;
        await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId);
        await DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "ApplyStatusAsync", fixture.Session,
            new DaemonStatus { Status = "awaiting-login" }, null);
        Assert.Null(run.CancelReason);
        fixture.Client.Set(run, "cancelled", 5, "cancelled");
        await fixture.RefreshAsync();
        Assert.Equal("cancelled", (await run.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5))).Snapshot.State);
        Assert.Null(run.Snapshot.Reason);
        await using var context = new AppDbContext(fixture.Options);
        var saved = await context.PrefillRuns.SingleAsync(row => row.Id == run.PrefillRunId);
        Assert.Equal("cancelled", saved.State);
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
    public List<Guid> Cancelled { get; } = [];
    public int StartCount { get; private set; }
    public int StartLoginCalls { get; private set; }

    /// <summary>When set, a status read runs this instead of answering.</summary>
    public Func<Task<DaemonStatus?>>? StatusRead { get; set; }

    /// <summary>When true, a login command makes the daemon report its logged-in capabilities.</summary>
    public bool SignInOnStartLogin { get; set; }
    public bool LoseAcknowledgement { get; set; }

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
                CancelledApps = itemResult == "cancelled" ? 1 : 0
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
                status.ActiveOperations = Pages.Values.Where(page => page.Operation.State is not ("completed" or "failed" or "cancelled"))
                    .Select(page => page.Operation).ToList();
                status.RecentOperations = Pages.Values.Where(page => page.Operation.State is "completed" or "failed" or "cancelled")
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
            return LoseAcknowledgement ? Task.FromException<PrefillResult>(new IOException("Acknowledgement lost"))
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
        if (targetMethod?.Name == nameof(IDaemonClient.CancelPrefillAsync) && args?[0] is Guid cancelled)
        {
            Cancelled.Add(cancelled);
            var page = Pages[cancelled];
            if (CompleteCancellation)
            {
                page = page with
                {
                    Operation = page.Operation with
                    {
                        State = "cancelled",
                        Sequence = page.Operation.Sequence + 1,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        CancelledApps = page.TotalItems
                    },
                    Items = page.Items.Select(item => item with { State = "cancelled", Result = "cancelled", Sequence = item.Sequence + 1 }).ToArray()
                };
                Pages[cancelled] = page;
            }
            return Task.FromResult(page.Operation);
        }
        return base.Invoke(targetMethod, args);
    }
}
