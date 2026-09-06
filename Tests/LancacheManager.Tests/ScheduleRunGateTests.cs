using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the one question every schedule asks before it runs, and the identical way each caller
/// treats the answer: the loop declines before it touches any run state, the startup pass declines
/// the same way, a manual Run Now never arms a run it cannot start, and Run all counts the refusals
/// while still triggering everything that can run.
///
/// Every gate installed here answers null for a key it was not given, because the static hook is
/// process-wide and services under test in other classes must keep running normally.
/// </summary>
[Collection(nameof(DownloadsEndedEventCollection))]
public class ScheduleRunGateTests
{
    private const string DownloadReason = "A client download is writing to the cache right now.";
    private const string EvictionKey = "cacheReconciliation";

    [Fact]
    public async Task DeclinedRun_LeavesLastRunUtcAndTheRunningFlagUntouchedAsync()
    {
        using var service = new RunGateProbeService(EvictionKey);
        var stampedBefore = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        service.SetLastRunUtc(stampedBefore);

        var (shuttingDown, runFailed) = await WithGateAsync(
            DeclineOnly(EvictionKey),
            () => service.InvokeRunScheduledWorkAsync(RunTrigger.Scheduled, CancellationToken.None));

        Assert.False(shuttingDown);
        Assert.False(runFailed);
        Assert.Equal(stampedBefore, service.LastRunUtc);
        Assert.False(service.IsCurrentlyExecuting);
        Assert.False(service.WorkRan);
        Assert.False(service.EndBroadcast);
    }

    [Fact]
    public async Task AllowedRun_StampsLastRunUtcSoTheDeclineIsWhatSpareditAsync()
    {
        using var service = new RunGateProbeService(EvictionKey);
        var stampedBefore = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        service.SetLastRunUtc(stampedBefore);

        await WithGateAsync(
            DeclineOnly("someOtherKey"),
            () => service.InvokeRunScheduledWorkAsync(RunTrigger.Scheduled, CancellationToken.None));

        Assert.True(service.WorkRan);
        Assert.True(service.EndBroadcast);
        Assert.NotEqual(stampedBefore, service.LastRunUtc);
    }

