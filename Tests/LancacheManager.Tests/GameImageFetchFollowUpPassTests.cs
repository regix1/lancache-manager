using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Services.Scheduling;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A fetch pass reads the list of games it will fetch art for when it starts, so rows written after
/// that are invisible to it. Depot mapping writes exactly such rows and then asks for a pass, which
/// is refused while one is running. What happens to that refused request is what these tests pin.
///
/// A null from StartFetchInBackgroundAsync is documented as "the lock was held and a follow-up is armed",
/// and callers drop their own fallbacks on the strength of that, so it is pinned for both flavors of
/// caller rather than only for the one that found the bug.
///
/// On the serialized collection because these tests drive the static execution lock inside
/// GameImageFetchService, which is process-wide: two classes taking it at once would refuse each
/// other's first start and fail whichever ran second.
/// </summary>
[Collection(nameof(GameImageExecutionLockCollection))]
public sealed class GameImageFetchFollowUpPassTests
{
    /// <summary>How long a pass that is supposed to run is given to reach its fetch.</summary>
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the test watches for a pass that must not happen. Each unwanted pass costs one scope
    /// and one count query, so a chain announces itself in well under this.
    /// </summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartRefusedWhileAPassRuns_FetchesAgainWhenThatPassEndsAsync(bool refusedAsksForEpicUrls)
    {
        var httpClients = new PausingHttpClientFactory();
        await using var provider = BuildProvider(httpClients);
        var tracker = NewTracker();
        var service = NewService(provider, tracker, NullNotifications());

        var running = await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled);
        Assert.NotNull(running);
        Assert.True(await httpClients.WaitForCallAsync(Arrival), "the first pass never reached its fetch");

