using System.Collections.Concurrent;
using System.Data.Common;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    /// <summary>
    /// How long a start through the queue may take before a queue lock that never came back fails the
    /// test instead of hanging it.
    /// </summary>
    private static readonly TimeSpan TickTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartRefusedWhileAPassRuns_FetchesAgainWhenThatPassEndsAsync(bool refusedAsksForEpicUrls)
    {
        var httpClients = new PausingHttpClientFactory();
        var tracker = NewTracker();
        await using var provider = BuildProvider(httpClients, tracker);
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
        var tracker = NewTracker();
        await using var provider = BuildProvider(httpClients, tracker);
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
        var queue = new OperationWaitlistService(
            tracker,
            conflictChecker,
            NullLogger<OperationWaitlistService>.Instance);

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
            CancellationToken.None, new GameImageFetchRequestKey(RefreshEpicImageUrls: false));

        Assert.Equal(OperationStartStatus.Waiting, answer.Status);
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
        var queue = new OperationWaitlistService(tracker, conflictChecker, NullLogger<OperationWaitlistService>.Instance);
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
            CancellationToken.None, new GameImageFetchRequestKey(RefreshEpicImageUrls: false));
        Assert.Equal(OperationStartStatus.Waiting, parked.Status);
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
        var tracker = NewTracker();
        await using var services = await BuildServicesWithADownloadAsync(passes, tracker);
        var notifications = HoldingNotifications.Create();
        var service = NewService(services, tracker, notifications.Service);
        var queue = services.GetRequiredService<IOperationWaitlist>();

        var running = await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled);
        Assert.NotNull(running);
        Assert.True(await notifications.WaitForHeldProgressAsync(Arrival), "the running pass never reported progress");

        var refill = await ParkRefillAsync(service, queue);

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
        var tracker = NewTracker();
        await using var services = await BuildServicesWithADownloadAsync(passes, tracker);
        var notifications = HoldingNotifications.Create();
        var service = NewService(services, tracker, notifications.Service);
        var queue = services.GetRequiredService<IOperationWaitlist>();

        using var stopping = new CancellationTokenSource();
        var scheduledRun = (Task)typeof(ScopedScheduledBackgroundService)
            .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(CancellationToken)])!
            .Invoke(service, [stopping.Token])!;
        Assert.True(await notifications.WaitForHeldProgressAsync(Arrival), "the scheduled pass never reported progress");
        var canceledPass = Assert.Single(tracker.GetActiveOperations(OperationType.GameImageFetch)).Id;

        Assert.Null(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));

        var refill = await ParkRefillAsync(service, queue);

        // A broadcast to browsers takes longer than the queue's in-process promotion. Holding the
        // canceled pass's ending broadcast until the queue has handed the refill on makes that the
        // order on every run instead of a thread-pool race.
        notifications.HoldFirstCompletionUntil(() => tracker.GetOperation(refill)?.Status != OperationStatus.Waiting);

        await stopping.CancelAsync();
        notifications.ReleaseProgress();

        await AssertTheRefillRanNextWithTheEpicRefreshAsync(passes, tracker, refill);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduledRun);
        Assert.Equal(OperationStatus.Canceled, tracker.GetOperation(canceledPass)!.Status);
    }

    /// <summary>
    /// Refresh banners backdates every stored row and the Epic URL refresh moves the mapping to Epic's
    /// current art. The stale phase must fetch an Epic banner from that current URL, not from the one
    /// stored at its first fetch, and store the new URL on the row.
    /// </summary>
    [Fact]
    public async Task ABackdatedEpicBannerIsFetchedFromItsMappingsCurrentUrlAsync()
    {
        const string storedUrl = "https://cdn.example/old-art.jpg";
        const string currentUrl = "https://cdn.example/new-art.jpg";
        var requests = new UrlRecorder();
        var databaseName = $"game-image-epic-url-{Guid.NewGuid():N}";
        var collection = new ServiceCollection();
        collection.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
        collection.AddSingleton<IHttpClientFactory>(requests);
        await using var services = collection.BuildServiceProvider();

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Downloads.Add(new Download { Service = "origin", ClientIp = "10.0.0.1" });
            db.EpicGameMappings.Add(new EpicGameMapping { AppId = "epic-app", Name = "Epic Game", ImageUrl = currentUrl });
            db.GameImages.Add(new GameImage
            {
                AppId = "epic-app",
                Service = "epicgames",
                ImageData = [1, 2, 3],
                SourceUrl = storedUrl,
                FetchedAtUtc = DateTime.UnixEpoch
            });
            await db.SaveChangesAsync();
        }

        var tracker = NewTracker();
        var service = NewService(services, tracker, NullNotifications());
        Assert.NotNull(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));
        await WaitForPassesToFinishAsync(tracker);

        Assert.Equal([currentUrl], requests.Snapshot());
        using (var scope = services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().GameImages.SingleAsync();
            Assert.Equal(currentUrl, row.SourceUrl);
        }
    }

    /// <summary>
    /// A pass that ends while a cache clear runs leaves its follow-up armed. The follow-up must not start
    /// beside the clear: it waits on the queue's card and runs when the clear ends.
    /// </summary>
    [Fact]
    public async Task AFollowUpArmedDuringACacheClearWaitsForTheClearAsync()
    {
        var httpClients = new PausingHttpClientFactory();
        var tracker = NewTracker();
        await using var services = BuildProvider(httpClients, tracker);
        var service = NewService(services, tracker, NullNotifications());

        Assert.NotNull(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));
        Assert.True(await httpClients.WaitForCallAsync(Arrival), "the first pass never reached its fetch");
        var clearing = tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear (All)", new CancellationTokenSource());
        Assert.Null(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));

        httpClients.Release();

        Assert.True(
            SpinWait.SpinUntil(
                () => tracker.GetWaitingOperations().Any(op => op.Type == OperationType.GameImageFetch),
                TickTimeout),
            "the follow-up did not wait on the queue while the cache clear ran");
        Assert.Equal(1, httpClients.Calls);

        tracker.CompleteOperation(clearing, success: true);
        Assert.True(await httpClients.WaitForCallAsync(Arrival), "the waiting follow-up never ran once the clear ended");
        await WaitForPassesToFinishAsync(tracker);
    }

    /// <summary>
    /// The Game Images schedule's tick while a cache clear runs fetches nothing: it waits on one card,
    /// the card on the tick's own notice, and its pass runs when the clear ends.
    /// </summary>
    [Fact]
    public async Task AScheduledTickDuringACacheClearWaitsOnItsOwnNoticeAsync()
    {
        var passes = new PassRecorder();
        var tracker = NewTracker();
        await using var services = await BuildServicesWithADownloadAsync(passes, tracker);
        var service = NewService(services, tracker, NullNotifications());
        var notice = new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled);
        service.SelectRunNotice(notice);
        var clearing = tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear (All)", new CancellationTokenSource());

        await RunTickAsync(service);

        Assert.Empty(passes.Snapshot());
        var waiting = Assert.Single(tracker.GetWaitingOperations());
        Assert.Equal(OperationType.GameImageFetch, waiting.Type);
        Assert.Equal(waiting.Id, notice.OperationId);

        tracker.CompleteOperation(clearing, success: true);
        Assert.True(SpinWait.SpinUntil(() => passes.Snapshot().Length == 1, Arrival), "the waiting pass never ran");
        await WaitForPassesToFinishAsync(tracker);
    }

    /// <summary>
    /// A person's Run Now that waits behind a cache clear runs on the request it waited on, so the
    /// schedule's history names the person who pressed it rather than an unknown actor.
    /// </summary>
    [Fact]
    public async Task AParkedRunNowRecordsThePersonWhoPressedItAsync()
    {
        var passes = new PassRecorder();
        var tracker = NewTracker();
        await using var services = await BuildServicesWithADownloadAsync(passes, tracker);
        var service = NewService(services, tracker, NullNotifications());
        var person = new ScheduleActor(ScheduleActorKind.Account, Guid.NewGuid(), "alice");
        var notice = new RunNotice(NotificationMode.Manual, RunTrigger.Manual, person);
        service.SelectRunNotice(notice);
        var clearing = tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear (All)", new CancellationTokenSource());

        await RunTickAsync(service);
        var waiting = Assert.Single(tracker.GetWaitingOperations());

        tracker.CompleteOperation(clearing, success: true);
        Assert.True(SpinWait.SpinUntil(() => passes.Snapshot().Length == 1, Arrival), "the waiting pass never ran");
        await WaitForPassesToFinishAsync(tracker);

        var card = tracker.GetOperation(waiting.Id)!;
        Assert.Equal(OperationStatus.Completed, card.Status);
        var run = tracker.GetOperation(card.NextOperationId!.Value)!;
        Assert.Equal(run.Id, notice.OperationId);
        // The schedule history row the run's end writes, read the way the schedule registry reads it.
        var row = new ScheduleExecutionService(null!, NullLogger<ScheduleExecutionService>.Instance)
            .Capture(run, "gameImageFetch");
        Assert.Equal(ScheduleActorKind.Account, row.ActorKind);
        Assert.Equal(person.AccountId, row.AccountId);
        Assert.Equal(person.Username, row.Username);
        Assert.Equal(RunTrigger.Manual, row.Trigger);
    }

    /// <summary>
    /// A Run Now pressed while an automatic pass waits behind a cache clear joins that waiting pass,
    /// and the pass it runs is recorded under the person and their click, not as the server's own run.
    /// </summary>
    [Fact]
    public async Task ARunNowJoinedToAParkedAutomaticPassRecordsThePersonAsync()
    {
        var passes = new PassRecorder();
        var tracker = NewTracker();
        await using var services = await BuildServicesWithADownloadAsync(passes, tracker);
        var service = NewService(services, tracker, NullNotifications());
        var clearing = tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear (All)", new CancellationTokenSource());
        await service.StartFetchThroughQueueAsync().WaitAsync(TickTimeout);
        var waiting = Assert.Single(tracker.GetWaitingOperations());

        var person = new ScheduleActor(ScheduleActorKind.Account, Guid.NewGuid(), "alice");
        service.SelectRunNotice(new RunNotice(NotificationMode.Manual, RunTrigger.Manual, person));
        await RunTickAsync(service);
        Assert.Equal(waiting.Id, Assert.Single(tracker.GetWaitingOperations()).Id);

        tracker.CompleteOperation(clearing, success: true);
        Assert.True(SpinWait.SpinUntil(() => passes.Snapshot().Length == 1, Arrival), "the waiting pass never ran");
        await WaitForPassesToFinishAsync(tracker);

        var card = tracker.GetOperation(waiting.Id)!;
        Assert.Equal(OperationStatus.Completed, card.Status);
        var row = new ScheduleExecutionService(null!, NullLogger<ScheduleExecutionService>.Instance)
            .Capture(tracker.GetOperation(card.NextOperationId!.Value)!, "gameImageFetch");
        Assert.Equal(ScheduleActorKind.Account, row.ActorKind);
        Assert.Equal(person.AccountId, row.AccountId);
        Assert.Equal(person.Username, row.Username);
        Assert.Equal(RunTrigger.Manual, row.Trigger);
    }

    /// <summary>
    /// A tick that finds a banner pass already running does nothing and shows no card, as a tick that
    /// finds the execution lock held always has.
    /// </summary>
    [Fact]
    public async Task AScheduledTickDuringARunningPassAddsNoCardAsync()
    {
        var httpClients = new PausingHttpClientFactory();
        var tracker = NewTracker();
        await using var services = BuildProvider(httpClients, tracker);
        var service = NewService(services, tracker, NullNotifications());
        service.SelectRunNotice(new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled));
        var running = await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled);
        Assert.NotNull(running);
        Assert.True(await httpClients.WaitForCallAsync(Arrival), "the running pass never reached its fetch");

        await RunTickAsync(service);

        Assert.Equal(running, Assert.Single(tracker.GetRuns().Runs).OperationId);
        Assert.Empty(tracker.GetWaitingOperations());
        httpClients.Release();
        await WaitForPassesToFinishAsync(tracker);
    }

    /// <summary>
    /// A routine tick with no downloads yet stays quiet: no card, nothing parked.
    /// </summary>
    [Fact]
    public async Task AScheduledTickWithNothingToFetchShowsNoCardAsync()
    {
        var httpClients = new PausingHttpClientFactory();
        httpClients.Release();
        var tracker = NewTracker();
        await using var services = BuildProvider(httpClients, tracker);
        var service = NewService(services, tracker, NullNotifications());
        service.SelectRunNotice(new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled));

        await RunTickAsync(service);

        Assert.Empty(tracker.GetRuns().Runs);
        Assert.Empty(tracker.GetWaitingOperations());
    }

    /// <summary>
    /// A tick with work shows its card at once, and the queue's lock is let go once the card has
    /// started: a second tick sent while the pass runs answers instead of waiting for the pass to end.
    /// </summary>
    [Fact]
    public async Task AScheduledTickLetsGoOfTheQueueOnceItsCardStartsAsync()
    {
        var passes = new PassRecorder();
        var tracker = NewTracker();
        await using var services = await BuildServicesWithADownloadAsync(passes, tracker);
        var notifications = HoldingNotifications.Create();
        var service = NewService(services, tracker, notifications.Service);
        service.SelectRunNotice(new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled));

        var first = RunTickAsync(service);
        Assert.True(await notifications.WaitForHeldProgressAsync(Arrival), "the scheduled pass never reported progress");
        Assert.Single(tracker.GetActiveOperations(OperationType.GameImageFetch));

        await RunTickAsync(service);

        notifications.ReleaseProgress();
        await first;
        Assert.Single(tracker.GetRuns().Runs);
        Assert.Empty(tracker.GetWaitingOperations());
        await WaitForPassesToFinishAsync(tracker);
    }

    /// <summary>
    /// An automatic banner pass blocked by another job is handed to the queue to wait on a card, and
    /// nothing starts while it waits.
    /// </summary>
    [Fact]
    public async Task AnAutomaticPassBlockedByAnotherJobWaitsOnTheQueueAsync()
    {
        var httpClients = new PausingHttpClientFactory();
        httpClients.Release();
        var tracker = NewTracker();
        var queue = new ConflictingQueue();
        var collection = new ServiceCollection();
        collection.AddDbContext<AppDbContext>(
            options => options.UseInMemoryDatabase($"game-image-fetch-{Guid.NewGuid():N}"));
        collection.AddSingleton<IHttpClientFactory>(httpClients);
        collection.AddSingleton<IOperationWaitlist>(queue);
        await using var services = collection.BuildServiceProvider();
        var service = NewService(services, tracker, NullNotifications());

        await service.StartFetchThroughQueueAsync().WaitAsync(TickTimeout);

        Assert.Equal(OperationType.GameImageFetch, queue.Type);
        Assert.Equal(ConflictScope.Bulk(), queue.Scope);
        Assert.Equal(RunTrigger.Scheduled, Assert.IsType<RunNotice>(queue.Notice).Trigger);
        Assert.Empty(tracker.GetRuns().Runs);
        Assert.Equal(0, httpClients.Calls);
    }

    /// <summary>
    /// A Clear image cache pressed while an automatic pass waits behind another job does not join that
    /// pass, which skips the Epic URL refresh the refill needs: it waits on its own card.
    /// </summary>
    [Fact]
    public async Task ARefillDoesNotJoinAWaitingAutomaticPassAsync()
    {
        var httpClients = new PausingHttpClientFactory();
        httpClients.Release();
        var tracker = NewTracker();
        await using var services = BuildProvider(httpClients, tracker);
        var service = NewService(services, tracker, NullNotifications());
        var queue = services.GetRequiredService<IOperationWaitlist>();
        var clearing = tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear (All)", new CancellationTokenSource());

        await service.StartFetchThroughQueueAsync().WaitAsync(TickTimeout);
        var automatic = Assert.Single(tracker.GetWaitingOperations()).Id;
        var refill = await ParkRefillAsync(service, queue);

        Assert.NotEqual(automatic, refill);
        Assert.Equal(2, tracker.GetWaitingOperations().Count(operation => operation.Type == OperationType.GameImageFetch));

        foreach (var waiting in tracker.GetWaitingOperations().ToList())
            tracker.CancelOperation(waiting.Id);
        tracker.CompleteOperation(clearing, success: true);
        await WaitForPassesToFinishAsync(tracker);
    }

    /// <summary>
    /// An automatic banner pass asked for while a pass runs is carried by that pass's follow-up flag,
    /// as a direct start is, and shows no waiting card of its own.
    /// </summary>
    [Fact]
    public async Task AnAutomaticPassDuringARunningPassRidesItsFollowUpAsync()
    {
        var httpClients = new PausingHttpClientFactory();
        var tracker = NewTracker();
        await using var services = BuildProvider(httpClients, tracker);
        var service = NewService(services, tracker, NullNotifications());
        Assert.NotNull(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));
        Assert.True(await httpClients.WaitForCallAsync(Arrival), "the running pass never reached its fetch");

        await service.StartFetchThroughQueueAsync().WaitAsync(TickTimeout);

        Assert.Empty(tracker.GetWaitingOperations());
        var followUp = typeof(GameImageFetchService).GetField("_followUpPassWanted", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(1, (int)followUp.GetValue(null)!);

        httpClients.Release();
        Assert.True(await httpClients.WaitForCallAsync(Arrival), "the follow-up pass never ran");
        await WaitForPassesToFinishAsync(tracker);
    }

    /// <summary>
    /// Refresh banners pressed while a cache clear runs, and the tab closed before the answer: the
    /// request is aborted, and the refill still waits for the clear on a card instead of running
    /// beside it. It runs, with the Epic URL refresh, once the clear ends.
    /// </summary>
    [Fact]
    public async Task ARefillWhoseRequestWasAbortedWaitsForTheCacheClearAsync()
    {
        var passes = new PassRecorder();
        var tracker = NewTracker();
        await using var services = await BuildServicesWithADownloadAsync(passes, tracker);
        var service = NewService(services, tracker, NullNotifications());
        var clearing = tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear (All)", new CancellationTokenSource());
        var controller = new GameImagesController(
            NullLogger<GameImagesController>.Instance,
            NullProxy<IImageCacheService>(),
            service,
            null!,
            services.GetRequiredService<IOperationWaitlist>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var thrown = await Record.ExceptionAsync(() => ClearAsAbortedRequestAsync(controller));

        await Task.Delay(Quiet);
        Assert.Empty(passes.Snapshot());
        Assert.Equal(OperationType.GameImageFetch, Assert.Single(tracker.GetWaitingOperations()).Type);
        Assert.Null(thrown);

        tracker.CompleteOperation(clearing, success: true);
        Assert.True(SpinWait.SpinUntil(() => passes.Snapshot().Length == 1, Arrival), "the waiting refill never ran");
        Assert.True(passes.Snapshot()[0], "the refill ran without the Epic URL refresh");
        await WaitForPassesToFinishAsync(tracker);
    }

    /// <summary>
    /// A scheduled tick reads the Downloads count before it takes the operation queue's lock, so
    /// another job starting through the queue never waits on that query.
    /// </summary>
    [Fact]
    public async Task AScheduledTickCountsDownloadsWithoutHoldingTheQueueAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var count = new HeldDownloadsCount();
        var tracker = NewTracker();
        var collection = new ServiceCollection();
        collection.AddDbContext<AppDbContext>(
            options => options.UseNpgsql(database.ConnectionString).AddInterceptors(count));
        collection.AddSingleton<IHttpClientFactory>(new PassRecorder());
        AddOperationWaitlist(collection, tracker);
        await using var services = collection.BuildServiceProvider();
        var service = NewService(services, tracker, NullNotifications());
        service.SelectRunNotice(new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled));

        var tick = RunTickAsync(service);
        Assert.True(await count.WaitUntilHeldAsync(Arrival), "the tick never counted downloads");

        var otherStart = services.GetRequiredService<IOperationWaitlist>().StartUnlessBusyAsync(
            OperationType.CacheClearing, ConflictScope.Bulk(), () => Task.FromResult<Guid?>(null), CancellationToken.None, OperationRequestKey.Plain);
        var answered = await Task.WhenAny(otherStart, Task.Delay(Quiet)) == otherStart;

        count.Release();
        await tick;
        await otherStart.WaitAsync(TickTimeout);
        Assert.True(answered, "another job's start waited for the tick's Downloads count");
    }

    /// <summary>
    /// A tick blocked by another job that is gone by the time the queue re-checks is started by the
    /// queue at once, and the tick ends only when that pass ends, as an unblocked tick does.
    /// </summary>
    [Fact]
    public async Task AScheduledTickTheQueueStartsOnItsRecheckEndsWithItsPassAsync()
    {
        var httpClients = new PausingHttpClientFactory();
        var tracker = NewTracker();
        var collection = new ServiceCollection();
        collection.AddDbContext<AppDbContext>(
            options => options.UseInMemoryDatabase($"game-image-fetch-{Guid.NewGuid():N}"));
        collection.AddSingleton<IHttpClientFactory>(httpClients);
        collection.AddSingleton<IOperationWaitlist>(new BlockerGoneOnRecheckQueue(new OperationWaitlistService(
            tracker,
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationWaitlistService>.Instance)));
        await using var services = collection.BuildServiceProvider();
        var service = NewService(services, tracker, NullNotifications());
        service.SelectRunNotice(new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled));

        var tick = RunTickAsync(service);
        Assert.True(await httpClients.WaitForCallAsync(Arrival), "the pass the queue started never reached its fetch");
        await Task.Delay(Quiet);
        var endedBeforeItsPass = tick.IsCompleted;

        httpClients.Release();
        await tick;
        await WaitForPassesToFinishAsync(tracker);
        Assert.False(endedBeforeItsPass, "the tick ended while the pass it started was still running");
    }

    /// <summary>
    /// Calls the route the way ASP.NET does for a request the browser has abandoned: the request's
    /// aborted token is canceled and bound to any token parameter the action declares.
    /// </summary>
    internal static async Task<IActionResult> ClearAsAbortedRequestAsync(GameImagesController controller)
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        controller.ControllerContext.HttpContext.RequestAborted = aborted.Token;
        var action = typeof(GameImagesController).GetMethod(nameof(GameImagesController.ClearImageCacheAsync))!;
        var arguments = action.GetParameters()
            .Select(object? (ParameterInfo parameter) =>
                parameter.ParameterType == typeof(CancellationToken) ? aborted.Token : parameter.DefaultValue)
            .ToArray();
        return await (Task<IActionResult>)action.Invoke(controller, arguments)!;
    }

    // The schedule's tick: opens a scope and runs the pass in it, as the base loop does.
    private static Task RunTickAsync(GameImageFetchService service) =>
        ((Task)typeof(ScopedScheduledBackgroundService)
            .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(CancellationToken)])!
            .Invoke(service, [CancellationToken.None])!)
            .WaitAsync(TickTimeout);

    private static ServiceProvider BuildProvider(IHttpClientFactory httpClients, UnifiedOperationTracker tracker)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(
            options => options.UseInMemoryDatabase($"game-image-fetch-{Guid.NewGuid():N}"));
        services.AddSingleton(httpClients);
        AddOperationWaitlist(services, tracker);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The queue a follow-up pass and a scheduled tick start through, on the tracker the service reports
    /// to, so a conflict it finds is the one the test registered.
    /// </summary>
    private static void AddOperationWaitlist(IServiceCollection services, UnifiedOperationTracker tracker)
        => services.AddSingleton<IOperationWaitlist>(new OperationWaitlistService(
            tracker,
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationWaitlistService>.Instance));

    /// <summary>
    /// One download with no game identity gets every pass past the empty-table exit and gives no
    /// phase anything to fetch, so each pass runs all four phases and finishes. No Epic account is
    /// signed in, so a pass that asks for the Epic service records the request and skips the refresh.
    /// </summary>
    private static async Task<ServiceProvider> BuildServicesWithADownloadAsync(PassRecorder passes, UnifiedOperationTracker tracker)
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
        AddOperationWaitlist(collection, tracker);
        var services = collection.BuildServiceProvider();

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Downloads.Add(new Download { Service = "origin", ClientIp = "10.0.0.1" });
        await db.SaveChangesAsync();
        return services;
    }

    /// <summary>
    /// Presses Refresh banners through the controller, whose gated start finds the running pass and
    /// parks the refill on the queue.
    /// </summary>
    private static async Task<Guid> ParkRefillAsync(GameImageFetchService service, IOperationWaitlist queue)
    {
        var controller = new GameImagesController(
            NullLogger<GameImagesController>.Instance,
            NullProxy<IImageCacheService>(),
            service,
            null!,
            queue)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var accepted = Assert.IsType<AcceptedResult>(await controller.ClearImageCacheAsync());
        var parked = Assert.IsType<OperationStartResponse>(accepted.Value);
        Assert.Equal(OperationStartStatus.Waiting, parked.Status);
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
    /// A queue whose gated start always meets a running cache clear, recording the park it is then
    /// asked for.
    /// </summary>
    private sealed class ConflictingQueue : IOperationWaitlist
    {
        public OperationType? Type { get; private set; }
        public ConflictScope? Scope { get; private set; }
        public RunNotice? Notice { get; private set; }

        public Task<OperationStartResponse> EnqueueAsync(
            OperationType type,
            ConflictScope scope,
            string displayName,
            Func<Task<Guid?>> start,
            CancellationToken ct,
            OperationRequestKey requestKey,
            bool reportRefusal = false,
            RunNotice? notice = null,
            string? target = null)
        {
            Type = type;
            Scope = scope;
            Notice = notice;
            return Task.FromResult(new OperationStartResponse
            {
                OperationId = Guid.NewGuid(),
                Status = OperationStartStatus.Waiting
            });
        }

        public Task<OperationConflictResponse?> StartUnlessBusyAsync(
            OperationType type, ConflictScope scope, Func<Task<Guid?>> start, CancellationToken ct, OperationRequestKey requestKey)
            => Task.FromResult<OperationConflictResponse?>(ClearConflict());
    }

    /// <summary>
    /// The answer a gated start gets while a cache clear runs: the clear blocks every other job.
    /// </summary>
    private static OperationConflictResponse ClearConflict() => new()
    {
        StageKey = "errors.conflict.globalOperationActive",
        Error = "blocked",
        ActiveOperationId = Guid.NewGuid(),
        ActiveOperationType = nameof(OperationType.CacheClearing)
    };

    /// <summary>
    /// A queue whose gated start meets a cache clear that has ended by the time the request is parked:
    /// the park goes to the real queue, whose re-check finds nothing in the way and starts at once.
    /// </summary>
    private sealed class BlockerGoneOnRecheckQueue(IOperationWaitlist queue) : IOperationWaitlist
    {
        public Task<OperationStartResponse> EnqueueAsync(
            OperationType type,
            ConflictScope scope,
            string displayName,
            Func<Task<Guid?>> start,
            CancellationToken ct,
            OperationRequestKey requestKey,
            bool reportRefusal = false,
            RunNotice? notice = null,
            string? target = null)
            => queue.EnqueueAsync(type, scope, displayName, start, ct, requestKey, reportRefusal, notice, target);

        public Task<OperationConflictResponse?> StartUnlessBusyAsync(
            OperationType type, ConflictScope scope, Func<Task<Guid?>> start, CancellationToken ct, OperationRequestKey requestKey)
            => Task.FromResult<OperationConflictResponse?>(ClearConflict());
    }

    /// <summary>
    /// Holds the first count of the Downloads table at the database until the test lets it go, so a
    /// test can ask what else waits while that query runs.
    /// </summary>
    private sealed class HeldDownloadsCount : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _counts;

        public async Task<bool> WaitUntilHeldAsync(TimeSpan timeout)
            => await Task.WhenAny(_held.Task, Task.Delay(timeout)) == _held.Task;

        public void Release() => _released.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData evt,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await HoldFirstCountAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData evt,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            await HoldFirstCountAsync(command);
            return result;
        }

        private async Task HoldFirstCountAsync(DbCommand command)
        {
            if (command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("\"Downloads\"", StringComparison.Ordinal)
                && Interlocked.Increment(ref _counts) == 1)
            {
                _held.TrySetResult();
                await _released.Task.WaitAsync(Arrival);
            }
        }
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
    /// Answers every request with an image large enough to pass the size gate and records each URL
    /// asked for.
    /// </summary>
    private sealed class UrlRecorder : HttpMessageHandler, IHttpClientFactory
    {
        private readonly ConcurrentQueue<string> _urls = new();

        public string[] Snapshot() => _urls.ToArray();

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _urls.Enqueue(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[10_000])
            });
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