    [Fact]
    public async Task DeclinedRun_IsNotAFailureSoTheLoopTakesItsOrdinarySleepAsync()
    {
        using var service = new RunGateProbeService(EvictionKey);

        var started = DateTime.UtcNow;
        var (_, runFailed) = await WithGateAsync(
            DeclineOnly(EvictionKey),
            () => service.InvokeRunScheduledWorkAsync(RunTrigger.Scheduled, CancellationToken.None));

        // RunFailed false is what sends the caller to its interval sleep instead of ErrorRetryDelay,
        // and returning without waiting is what stops a long download costing one attempt a minute.
        Assert.False(runFailed);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DeclinedStartupRun_DoesNotExecuteStartupWorkAsync()
    {
        using var allowed = new StartupGateProbeService(EvictionKey);
        await WithGateAsync(DeclineOnly("someOtherKey"), async () =>
        {
            await allowed.StartAsync(CancellationToken.None);
            await allowed.StartupRan.WaitAsync(TimeSpan.FromSeconds(5));
            await allowed.StopAsync(CancellationToken.None);
            return true;
        });

        using var declined = new StartupGateProbeService(EvictionKey);
        await WithGateAsync(DeclineOnly(EvictionKey), async () =>
        {
            await declined.StartAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            await declined.StopAsync(CancellationToken.None);
            return true;
        });

        Assert.True(allowed.StartupRan.IsCompletedSuccessfully);
        Assert.False(declined.StartupRan.IsCompleted);
        Assert.Null(declined.LastRunUtc);
    }

    [Fact]
    public async Task ServiceThatProducesTheDownloadSignal_IsNeverBlockedByItAsync()
    {
        // The speed tracker runs on the same base class as the eight schedules. A gate that answered
        // for every subclass would stop the one service that reports whether a download is running,
        // exactly while one is.
        using var speedTracker = new RunGateProbeService(nameof(RustSpeedTrackerService));

        await WithGateAsync(
            DeclineOnly(EvictionKey),
            () => speedTracker.InvokeRunScheduledWorkAsync(RunTrigger.Scheduled, CancellationToken.None));

        Assert.True(speedTracker.WorkRan);
    }

    [Fact]
    public async Task RefusedRunNow_DoesNotArmThePendingRunAndReportsTheReasonAsync()
    {
        using var service = new RunGateProbeService(EvictionKey);
        var registry = CreateRegistry(service, CacheScanGateHarness.Downloading());

        var (status, skippedReason) = await registry.TriggerRunAsync(EvictionKey);

        Assert.NotNull(skippedReason);
        Assert.False(status.IsRunning);
        Assert.False(service.HasPendingRun);
    }

    [Fact]
    public async Task AcceptedRunNow_ArmsThePendingRunAndReportsNoReasonAsync()
    {
        using var service = new RunGateProbeService(EvictionKey);
        var registry = CreateRegistry(service, CacheScanGateHarness.Idle());

        var (status, skippedReason) = await registry.TriggerRunAsync(EvictionKey);

        Assert.Null(skippedReason);
        Assert.False(status.IsRunning);
        Assert.True(service.HasPendingRun);
    }

    [Fact]
    public async Task RunNow_ReportsAlreadyRunningWhileTheServicesOperationIsLiveAsync()
    {
        using var service = new RunGateProbeService(EvictionKey);
        var tracker = CreateRealTracker();
        var registry = CreateRegistry(service, CacheScanGateHarness.Idle(), tracker);

        using var cts = new CancellationTokenSource();
        tracker.RegisterOperation(OperationType.EvictionScan, EvictionKey, cts);

        var (status, skippedReason) = await registry.TriggerRunAsync(EvictionKey);

        Assert.Null(skippedReason);
        Assert.True(status.IsRunning);
    }

    /// <summary>
    /// Only a schedule whose work walks the cache tree waits for the tracker before its startup run
    /// is asked about. Everything else that runs on startup, the live log monitor and the dashboard
    /// warmer among them, has to start as promptly as it did.
    /// </summary>
    [Fact]
    public async Task OnlyACacheReadingScheduleWaitsForTheTrackerAsync()
    {
        using var service = new RunGateProbeService(EvictionKey);

        // A tracker that has not published yet, so there is something to wait for.
        var tracker = CacheScanGateHarness.TrackerWith(new DownloadSpeedSnapshot(), []);
        CacheScanGateHarness.SetField(tracker, "_unreportedSinceUtc", DateTime.UtcNow);

        var previousGate = ScheduledServiceBase.ScheduleRunGate;
        var previousWait = ScheduledServiceBase.WaitForDownloadAnswer;
        try
        {
            // Constructed here rather than through the helper, which restores the hooks: this test
            // is about the wait the registry installs, so the installed one has to stay put.
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                CreateDefaultProxy<ISignalRNotificationService>(),
                CreateRealTracker(),
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.GateOver(tracker));

            var wait = ScheduledServiceBase.WaitForDownloadAnswer!;

            // Log rotation never touches the cache tree, so it is not held up for an answer that
            // could not refuse it anyway.
            Assert.True(wait("logRotation", CancellationToken.None).IsCompleted);

            using var cancel = new CancellationTokenSource();
            var waiting = wait(EvictionKey, cancel.Token);
            Assert.False(waiting.IsCompleted);

            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previousGate;
            ScheduledServiceBase.WaitForDownloadAnswer = previousWait;
        }
    }

    [Fact]
    public async Task RunAll_CountsTheRefusalsAndStillTriggersEverythingElseAsync()
    {
        using var refused = new RunGateProbeService(EvictionKey);
        using var allowed = new RunGateProbeService("logRotation");
        var registry = CreateRegistry(
            new ScheduledBackgroundService[] { refused, allowed },
            CacheScanGateHarness.Downloading(),
            CreateRealTracker());

        var (triggeredCount, alreadyRunningCount, skippedCount, skippedReason) =
            await registry.TriggerAllAsync();

        // Both are asked the same question. The eviction scan walks the cache tree and declines; log
        // rotation never touches it and runs, so the fan-out is not blocked wholesale and the counts
        // add up to the two it considered.
        Assert.Equal(1, triggeredCount);
        Assert.Equal(0, alreadyRunningCount);
        Assert.Equal(1, skippedCount);
        Assert.NotNull(skippedReason);
        Assert.True(allowed.HasPendingRun);
        Assert.False(refused.HasPendingRun);
    }