        // False is depot mapping, which has just written GameAppId onto downloads the running pass
        // already read. True is the clear-the-cache path, which has just deleted every stored image.
        // Both are refused the execution lock, and neither may lose its request.
        Assert.Null(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: refusedAsksForEpicUrls, RunTrigger.Scheduled));

        httpClients.Release();

        Assert.True(
            await httpClients.WaitForCallAsync(Arrival),
            "the refused request was dropped: no pass ran once the running one finished");

        await WaitForPassesToFinishAsync(tracker);
    }

    /// <summary>
    /// The record is cleared when a pass takes the execution lock, before that pass reads its work
    /// list, and the empty-downloads exit at the top of the fetch is the one place a pass can leave
    /// without having read it. Leaving the record set there instead reads as the safer choice and is
    /// not: the pass that the exit would arm reads the same empty table and arms another, so the
    /// chain never ends. This is what stops that being introduced.
    /// </summary>
    [Fact]
    public async Task AFollowUpThatFindsNoDownloadsDoesNotChainMorePassesAsync()
    {
        var httpClients = new PausingHttpClientFactory();
        await using var provider = BuildProvider(httpClients);
        var tracker = NewTracker();
        var service = NewService(provider, tracker, NullNotifications());

        Assert.NotNull(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));
        Assert.True(await httpClients.WaitForCallAsync(Arrival), "the first pass never reached its fetch");
        Assert.Null(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));

        httpClients.Release();
        Assert.True(await httpClients.WaitForCallAsync(Arrival), "the follow-up pass never ran");

        // The Downloads table this provider builds is empty, so the follow-up returns at the top of
        // the fetch without reading a work list. Nothing may follow it. Read the count after the
        // window a chain would announce itself in, rather than asking whether one more pass arrived:
        // the number separates a single stray pass from a chain, and a boolean cannot.
        await Task.Delay(Quiet);
        var passes = httpClients.Calls;
        Assert.True(
            passes == 2,
            $"expected the running pass and its follow-up and nothing else, saw {passes}; more than "
                + "two means a pass that found nothing to do armed another one, which repeats");

        await WaitForPassesToFinishAsync(tracker);
    }

    /// <summary>
    /// A second image fetch asked for while a pass runs waits for that pass instead of being answered by
    /// it: a pass reads its work list when it starts, so a request made while it runs needs a pass of its
    /// own.
    /// </summary>
    [Fact]
    public async Task ASecondImageFetchWaitsForTheRunningPassAsync()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
        var conflictChecker = OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance);
        var queue = new OperationQueueService(
            tracker,
            conflictChecker,
            NullLogger<OperationQueueService>.Instance);

        var pass = tracker.RegisterOperation(
            OperationType.GameImageFetch, "Game Image Fetch", new CancellationTokenSource());

        var starts = 0;
        var answer = await queue.EnqueueAsync(
            OperationType.GameImageFetch,
            ConflictScope.Bulk(),
            "Game Image Fetch",
            () =>
            {
                Interlocked.Increment(ref starts);
                return Task.FromResult<Guid?>(Guid.NewGuid());
            },
            CancellationToken.None);

        Assert.True(answer.Queued);
        Assert.NotEqual(pass, answer.OperationId);
        Assert.Equal(0, Volatile.Read(ref starts));

        tracker.CompleteOperation(pass, success: true);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref starts) == 1, TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// A Clear image cache pressed while a cache clear or database reset runs waits in the queue. A
    /// scheduled image pass that starts meanwhile runs under the same title, and the waiting refill is
    /// still not taken for that pass: it runs its own pass once that pass ends.
    /// </summary>
    [Fact]
    public async Task AParkedRefillIsNotFoldedIntoARunningScheduledPassAsync()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
        var conflictChecker = OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance);
        var queue = new OperationQueueService(tracker, conflictChecker, NullLogger<OperationQueueService>.Instance);
        var clearing = tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear (All)", new CancellationTokenSource());
        var starts = 0;
        var parked = await queue.EnqueueAsync(
            OperationType.GameImageFetch,
            ConflictScope.Bulk(),
            "Game Image Fetch",
            () =>
            {
                Interlocked.Increment(ref starts);
                return Task.FromResult<Guid?>(Guid.NewGuid());
            },
            CancellationToken.None);
        Assert.True(parked.Queued);
        var passName = ScheduleTitles.ByServiceKey["gameImageFetch"];
        var pass = tracker.RegisterOperation(OperationType.GameImageFetch, passName, new CancellationTokenSource());

        tracker.CompleteOperation(clearing, success: true);
        Assert.True(SpinWait.SpinUntil(
            () => tracker.GetOperation(parked.OperationId)!.BlockedByName == passName, TimeSpan.FromSeconds(5)));
        Assert.Equal(OperationStatus.Waiting, tracker.GetOperation(parked.OperationId)!.Status);
        Assert.Equal(0, Volatile.Read(ref starts));

        tracker.CompleteOperation(pass, success: true);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref starts) == 1, TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// Refresh banners pressed while a pass runs parks its refill behind that pass. When the pass
    /// succeeds, the refill must find the execution lock free and run a pass of its own with the Epic
    /// URL refresh. A refill turned away by a lock the finished pass still holds leaves a follow-up
    /// without the refresh in its place, and parks behind that one.
    /// </summary>
    [Fact]
    public async Task ARefillParkedBehindAPassThatSucceedsRunsItsOwnPassWithTheEpicRefreshAsync()
    {
        var passes = new PassRecorder();
        await using var services = await BuildServicesWithADownloadAsync(passes);
        var tracker = NewTracker();
        var notifications = HoldingNotifications.Create();
        var service = NewService(services, tracker, notifications.Service);
        var checker = OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance);
        var queue = new OperationQueueService(tracker, checker, NullLogger<OperationQueueService>.Instance);

        var running = await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled);
        Assert.NotNull(running);
        Assert.True(await notifications.WaitForHeldProgressAsync(Arrival), "the running pass never reported progress");

        var refill = await ParkRefillAsync(service, checker, queue);

        notifications.ReleaseProgress();

        await AssertTheRefillRanNextWithTheEpicRefreshAsync(passes, tracker, refill);
        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(running.Value)!.Status);
    }

    /// <summary>
    /// The same refill, parked behind a scheduled pass that is canceled while a follow-up is armed:
    /// depot mapping or log processing asked for a pass during it and was refused the lock. The refill
    /// still runs next, with the Epic URL refresh, because the canceled pass starts its follow-up only
    /// after its run has ended, and by then the refill has taken the lock and the request with it.
    /// </summary>
    [Fact]
    public async Task ARefillParkedBehindACanceledScheduledPassRunsBeforeTheArmedFollowUpAsync()
    {
        var passes = new PassRecorder();
        await using var services = await BuildServicesWithADownloadAsync(passes);
        var tracker = NewTracker();
        var notifications = HoldingNotifications.Create();
        var service = NewService(services, tracker, notifications.Service);
        var checker = OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance);
        var queue = new OperationQueueService(tracker, checker, NullLogger<OperationQueueService>.Instance);

        using var stopping = new CancellationTokenSource();
        var scheduledRun = (Task)typeof(ScopedScheduledBackgroundService)
            .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(CancellationToken)])!
            .Invoke(service, [stopping.Token])!;
        Assert.True(await notifications.WaitForHeldProgressAsync(Arrival), "the scheduled pass never reported progress");
        var canceledPass = Assert.Single(tracker.GetActiveOperations(OperationType.GameImageFetch)).Id;

        Assert.Null(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));

        var refill = await ParkRefillAsync(service, checker, queue);

        // A broadcast to browsers takes longer than the queue's in-process promotion. Holding the
        // canceled pass's ending broadcast until the queue has handed the refill on makes that the
        // order on every run instead of a thread-pool race.
        notifications.HoldFirstCompletionUntil(() => tracker.GetOperation(refill)?.Status != OperationStatus.Waiting);

        await stopping.CancelAsync();
        notifications.ReleaseProgress();

        await AssertTheRefillRanNextWithTheEpicRefreshAsync(passes, tracker, refill);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduledRun);
        Assert.Equal(OperationStatus.Cancelled, tracker.GetOperation(canceledPass)!.Status);
    }

    private static ServiceProvider BuildProvider(IHttpClientFactory httpClients)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(
            options => options.UseInMemoryDatabase($"game-image-fetch-{Guid.NewGuid():N}"));
        services.AddSingleton(httpClients);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// One download with no game identity gets every pass past the empty-table exit and gives no
    /// phase anything to fetch, so each pass runs all four phases and finishes. No Epic account is
    /// signed in, so a pass that asks for the Epic service records the request and skips the refresh.
    /// </summary>
    private static async Task<ServiceProvider> BuildServicesWithADownloadAsync(PassRecorder passes)
    {
        // Named once here: the options lambda runs on every DbContext, and a name made inside it
        // would hand each pass its own empty database.
        var databaseName = $"game-image-refill-{Guid.NewGuid():N}";
        var collection = new ServiceCollection();
        collection.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
        collection.AddSingleton<IHttpClientFactory>(passes);
        collection.AddScoped<EpicMappingService>(_ =>
        {
            passes.RecordEpicRefresh();
            return null!;
        });
        var services = collection.BuildServiceProvider();

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Downloads.Add(new Download { Service = "origin", ClientIp = "10.0.0.1" });
        await db.SaveChangesAsync();
        return services;
    }

    /// <summary>
    /// Presses Refresh banners through the controller, whose conflict check finds the running pass
    /// and parks the refill on the queue.
    /// </summary>
    private static async Task<Guid> ParkRefillAsync(
        GameImageFetchService service, IOperationConflictChecker checker, IOperationQueue queue)
    {
        var controller = new GameImagesController(
            NullLogger<GameImagesController>.Instance,
            NullProxy<IImageCacheService>(),
            service,
            null!,
            checker,
            queue)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var accepted = Assert.IsType<AcceptedResult>(await controller.ClearImageCacheAsync(CancellationToken.None));
        var parked = Assert.IsType<QueuedOperationResponse>(accepted.Value);
        Assert.True(parked.Queued);
        return parked.OperationId;
    }

    private static async Task AssertTheRefillRanNextWithTheEpicRefreshAsync(
        PassRecorder passes, UnifiedOperationTracker tracker, Guid refill)
    {
        Assert.True(SpinWait.SpinUntil(() => passes.Snapshot().Length >= 2, Arrival), "no pass ran after the first");
        var first = passes.Snapshot();
        Assert.False(first[0]);
        Assert.True(first[1], "a pass without the Epic URL refresh ran before the refill");

        await WaitForPassesToFinishAsync(tracker);
        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(refill)!.Status);
    }

    private static UnifiedOperationTracker NewTracker() => new(
        new ProcessManager(NullLogger<ProcessManager>.Instance),
        NullLogger<UnifiedOperationTracker>.Instance);

    private static GameImageFetchService NewService(
        IServiceProvider provider, UnifiedOperationTracker tracker, ISignalRNotificationService notifications)
    {
        return new GameImageFetchService(
            provider,
            NullLogger<GameImageFetchService>.Instance,
            new ConfigurationBuilder().Build(),
            NullProxy<IStateService>(),
            notifications,
            NullProxy<IImageCacheService>(),
            tracker);
    }

    /// <summary>
    /// Returns once every pass this test started has let go of the execution lock.
    /// </summary>
    /// <remarks>
    /// The lock is static and shared by every test in this class, so a pass still unwinding when a
    /// test returns is a pass the next test gets refused by, which reads as a failure of whichever
    /// test happened to run second. Waiting on the tracker rather than on the lock itself is what
    /// makes this honest: a pass releases the lock and only then completes its tracked operation
    /// (`GameImageFetchService.cs:185-186`), so an empty active list means the lock is already free.
    /// Reaching into the semaphore instead would take a permit the finishing pass is about to give
    /// back.
    /// </remarks>
    private static async Task WaitForPassesToFinishAsync(UnifiedOperationTracker tracker)
    {
        var deadline = DateTime.UtcNow + Arrival;
        while (tracker.GetActiveOperations(OperationType.GameImageFetch).Any())
        {
            Assert.True(DateTime.UtcNow < deadline, "a fetch pass never finished");
            await Task.Delay(10);
        }
    }

    private static ISignalRNotificationService NullNotifications() => NullProxy<ISignalRNotificationService>();

    private static T NullProxy<T>() where T : class => DispatchProxy.Create<T, NullReturningProxy>();

    /// <summary>
    /// Stands in for the HTTP client factory the fetch asks for before it reads anything, so a pass
    /// can be held inside the execution lock for as long as the test needs and each entry counted.
    /// </summary>
    private sealed class PausingHttpClientFactory : IHttpClientFactory
    {
        private readonly SemaphoreSlim _entered = new(0);
        private readonly ManualResetEventSlim _release = new(false);
        private int _calls;

        /// <summary>
        /// Passes that have reached their fetch. A count rather than a flag because the number is
        /// the diagnosis: three says one stray pass ran, thirty says a chain is running away.
        /// </summary>
        public int Calls => Volatile.Read(ref _calls);

        public HttpClient CreateClient(string name)
        {
            Interlocked.Increment(ref _calls);
            _entered.Release();
            _release.Wait(TimeSpan.FromSeconds(30));
            return new HttpClient();
        }

        public Task<bool> WaitForCallAsync(TimeSpan timeout) => _entered.WaitAsync(timeout);

        public void Release() => _release.Set();
    }

    /// <summary>
    /// Records each pass when it asks for its HTTP client, with whether it asked for the Epic service
    /// just before, which only a pass that refreshes Epic's image URLs does. Passes hold the execution
    /// lock while they do both, so one pass's two records never interleave with another's.
    /// </summary>
    private sealed class PassRecorder : IHttpClientFactory
    {
        private readonly object _sync = new();
        private readonly List<bool> _refreshedEpicUrls = [];
        private bool _epicRefreshRequested;

        public bool[] Snapshot()
        {
            lock (_sync)
            {
                return _refreshedEpicUrls.ToArray();
            }
        }

        public void RecordEpicRefresh()
        {
            lock (_sync)
            {
                _epicRefreshRequested = true;
            }
        }

        public HttpClient CreateClient(string name)
        {
            lock (_sync)
            {
                _refreshedEpicUrls.Add(_epicRefreshRequested);
                _epicRefreshRequested = false;
            }

            return new HttpClient();
        }
    }

    /// <summary>
    /// Holds the first progress broadcast, which a pass sends with its run registered and the
    /// execution lock held, until the test lets it go. Can also hold the first run-ended broadcast
    /// until a condition is true. Every other broadcast finishes a moment later rather than at once,
    /// as a send to browsers does: a send that finishes at once lets a pass that ends its run while
    /// holding the lock release it before the queue's promotion gets there, which hides that order.
    /// </summary>
    private class HoldingNotifications : DispatchProxy
    {
        private readonly TaskCompletionSource _progressHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _progressReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<bool>? _completionHeldUntil;
        private int _progressSends;
        private int _completionSends;

        public static HoldingNotifications Create()
            => (HoldingNotifications)(object)DispatchProxy.Create<ISignalRNotificationService, HoldingNotifications>();

        public ISignalRNotificationService Service => (ISignalRNotificationService)(object)this;

        public async Task<bool> WaitForHeldProgressAsync(TimeSpan timeout)
            => await Task.WhenAny(_progressHeld.Task, Task.Delay(timeout)) == _progressHeld.Task;

        public void ReleaseProgress() => _progressReleased.TrySetResult();

        public void HoldFirstCompletionUntil(Func<bool> condition) => Volatile.Write(ref _completionHeldUntil, condition);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                var eventName = (string)args![0]!;
                if (eventName == SignalREvents.GameImageFetchProgress && Interlocked.Increment(ref _progressSends) == 1)
                {
                    _progressHeld.TrySetResult();
                    return _progressReleased.Task;
                }

                if (eventName == SignalREvents.GameImageFetchComplete
                    && Interlocked.Increment(ref _completionSends) == 1
                    && Volatile.Read(ref _completionHeldUntil) is { } condition)
                {
                    return Task.Run(() => SpinWait.SpinUntil(condition, Arrival));
                }
            }

            return Task.Delay(1);
        }
    }
}