    [Theory]
    [InlineData("gameImageFetch")]
    [InlineData("cacheSnapshot")]
    [InlineData("operationHistoryCleanup")]
    [InlineData("logRotation")]
    [InlineData("dashboardCacheWarmer")]
    public async Task JobThatNeverReadsTheCache_RunsWhileADownloadIsWritingAsync(string serviceKey)
    {
        using var service = new RunGateProbeService(serviceKey);
        var registry = CreateRegistry(service, CacheScanGateHarness.Downloading());

        var (_, skippedReason) = await registry.TriggerRunAsync(serviceKey);

        Assert.Null(skippedReason);
        Assert.True(service.HasPendingRun);
    }

    [Theory]
    [InlineData("cacheReconciliation")]
    [InlineData("cacheSizeScan")]
    [InlineData("gameDetection")]
    public async Task CacheScan_DeclinesWhileADownloadIsWritingAsync(string serviceKey)
    {
        using var service = new RunGateProbeService(serviceKey);
        var registry = CreateRegistry(service, CacheScanGateHarness.Downloading());

        var (_, skippedReason) = await registry.TriggerRunAsync(serviceKey);

        Assert.NotNull(skippedReason);
        Assert.False(service.HasPendingRun);
    }

    [Fact]
    public async Task HeldSchedule_ShowsOneWaitingCardPerDownloadAsync()
    {
        using var service = new RunGateProbeService(EvictionKey);
        var announcements = new List<OperationWaitingNotification>();
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && (string?)args?[0] == SignalREvents.OperationWaiting
                && args[1] is OperationWaitingNotification waiting)
            {
                lock (announcements)
                {
                    announcements.Add(waiting);
                }
            }

            return Task.CompletedTask;
        });

        var snapshot = new DownloadSpeedSnapshot();
        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                notifications,
                CreateRealTracker(),
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.With(snapshot));
            var gate = ScheduledServiceBase.ScheduleRunGate!;

            CacheScanGateHarness.MakeBusy(snapshot);
            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            await WaitForCountAsync(announcements, 1);

            // Same download still running: the run is refused again, but it is already held and its
            // card is already on screen, so no second card goes up.
            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            lock (announcements)
            {
                Assert.Single(announcements);
            }

            // Downloads stop, the held run starts and its card closes, so the next download is a new
            // hold and a new card.
            CacheScanGateHarness.MakeIdle(snapshot);
            Assert.Null(gate(EvictionKey, RunTrigger.Scheduled));

            CacheScanGateHarness.MakeBusy(snapshot);
            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            await WaitForCountAsync(announcements, 2);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    [Fact]
    public void ScheduleRefusedForADownload_RunsOnceTheDownloadStops()
    {
        // The whole point of holding the run: a nightly scan refused during a prefill used to slip a
        // full interval. Another schedule asking after the download stops is the backstop path, and
        // it is the one a test can drive without the tracker's own edge.
        using var refused = new RunGateProbeService(EvictionKey);
        using var asksLater = new RunGateProbeService("cacheSizeScan");
        var snapshot = new DownloadSpeedSnapshot();
        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            _ = new ServiceScheduleRegistry(
                [refused, asksLater],
                CacheScanGateHarness.VisibleClientsStateService(),
                CreateDefaultProxy<ISignalRNotificationService>(),
                CreateRealTracker(),
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.With(snapshot));
            var gate = ScheduledServiceBase.ScheduleRunGate!;

            CacheScanGateHarness.MakeBusy(snapshot);
            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            Assert.False(refused.TakePendingDeferredRun());

            CacheScanGateHarness.MakeIdle(snapshot);
            Assert.Null(gate("cacheSizeScan", RunTrigger.Scheduled));

            // The held run is armed on the schedule that was refused, not on the one that asked, and
            // on the deferred flag rather than the Run Now one so it is still reported as Scheduled.
            Assert.True(refused.TakePendingDeferredRun());
            Assert.False(refused.HasPendingRun);
            Assert.False(asksLater.TakePendingDeferredRun());
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    [Fact]
    public async Task RefusedRunNow_IsHeldAndRunsWhenTheCacheIsFreeAsync()
    {
        // A click is held like any other refused run. Run All is why: it refuses several schedules at
        // once and used to report them as simply not run, leaving the person to come back and press
        // it again once the download finished.
        using var service = new RunGateProbeService(EvictionKey);
        using var asksLater = new RunGateProbeService("cacheSizeScan");
        var snapshot = new DownloadSpeedSnapshot();
        var gate = CacheScanGateHarness.With(snapshot);
        CacheScanGateHarness.MakeBusy(snapshot);
        var registry = CreateRegistry([service, asksLater], gate);

        var (_, skippedReason) = await registry.TriggerRunAsync(EvictionKey);
        Assert.NotNull(skippedReason);
        // The answer says the run is kept rather than telling the person to try again, which is what
        // the gate's own sentence does for the controllers.
        Assert.Contains("queued", skippedReason);
        Assert.False(service.HasPendingRun);

        CacheScanGateHarness.MakeIdle(snapshot);
        await registry.TriggerRunAsync("cacheSizeScan");

        // Held on the deferred flag, so the run it eventually gets still reports as Scheduled.
        Assert.True(service.TakePendingDeferredRun());
        Assert.False(service.HasPendingRun);
    }

    [Fact]
    public async Task ManualRunRefusedWhileTheCardIsUp_AddsNoSecondCardAsync()
    {
        // This used to raise a second card for the click, because the first card dismissed itself and
        // going quiet would have left the person with nothing. The card now stays up saying the run is
        // held, so a click while it is showing needs no card of its own - it still gets the reason on
        // the response it is waiting for.
        using var service = new RunGateProbeService(EvictionKey);
        var announcements = new List<OperationWaitingNotification>();
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && (string?)args?[0] == SignalREvents.OperationWaiting
                && args[1] is OperationWaitingNotification waiting)
            {
                lock (announcements)
                {
                    announcements.Add(waiting);
                }
            }

            return Task.CompletedTask;
        });

        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                notifications,
                CreateRealTracker(),
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Downloading());
            var gate = ScheduledServiceBase.ScheduleRunGate!;

            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            await WaitForCountAsync(announcements, 1);

            // A second timer tick stays quiet, and so does the click: one hold, one card.
            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            Assert.NotNull(gate(EvictionKey, RunTrigger.Manual));
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            lock (announcements)
            {
                Assert.Single(announcements);
            }
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    [Fact]
    public async Task DownloadsEndingReleasesTheHold_WithoutWaitingForAScheduleToPollAsync()
    {
        // The gap this covers: a run is held, downloads stop, another download starts, and no schedule
        // asked the gate in between. The tracker sees that edge itself, so the held run is released and
        // its card closed without anyone polling, leaving the next download free to hold it again.
        using var service = new RunGateProbeService(EvictionKey);
        var announcements = new List<OperationWaitingNotification>();
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && (string?)args?[0] == SignalREvents.OperationWaiting
                && args[1] is OperationWaitingNotification waiting)
            {
                lock (announcements)
                {
                    announcements.Add(waiting);
                }
            }

            return Task.CompletedTask;
        });

        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                notifications,
                CreateRealTracker(),
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Downloading());
            var gate = ScheduledServiceBase.ScheduleRunGate!;

            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            await WaitForCountAsync(announcements, 1);

            // Downloads end. Nothing asks the gate, which is exactly the case that used to stay
            // armed-out; the tracker's own edge is what re-arms it.
            RaiseDownloadsEnded();

            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            await WaitForCountAsync(announcements, 2);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    // The event is static and parameterless, so the test raises it the way the tracker does.
    private static void RaiseDownloadsEnded()
    {
        var field = typeof(RustSpeedTrackerService).GetField(
            nameof(RustSpeedTrackerService.DownloadsEnded),
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        ((Action?)field!.GetValue(null))?.Invoke();
    }

    // The tracker fires its terminal emit fire-and-forget, so a count is waited for rather than read.
    private static async Task WaitForCountAsync<T>(List<T> announcements, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (announcements)
            {
                if (announcements.Count >= expected)
                {
                    Assert.Equal(expected, announcements.Count);
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        lock (announcements)
        {
            Assert.Equal(expected, announcements.Count);
        }
    }

    [Fact]
    public async Task RunAll_TriggersEverythingWhileNothingIsDownloadingAsync()
    {
        using var first = new RunGateProbeService(EvictionKey);
        using var second = new RunGateProbeService("logRotation");
        var registry = CreateRegistry(
            new ScheduledBackgroundService[] { first, second },
            CacheScanGateHarness.Idle(),
            CreateRealTracker());

        var (triggeredCount, alreadyRunningCount, skippedCount, skippedReason) =
            await registry.TriggerAllAsync();

        Assert.Equal(2, triggeredCount);
        Assert.Equal(0, alreadyRunningCount);
        Assert.Equal(0, skippedCount);
        Assert.Null(skippedReason);
        Assert.True(first.HasPendingRun);
        Assert.True(second.HasPendingRun);
    }

    [Fact]
    public async Task RefusedScheduledRun_PutsUpTheWaitingCardThatStaysAsync()
    {
        // The card a run blocked by another heavy operation already gets from the queue. A run blocked
        // by a download used to get a terminal one that dismissed itself after a few seconds, so the
        // person was left with nothing on screen saying the run was still coming.
        using var service = new RunGateProbeService(EvictionKey);
        var sent = new TaskCompletionSource<OperationWaitingNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && (string?)args?[0] == SignalREvents.OperationWaiting
                && args[1] is OperationWaitingNotification waiting)
            {
                sent.TrySetResult(waiting);
            }

            return Task.CompletedTask;
        });

        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            // Constructed here rather than through the helper, which restores the hook: this test is
            // about the answer the registry installs, so the installed one has to stay put.
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                notifications,
                CreateRealTracker(),
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Downloading());

            var reason = ScheduledServiceBase.ScheduleRunGate!(EvictionKey, RunTrigger.Scheduled);

            Assert.NotNull(reason);
            var waiting = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(OperationType.EvictionScan.ToWireString(), waiting.OperationType);
            // The name the card reads. The schedule key would render the card as "cacheReconciliation:
            // waiting for ...", which is not what the schedule is called anywhere a person looks.
            Assert.Equal("Eviction Scan", waiting.Name);
            // No prefill is running in this test, so there is no blocker to name and the card falls
            // back to its own wording rather than guessing at the download.
            Assert.Null(waiting.BlockedByName);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    [Fact]
    public async Task RefusalAtPromotion_CompletesAsSkippedRatherThanFailedAsync()
    {
        var (status, waitingComplete) =
            await PromoteWithStartFailureAsync(new DownloadInProgressException(DownloadReason));

        Assert.Equal(OperationStatus.Skipped, status.Status);
        // A translation key, not the gate's sentence. The card renders whatever lands here through
        // i18next, and the gate's own wording is written for an HTTP caller in English.
        Assert.Equal(CacheScanGate.ScheduleQueuedReasonKey, status.Message);

        // The waiting card must be told the run was declined, not that something took it over.
        // Promoted removes the card without reading the reason, so the two cannot both be true.
        Assert.NotNull(waitingComplete);
        Assert.True(waitingComplete!.Skipped);
        Assert.False(waitingComplete.Promoted);
        Assert.Equal(CacheScanGate.ScheduleQueuedReasonKey, waitingComplete.Error);
    }

    [Fact]
    public async Task RefusalOnTheImmediatePath_StillReachesTheCallerAsync()
    {
        // Nothing is queued ahead of it, so the start delegate runs inline. A refusal there has to
        // come back out: the HTTP caller is waiting on it and its 400 with the reason is the answer,
        // and swallowing it here would take that away.
        //
        // It must ALSO stay quiet by default. The caller renders the exception, so announcing as
        // well puts two notices on screen for one click, the second of them describing the
        // scheduled run rather than what was clicked.
        var announcements = new List<string>();
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && args?[1] is ScheduledRunCompleteEvent)
            {
                lock (announcements)
                {
                    announcements.Add((string)args[0]!);
                }
            }

            return Task.CompletedTask;
        });

        var tracker = CreateRealTracker();
        var registryNotifications = notifications;
        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            using var service = new RunGateProbeService("gameDetection");
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                registryNotifications,
                tracker,
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Idle());

            var queue = new OperationQueueService(
                tracker,
                new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance),
                CreateDefaultProxy<ISignalRNotificationService>(),
                NullLogger<OperationQueueService>.Instance);

            var refused = await Assert.ThrowsAsync<DownloadInProgressException>(
                () => queue.EnqueueAsync(
                    OperationType.GameDetection,
                    ConflictScope.Bulk(),
                    "Game Detection",
                    () => throw new DownloadInProgressException(DownloadReason),
                    CancellationToken.None));

            Assert.Equal(DownloadReason, refused.Message);
            Assert.Empty(tracker.GetWaitingOperations());

            await Task.Delay(TimeSpan.FromMilliseconds(250));
            lock (announcements)
            {
                Assert.Empty(announcements);
            }
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    [Fact]
    public async Task RefusalOnTheImmediatePath_IsAnnouncedThroughTheTrackerAsync()
    {
        // The queue has no idea which card a schedule owns and is not given one. It reports the
        // refusal on the tracker, and the registry, already subscribed to that terminal hook, turns
        // it into the same card a refused scheduled run produces.
        var announced = new TaskCompletionSource<ScheduledRunCompleteEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && (string?)args?[0] == SignalREvents.GameDetectionComplete
                && args[1] is ScheduledRunCompleteEvent complete)
            {
                announced.TrySetResult(complete);
            }

            return Task.CompletedTask;
        });

        var tracker = CreateRealTracker();
        using var service = new RunGateProbeService("gameDetection");
        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                notifications,
                tracker,
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Idle());

            var queue = new OperationQueueService(
                tracker,
                new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance),
                CreateDefaultProxy<ISignalRNotificationService>(),
                NullLogger<OperationQueueService>.Instance);

            await Assert.ThrowsAsync<DownloadInProgressException>(
                () => queue.EnqueueAsync(
                    OperationType.GameDetection,
                    ConflictScope.Bulk(),
                    "Game Detection",
                    () => throw new DownloadInProgressException(DownloadReason),
                    CancellationToken.None,
                    reportRefusal: true));

            var terminal = await announced.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(OperationStatus.Skipped, terminal.Status);
            Assert.Equal(CacheScanGate.ScheduleQueuedReasonKey, terminal.Error);
            Assert.True(terminal.ShowNotification);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    [Fact]
    public async Task RealFailureAtPromotion_StillCompletesAsFailedAsync()
    {
        var (status, _) = await PromoteWithStartFailureAsync(new InvalidOperationException("the worker broke"));

        Assert.Equal(OperationStatus.Failed, status.Status);
        Assert.Equal("the worker broke", status.Message);
    }

    [Fact]
    public async Task StatedPreconditionThatIsNotADownload_StillCompletesAsFailedAsync()
    {
        // A write-permission re-check on a wrong PUID or PGID, and a datasource that cannot map
        // logical objects, both throw the base ValidationException at promotion. Reporting those as
        // skips would leave a misconfigured install doing nothing and saying nothing.
        var (status, waitingComplete) =
            await PromoteWithStartFailureAsync(new ValidationException("Cannot write to the cache directory"));

        Assert.Equal(OperationStatus.Failed, status.Status);
        Assert.Equal("Cannot write to the cache directory", status.Message);
        Assert.NotNull(waitingComplete);
        Assert.False(waitingComplete!.Skipped);
        Assert.False(waitingComplete.Promoted);
    }

    /// <summary>
    /// Parks a request behind a live operation, then finishes the blocker so the queue promotes the
    /// waiter and its start delegate throws <paramref name="startFailure"/>. Returns the waiting
    /// operation's terminal state.
    /// </summary>
    private static async Task<(OperationInfo Status, OperationWaitingCompleteNotification? WaitingComplete)>
        PromoteWithStartFailureAsync(Exception startFailure)
    {
        var tracker = CreateRealTracker();
        var conflictChecker = new OperationConflictChecker(
            tracker, NullLogger<OperationConflictChecker>.Instance);
        OperationWaitingCompleteNotification? waitingComplete = null;
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && args?[1] is OperationWaitingCompleteNotification complete)
            {
                Volatile.Write(ref waitingComplete, complete);
            }

            return Task.CompletedTask;
        });
        var queue = new OperationQueueService(
            tracker, conflictChecker, notifications,
            NullLogger<OperationQueueService>.Instance);

        using var blockerCts = new CancellationTokenSource();
        var blocker = tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", blockerCts);

        var queued = await queue.EnqueueAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            "Game Detection",
            () => throw startFailure,
            CancellationToken.None);
        Assert.True(queued.Queued);

        tracker.CompleteOperation(blocker, success: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var waiting = tracker.GetOperation(queued.OperationId);
            if (waiting is not null && waiting.Status.IsTerminal() && Volatile.Read(ref waitingComplete) is not null)
            {
                return (waiting, Volatile.Read(ref waitingComplete));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new InvalidOperationException("The promoted operation never reached a terminal state.");
    }

    [Fact]
    public void RefusalReason_SerializesAsSkippedReasonOnBothResponses()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var runNow = JsonSerializer.Serialize(
            new QueuedOperationResponse { Status = "skipped", SkippedReason = DownloadReason },
            options);
        var runAll = JsonSerializer.Serialize(
            new TriggerAllResponse { SkippedCount = 1, SkippedReason = DownloadReason },
            options);

        Assert.Contains("\"skippedReason\"", runNow);
        Assert.Contains("\"status\":\"skipped\"", runNow);
        Assert.Contains("\"skippedReason\"", runAll);
        Assert.Contains("\"skippedCount\":1", runAll);
    }

    [Fact]
    public void SkippedOperation_KeepsTheSuppliedReasonAndFallsBackWithoutOne()
    {
        var tracker = CreateRealTracker();

        using var withReason = new CancellationTokenSource();
        var explained = tracker.RegisterOperation(OperationType.EvictionScan, EvictionKey, withReason);
        tracker.CompleteOperation(explained, success: true, error: DownloadReason, skipped: true);

        using var withoutReason = new CancellationTokenSource();
        var bare = tracker.RegisterOperation(OperationType.EvictionScan, EvictionKey, withoutReason);
        tracker.CompleteOperation(bare, success: true, skipped: true);

        Assert.Equal(OperationStatus.Skipped, tracker.GetOperation(explained)!.Status);
        Assert.Equal(DownloadReason, tracker.GetOperation(explained)!.Message);
        Assert.Equal("Operation skipped - nothing to do", tracker.GetOperation(bare)!.Message);
    }

    private static Func<string, RunTrigger, string?> DeclineOnly(string serviceKey)
        => (key, _) => string.Equals(key, serviceKey, StringComparison.OrdinalIgnoreCase)
            ? DownloadReason
            : null;

    // The hook is a process-wide static, so it is always restored: another test class's service loop
    // must not inherit a gate this class installed.
    private static async Task<T> WithGateAsync<T>(Func<string, RunTrigger, string?> gate, Func<Task<T>> body)
    {
        var previous = ScheduledServiceBase.ScheduleRunGate;
        ScheduledServiceBase.ScheduleRunGate = gate;
        try
        {
            return await body();
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    private static ServiceScheduleRegistry CreateRegistry(
        ScheduledBackgroundService service,
        CacheScanGate gate,
        UnifiedOperationTracker? tracker = null,
        ISignalRNotificationService? notifications = null)
        => CreateRegistry([service], gate, tracker, notifications);

    // A registry with a real gate installs the process-wide hooks, so whatever was there is put
    // back: another test class's service loop must not inherit this one's answer, and must not
    // inherit a startup wait over a tracker this class threw away either.
    private static ServiceScheduleRegistry CreateRegistry(
        IReadOnlyList<ScheduledBackgroundService> services,
        CacheScanGate gate,
        UnifiedOperationTracker? tracker = null,
        ISignalRNotificationService? notifications = null)
    {
        var previousGate = ScheduledServiceBase.ScheduleRunGate;
        var previousWait = ScheduledServiceBase.WaitForDownloadAnswer;
        try
        {
            return new ServiceScheduleRegistry(
                services,
                CacheScanGateHarness.VisibleClientsStateService(),
                notifications ?? CreateDefaultProxy<ISignalRNotificationService>(),
                tracker,
                activityRegistry: null,
                cacheScanGate: gate);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previousGate;
            ScheduledServiceBase.WaitForDownloadAnswer = previousWait;
        }
    }

    private static UnifiedOperationTracker CreateRealTracker()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        return new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
    }

    private static T CreateDefaultProxy<T>() where T : class
        => DispatchProxy.Create<T, DefaultDispatch<T>>();

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, DefaultDispatch<T>>();
        ((DefaultDispatch<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    private class DefaultDispatch<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Handler is not null)
            {
                return Handler(targetMethod!, args);
            }

            var returnType = targetMethod!.ReturnType;
            if (returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                var fromResult = typeof(Task)
                    .GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType);
                return fromResult.Invoke(null, [DefaultValue(resultType)]);
            }

            return DefaultValue(returnType);
        }

        private static object? DefaultValue(Type type)
        {
            if (!type.IsValueType || Nullable.GetUnderlyingType(type) != null)
            {
                return null;
            }

            return Activator.CreateInstance(type);
        }
    }

    private sealed class RunGateProbeService : ScheduledBackgroundService
    {
        private readonly string _serviceKey;

        public RunGateProbeService(string serviceKey)
            : base(NullLogger<RunGateProbeService>.Instance, new ConfigurationBuilder().Build())
        {
            _serviceKey = serviceKey;
        }

        public override string ServiceKey => _serviceKey;
        protected override string ServiceName => _serviceKey;
        protected override TimeSpan Interval => TimeSpan.FromHours(1);
        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        public override bool DefaultRunOnStartup => false;

        public bool WorkRan { get; private set; }
        public bool EndBroadcast { get; private set; }
        public bool HasPendingRun => HasPendingManualRun();

        public bool TakePendingDeferredRun() => ConsumePendingDeferredRun();

        public void SetLastRunUtc(DateTime value) => LastRunUtc = value;

        public Task<(bool ShuttingDown, bool RunFailed)> InvokeRunScheduledWorkAsync(
            RunTrigger trigger,
            CancellationToken stoppingToken)
            => RunScheduledWorkAsync(
                ServiceKey,
                trigger,
                () =>
                {
                    WorkRan = true;
                    return Task.CompletedTask;
                },
                stoppingToken,
                "{ServiceName} probe run failed",
                () => EndBroadcast = true);

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class StartupGateProbeService : ScheduledBackgroundService
    {
        private readonly string _serviceKey;
        private readonly TaskCompletionSource _startupRan =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StartupGateProbeService(string serviceKey)
            : base(NullLogger<StartupGateProbeService>.Instance, new ConfigurationBuilder().Build())
        {
            _serviceKey = serviceKey;
        }

        public override string ServiceKey => _serviceKey;
        protected override string ServiceName => _serviceKey;
        protected override TimeSpan Interval => TimeSpan.FromHours(1);
        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        public override bool DefaultRunOnStartup => true;

        public Task StartupRan => _startupRan.Task;

        protected override Task OnStartupAsync(CancellationToken stoppingToken)
        {
            _startupRan.TrySetResult();
            return Task.CompletedTask;
        }

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
}
