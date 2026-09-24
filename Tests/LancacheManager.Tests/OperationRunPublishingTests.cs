using System.Reflection;
using System.Text.RegularExpressions;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The tracker keeps one list of runs and sends every change to a run as one row, in order, so the
/// browser draws exactly one card per run and removes it when the run ends.
/// </summary>
public sealed class OperationRunPublishingTests
{
    [Fact]
    public void AHeldRunBegunByItsOwnerIsNeverFinishedAsParked()
    {
        var tracker = CreateTracker();
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Scheduled);
        var id = tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting, notice: notice);
        notice.PendingId = id;

        Assert.True(tracker.BeginQueuedOperation(id, new Dictionary<string, object?>(), null, null));
        tracker.CancelOperation(id);

        // Its worker owns the terminal now, so the cancel must leave it unwinding rather than end it.
        Assert.False(tracker.CancelParkedOperation(id));
        Assert.Equal(OperationStatus.Cancelling, tracker.GetOperation(id)!.Status);
        Assert.Contains(tracker.GetActiveOperations(), op => op.Id == id);
    }

    [Fact]
    public void TheRunListHoldsEveryTrackedRunExceptThePrefillContainer()
    {
        var tracker = CreateTracker();
        var running = tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var waiting = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting, blockedByName: "Cache File Scan");
        var finished = tracker.RegisterOperation(OperationType.LogProcessing, "Log Processing", new CancellationTokenSource());
        tracker.CompleteOperation(finished, success: true);
        var container = tracker.RegisterOperation(OperationType.ScheduledPrefill, "Scheduled Prefill", new CancellationTokenSource(),
            new ScheduledPrefillOperationMetadata());

        var snapshot = tracker.GetRuns();

        Assert.Equal(3, snapshot.Runs.Count);
        Assert.DoesNotContain(snapshot.Runs, run => run.OperationId == container);
        Assert.Equal("running", Assert.Single(snapshot.Runs, run => run.OperationId == running).Status);
        var parked = Assert.Single(snapshot.Runs, run => run.OperationId == waiting);
        Assert.Equal("waiting", parked.Status);
        Assert.Equal("Cache File Scan", parked.BlockedByName);
        var ended = Assert.Single(snapshot.Runs, run => run.OperationId == finished);
        Assert.Equal("completed", ended.Status);
        Assert.False(ended.Retained);
        // Four rows were stamped; the container consumed none.
        Assert.Equal(4, snapshot.Revision);
    }

    [Fact]
    public void EveryStateChangeSendsOneRowAndProgressSendsNone()
    {
        var (tracker, recorder) = CreateRecordingTracker();
        var waiting = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting);
        tracker.SetBlockedByName(waiting, "Cache File Scan");
        tracker.SetBlockedByName(waiting, "Cache File Scan");
        tracker.RefreshRun(waiting);
        tracker.BeginQueuedOperation(waiting, new Dictionary<string, object?>(), null, null);
        tracker.UpdateProgress(waiting, 50, "Halfway");
        tracker.UpdateMetadata(waiting, _ => { });
        tracker.RegisterOperation(OperationType.ScheduledPrefill, "Scheduled Prefill", new CancellationTokenSource(),
            new ScheduledPrefillOperationMetadata());
        var restored = Guid.NewGuid();
        Assert.True(tracker.TryRestoreOperation(restored, OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource()));
        tracker.RecordHandoff(waiting, restored);
        tracker.CancelOperation(restored);
        tracker.ForceKillOperation(restored);
        tracker.CompleteOperation(restored, success: false, cancelled: true);
        tracker.CompleteOperation(waiting, success: false, error: "Disk read failed");
        tracker.UpdateKeptEnding(waiting, 2, latestRunSucceeded: false);
        Assert.True(tracker.CloseRun(waiting));

        var rows = SentRows(recorder);

        Assert.Equal(Enumerable.Range(1, 11).Select(revision => (long)revision), rows.Select(row => row.Revision));
        Assert.Equal(
            new[] { "waiting", "waiting", "waiting", "running", "running", "cancelling", "cancelling", "cancelled", "failed", "failed", "failed" },
            rows.Select(row => row.Status));
        Assert.Equal("Cache File Scan", rows[1].BlockedByName);
        Assert.True(rows[^1].Closed);
    }

    [Fact]
    public async Task RowsFromTwoThreadsLeaveInRevisionOrder()
    {
        var (tracker, recorder) = CreateRecordingTracker();
        var id = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting);
        using var start = new Barrier(2);
        var names = Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 200; i++) tracker.SetBlockedByName(id, $"Blocker {i}");
        });
        var refreshes = Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 200; i++) tracker.RefreshRun(id);
        });
        await Task.WhenAll(names, refreshes);
        tracker.CompleteOperation(id, success: true);

        var rows = await WaitForRowsAsync(recorder, 402);

        Assert.Equal(Enumerable.Range(1, 402).Select(revision => (long)revision), rows.Select(row => row.Revision));
        Assert.Equal(tracker.GetOperation(id)!.Status.ToWireString(), rows[^1].Status);
    }

    [Fact]
    public async Task RecordingAHandoffTakesNoOperationLockAndSendsNoRow()
    {
        var (tracker, recorder) = CreateRecordingTracker();
        var waitingId = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting);
        var nextId = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource());
        var waiting = tracker.GetOperation(waitingId)!;
        var next = tracker.GetOperation(nextId)!;
        var sent = SentRows(recorder).Count;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var holder = Task.Factory.StartNew(() =>
        {
            lock (waiting)
            lock (next)
            {
                held.SetResult();
                release.Wait();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await held.Task;

        try
        {
            // A version that locked either operation would block for as long as the holder keeps it.
            await Task.Run(() => tracker.RecordHandoff(waitingId, nextId)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
            await holder;
        }

        Assert.Same(next, tracker.GetOperation(waitingId, followHandoff: true));
        Assert.Equal(sent, SentRows(recorder).Count);
    }

    [Fact]
    public async Task AFailureStaysListedAfterTheReapDelayAndASuccessDoesNot()
    {
        var (tracker, recorder) = CreateRecordingTracker();
        var failed = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        var succeeded = tracker.RegisterOperation(OperationType.LogProcessing, "Log Processing", new CancellationTokenSource());
        tracker.CompleteOperation(failed, success: false, error: "Disk read failed");
        tracker.CompleteOperation(succeeded, success: true);

        var failedRow = SentRows(recorder).Last(row => row.OperationId == failed);
        Assert.True(failedRow.Retained);
        Assert.Equal("Disk read failed", failedRow.Error);
        Assert.False(SentRows(recorder).Last(row => row.OperationId == succeeded).Retained);

        // The tracker reaps an ordinary ending ten seconds after it completes.
        await Task.Delay(TimeSpan.FromSeconds(11));

        var runs = tracker.GetRuns().Runs;
        var kept = Assert.Single(runs, run => run.OperationId == failed);
        Assert.True(kept.Retained);
        Assert.False(kept.Closed);
        Assert.DoesNotContain(runs, run => run.OperationId == succeeded);
    }

    // Only red and amber endings stay until closed; a cancelled card leaves on its own like a
    // success. [124]
    [Fact]
    public void SkippedCardsAreKeptAndCancelledRunsAreNot()
    {
        var tracker = CreateTracker();
        var card = Ended(tracker, OperationType.EvictionScan, new RunNotice(NotificationMode.All, RunTrigger.Scheduled), cancelled: true);
        var background = Ended(tracker, OperationType.EvictionScan, new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled), cancelled: true);
        var skippedCard = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        tracker.CompleteOperation(skippedCard, success: true, skipped: true);

        Assert.False(Run(tracker, card).Retained);
        Assert.False(Run(tracker, background).Retained);
        Assert.True(Run(tracker, skippedCard).Retained);
    }

    [Fact]
    public void PhasesAndTypesWithoutACardAreNotKept()
    {
        var tracker = CreateTracker();
        var parent = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        var phase = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            parentOperationId: parent);
        tracker.CompleteOperation(phase, success: false, error: "Detection failed");
        var statusCheck = Ended(tracker, OperationType.StatusCheck, null, cancelled: false);

        Assert.False(Run(tracker, phase).Retained);
        Assert.False(Run(tracker, statusCheck).Retained);
        Assert.False(UnifiedOperationTracker.KeepsUntilClosed(tracker.GetOperation(phase)!));
    }

    [Fact]
    public void ARestoredPlatformPrefillRunOwnsItsCardUnderItsContainer()
    {
        var tracker = CreateTracker();
        var container = tracker.RegisterOperation(OperationType.ScheduledPrefill, "Scheduled Prefill", new CancellationTokenSource(),
            new ScheduledPrefillOperationMetadata());
        var scheduleId = Guid.NewGuid();
        var platform = Guid.NewGuid();
        Assert.True(tracker.TryRestoreOperation(platform, OperationType.ScheduledPrefill, "Scheduled Prefill - Steam - Nightly",
            new CancellationTokenSource(), new ScheduledPrefillServiceRunState(PrefillPlatform.Steam, scheduleId, "Nightly", new RunNotice(NotificationMode.All, RunTrigger.Manual)),
            parentOperationId: container));
        tracker.CompleteOperation(platform, success: false, error: "Login expired");

        var row = Run(tracker, platform);
        Assert.True(row.Retained);
        Assert.Equal(scheduleId, row.ScheduleId);
        Assert.Equal(PrefillPlatform.Steam, row.ServiceId);
    }

    [Fact]
    public void ASuccessWithAWarningIsKeptAndCarriesTheRawText()
    {
        var tracker = CreateTracker();
        var scan = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            new Dictionary<string, object?>());
        var plain = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            new Dictionary<string, object?>());
        var context = new Dictionary<string, object?> { ["detectionError"] = "signalr.gameDetect.error.fatal" };
        tracker.UpdateProgress(scan, 90, "signalr.evictionScan.detectingGames", operation =>
        {
            if (operation.Metadata is Dictionary<string, object?> values) values["context"] = context;
        });

        Assert.Equal("signalr.gameDetect.error.fatal", Run(tracker, scan).Warning);
        Assert.Null(Run(tracker, plain).Warning);
        Assert.Same(context, UnifiedOperationTracker.ReadContext(tracker.GetOperation(scan)!.Metadata));

        tracker.CompleteOperation(scan, success: true);
        tracker.CompleteOperation(plain, success: true);

        var ended = Run(tracker, scan);
        Assert.Equal("completed", ended.Status);
        Assert.Equal("signalr.gameDetect.error.fatal", ended.Warning);
        Assert.True(ended.Retained);
        Assert.False(Run(tracker, plain).Retained);
    }

    [Fact]
    public void TheSilentEvictionCleanupShowsNothingUnlessItFails()
    {
        var (tracker, recorder) = CreateRecordingTracker();
        var failed = tracker.RegisterOperation(OperationType.EvictionRemoval, "Remove Evicted", new CancellationTokenSource(),
            new EvictionRemovalMetadata(), notice: new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled));
        tracker.UpdateProgress(failed, 40, "Removing");
        tracker.CompleteOperation(failed, success: false, error: "Disk read failed");
        var succeeded = tracker.RegisterOperation(OperationType.EvictionRemoval, "Remove Evicted", new CancellationTokenSource(),
            new EvictionRemovalMetadata(), notice: new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled));
        tracker.CompleteOperation(succeeded, success: true);
        var manual = tracker.RegisterOperation(OperationType.EvictionRemoval, "Remove Evicted", new CancellationTokenSource(),
            new EvictionRemovalMetadata());

        var rows = SentRows(recorder);
        Assert.All(rows.Where(row => row.OperationId != manual), row => Assert.Equal(RunVisibility.Hidden, row.Visibility));
        Assert.True(rows.Last(row => row.OperationId == failed).Retained);
        Assert.False(rows.Last(row => row.OperationId == succeeded).Retained);
        Assert.Equal(RunVisibility.Card, Run(tracker, manual).Visibility);
    }

    [Fact]
    public void LiveLogIngestRowsAreHiddenAndOnlyItsNewestFailureIsKept()
    {
        var (tracker, recorder) = CreateRecordingTracker();
        // A live ingest pass swaps in its terminal numbers as it completes, the way the log
        // processor does; the swap must not change how the ended pass is drawn.
        Guid Pass() => tracker.RegisterOperation(OperationType.LogProcessing, "Log Processing", new CancellationTokenSource(),
            new Dictionary<string, object?>(), notice: new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled), liveIngest: true);
        void End(Guid id, bool success) => tracker.CompleteOperation(id, success, success ? null : "Log processing failed with exit code 1",
            onCompleting: operation => operation.Metadata = new object());

        var succeeded = Pass();
        tracker.UpdateProgress(succeeded, 50, "signalr.logProcessing.progress");
        End(succeeded, success: true);
        var first = Pass();
        End(first, success: false);
        // Hidden log processing that is not live ingest keeps its own failure card.
        var hidden = tracker.RegisterOperation(OperationType.LogProcessing, "Log Processing", new CancellationTokenSource(),
            notice: new RunNotice(NotificationMode.Hidden, RunTrigger.Manual));
        End(hidden, success: false);
        var second = Pass();
        End(second, success: false);
        var interactive = tracker.RegisterOperation(OperationType.LogProcessing, "Log Processing", new CancellationTokenSource());

        var live = SentRows(recorder).Where(row => row.OperationId == succeeded || row.OperationId == first || row.OperationId == second).ToList();
        Assert.All(live, row =>
        {
            Assert.Equal(RunVisibility.Hidden, row.Visibility);
            Assert.True(row.LiveIngest);
        });
        Assert.Contains(live, row => row.OperationId == first && row.Closed);
        Assert.Null(tracker.GetOperation(first));
        Assert.Equal(second, Assert.Single(tracker.GetRuns().Runs, run => run.Retained && run.LiveIngest).OperationId);
        var notLive = Run(tracker, hidden);
        Assert.True(notLive.Retained);
        Assert.False(notLive.LiveIngest);
        Assert.Equal(RunVisibility.Card, Run(tracker, interactive).Visibility);
    }

    // The prefill sign-in has a card of its own, drawn only by the browser that started it. [64]
    [Fact]
    public void APrefillSignInRowNamesItsPlatformAndOwnerAndItsFailureIsKept()
    {
        var tracker = CreateTracker();
        var owner = Guid.NewGuid();
        var id = tracker.RegisterOperation(OperationType.PrefillLogin, "Steam sign-in", new CancellationTokenSource(),
            PrefillPlatform.Steam, notice: new RunNotice(NotificationMode.All, RunTrigger.Manual), ownerSessionId: owner);

        var row = Run(tracker, id);
        Assert.Equal(PrefillPlatform.Steam, row.ServiceId);
        Assert.Equal(owner, row.OwnerSessionId);
        Assert.Equal(RunVisibility.Card, row.Visibility);

        tracker.CompleteOperation(id, success: false, error: "Login expired");
        Assert.True(Run(tracker, id).Retained);
    }

    [Fact]
    public void ClosingAKeptEndingSendsOneClosedRowAndRemovesIt()
    {
        var (tracker, recorder) = CreateRecordingTracker();
        var failed = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        var live = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        tracker.CompleteOperation(failed, success: false, error: "Disk read failed");
        var before = SentRows(recorder).Count;

        Assert.True(tracker.CloseRun(failed));

        var closed = Assert.Single(SentRows(recorder).Skip(before));
        Assert.Equal(failed, closed.OperationId);
        Assert.True(closed.Closed);
        Assert.True(closed.Retained);
        Assert.DoesNotContain(tracker.GetRuns().Runs, run => run.OperationId == failed);
        Assert.Null(tracker.GetOperation(failed));
        Assert.False(tracker.CloseRun(failed));
        Assert.False(tracker.CloseRun(live));
        Assert.Equal(before + 1, SentRows(recorder).Count);
    }

    [Fact]
    public void TheCloseEndpointAnswersNoContentThenNotFound()
    {
        var tracker = CreateTracker();
        var controller = new OperationsController(
            tracker,
            new OperationCancellationService(tracker, new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<OperationCancellationService>.Instance));
        var failed = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        tracker.CompleteOperation(failed, success: false, error: "Disk read failed");

        Assert.IsType<NoContentResult>(controller.CloseRun(failed));
        Assert.IsType<NotFoundObjectResult>(controller.CloseRun(failed));
    }

    [Fact]
    public void KeptEndingsCarryTheirScheduleAndTakeTheFailureStreak()
    {
        var (tracker, recorder) = CreateRecordingTracker();
        var scheduleId = Guid.NewGuid();
        var prefill = tracker.RegisterOperation(OperationType.ScheduledPrefill, "Scheduled Prefill - Epic - Nightly", new CancellationTokenSource(),
            new ScheduledPrefillServiceRunState(PrefillPlatform.Epic, scheduleId, "Nightly", new RunNotice(NotificationMode.All, RunTrigger.Manual)));
        var scan = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        var live = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        Assert.All(SentRows(recorder).Where(row => row.OperationId == prefill), row =>
        {
            Assert.Equal(scheduleId, row.ScheduleId);
            Assert.Equal(PrefillPlatform.Epic, row.ServiceId);
        });
        tracker.CompleteOperation(prefill, success: false, error: "Login expired");
        tracker.CompleteOperation(scan, success: false, error: "Disk read failed");

        var first = SentRows(recorder).Last(row => row.OperationId == prefill);
        Assert.Equal(0, first.ConsecutiveFailures);
        var kept = Run(tracker, prefill);
        Assert.True(kept.Retained);
        Assert.False(kept.Closed);
        Assert.Equal("scheduledPrefill", kept.OperationType);
        Assert.Equal(scheduleId, kept.ScheduleId);
        Assert.Null(Run(tracker, scan).ScheduleId);
        Assert.Null(Run(tracker, scan).ServiceId);

        var before = SentRows(recorder).Count;
        tracker.UpdateKeptEnding(prefill, 3, latestRunSucceeded: true);
        var updated = Assert.Single(SentRows(recorder).Skip(before));
        Assert.Equal(3, updated.ConsecutiveFailures);
        Assert.True(updated.LatestRunSucceeded);

        tracker.UpdateKeptEnding(live, 3, latestRunSucceeded: true);
        tracker.CloseRun(scan);
        var afterClose = SentRows(recorder).Count;
        tracker.UpdateKeptEnding(scan, 3, latestRunSucceeded: true);
        Assert.Equal(afterClose, SentRows(recorder).Count);
        Assert.Equal(before + 2, afterClose);
    }

    [Fact]
    public void TheFirstTerminalRowStampsTheCompletionOrderAndLaterRowsKeepIt()
    {
        var (tracker, recorder) = CreateRecordingTracker();
        var id = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        tracker.UpdateKeptEnding(id, 1, latestRunSucceeded: false);
        tracker.CompleteOperation(id, success: false, error: "Disk read failed");
        tracker.UpdateKeptEnding(id, 2, latestRunSucceeded: false);
        tracker.CloseRun(id);

        var rows = SentRows(recorder);
        Assert.Null(rows[0].CompletedRevision);
        var terminal = rows[1];
        Assert.Equal("failed", terminal.Status);
        Assert.Equal(terminal.Revision, terminal.CompletedRevision);
        Assert.All(rows.Skip(2), row => Assert.Equal(terminal.Revision, row.CompletedRevision));
        Assert.Equal(4, rows.Count);
    }

    [Fact]
    public async Task ASnapshotNeverListsARunWithoutARevision()
    {
        var tracker = CreateTracker();
        using var done = new CancellationTokenSource();
        var registrations = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                tracker.RegisterOperation(OperationType.LogProcessing, "Log Processing", new CancellationTokenSource());
            }
            done.Cancel();
        });

        while (!done.IsCancellationRequested)
        {
            Assert.All(tracker.GetRuns().Runs, run => Assert.True(run.Revision >= 1));
        }
        await registrations;
        Assert.All(tracker.GetRuns().Runs, run => Assert.True(run.Revision >= 1));
    }

    [Theory]
    [InlineData(NotificationMode.Manual)]
    [InlineData(NotificationMode.Silent)]
    public void AWaitingCardHandedToABackgroundRunRaisesItBeforeTheWaitingRowEnds(NotificationMode successorMode)
    {
        var (tracker, recorder) = CreateRecordingTracker();
        var waitingNotice = new RunNotice(successorMode == NotificationMode.Manual ? NotificationMode.Manual : NotificationMode.All, RunTrigger.Manual);
        var successorNotice = new RunNotice(successorMode, RunTrigger.Scheduled);
        var waiting = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting, notice: waitingNotice);
        var successor = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(), notice: successorNotice);
        Assert.Equal(RunVisibility.Background, Run(tracker, successor).Visibility);
        var before = SentRows(recorder).Count;

        tracker.RecordHandoff(waiting, successor);
        tracker.CompleteOperation(waiting, success: true);

        var sent = SentRows(recorder).Skip(before).ToList();
        Assert.Equal(2, sent.Count);
        Assert.Equal(successor, sent[0].OperationId);
        Assert.Equal(RunVisibility.Card, sent[0].Visibility);
        Assert.Equal(waiting, sent[1].OperationId);
        Assert.Equal(successor, sent[1].NextOperationId);
        Assert.Equal(RunVisibility.Card, Run(tracker, successor).Visibility);
        Assert.Equal(RunTrigger.Scheduled, successorNotice.Trigger);

        tracker.CompleteOperation(successor, success: true, skipped: true);
        Assert.True(Run(tracker, successor).Retained);
    }

    [Fact]
    public void AnEndedSuccessorIsNotRaisedByALaterHandoff()
    {
        var (tracker, recorder) = CreateRecordingTracker();
        var waiting = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting, notice: new RunNotice(NotificationMode.All, RunTrigger.Manual));
        var successor = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            notice: new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled));
        tracker.CompleteOperation(successor, success: false, cancelled: true);
        var before = SentRows(recorder).Count;

        tracker.RecordHandoff(waiting, successor);
        tracker.CompleteOperation(waiting, success: true);

        var sent = SentRows(recorder).Skip(before).ToList();
        Assert.Equal(waiting, Assert.Single(sent).OperationId);
        var ended = Run(tracker, successor);
        Assert.Equal(RunVisibility.Background, ended.Visibility);
        Assert.False(ended.Retained);
    }

    [Fact]
    public void AnEndedRunKeepsWhatItLookedLikeWhenItEnded()
    {
        var tracker = CreateTracker();
        var notice = new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled);
        var waiting = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting, notice: notice);
        tracker.CompleteOperation(waiting, success: false, cancelled: true);

        // The duplicate-request path raises a waiting notice's trigger without the operation lock.
        notice.Trigger = RunTrigger.Manual;

        var row = Run(tracker, waiting);
        Assert.Equal(RunVisibility.Background, row.Visibility);
        Assert.False(row.Retained);
        Assert.False(UnifiedOperationTracker.KeepsUntilClosed(tracker.GetOperation(waiting)!));

        var handedOn = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting);
        var next = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        tracker.RecordHandoff(handedOn, next);
        tracker.CompleteOperation(handedOn, success: true);
        tracker.CompleteOperation(next, success: false, error: "Disk read failed");
        Assert.True(tracker.CloseRun(next));

        // Reaping the successor removed the handoff link; the waiting row still names it.
        Assert.Same(tracker.GetOperation(handedOn), tracker.GetOperation(handedOn, followHandoff: true));
        Assert.Equal(next, Run(tracker, handedOn).NextOperationId);
    }

    [Fact]
    public async Task APromotedRunNamesTheWaitingRunOnItsFirstRow()
    {
        var (tracker, recorder) = CreateRecordingTracker();
        var queue = CreateQueue(tracker);
        var blocker = tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var started = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = await queue.EnqueueAsync(OperationType.GameDetection, ConflictScope.Bulk(), "Game Detection", async () =>
        {
            await Task.Yield();
            var id = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource());
            started.TrySetResult(id);
            return id;
        }, CancellationToken.None);
        Assert.True(queued.Queued);

        tracker.CompleteOperation(blocker, success: true);
        var promoted = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(queued.OperationId, SentRows(recorder).First(row => row.OperationId == promoted).PreviousOperationId);
    }

    [Fact]
    public void AHeldScheduleRunNamesTheWaitingRunOnItsFirstRow()
    {
        var (tracker, recorder) = CreateRecordingTracker();
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Scheduled);
        var waiting = tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting, notice: notice);
        notice.Attach(tracker, waiting);

        var run = tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource(),
            notice: notice);

        Assert.Equal(waiting, SentRows(recorder).First(row => row.OperationId == run).PreviousOperationId);
    }

    [Fact]
    public async Task PromotionOntoARunningDuplicateNamesItOnTheWaitingRowsEnding()
    {
        var (tracker, recorder) = CreateRecordingTracker();
        var queue = CreateQueue(tracker);
        var gate = Assert.IsType<SemaphoreSlim>(typeof(OperationQueueService)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue));
        var blocker = tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var queued = await queue.EnqueueAsync(OperationType.EvictionScan, ConflictScope.Bulk(), "Eviction Scan",
            () => Task.FromResult<Guid?>(Guid.NewGuid()), CancellationToken.None);
        Guid duplicate;
        int duplicateRow;
        await gate.WaitAsync();
        try
        {
            tracker.CompleteOperation(blocker, success: true);
            duplicate = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
            duplicateRow = SentRows(recorder).Count;
        }
        finally { gate.Release(); }

        var ending = await WaitForRowAsync(recorder, row => row.OperationId == queued.OperationId && row.Status == "completed");

        Assert.Equal(duplicate, ending.NextOperationId);
        var between = SentRows(recorder).Skip(duplicateRow).TakeWhile(row => row.Revision < ending.Revision);
        Assert.All(between, row => Assert.Equal(duplicate, row.OperationId));
    }

    [Fact]
    public void AttachingAHeldRunToRunningWorkNamesItOnTheWaitingRowsEnding()
    {
        var (tracker, recorder) = CreateRecordingTracker();
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Scheduled);
        var waiting = tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting, notice: notice);
        notice.Attach(tracker, waiting);
        var running =tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var before = SentRows(recorder).Count;

        notice.Attach(tracker, running);

        var ending = Assert.Single(SentRows(recorder).Skip(before));
        Assert.Equal(waiting, ending.OperationId);
        Assert.Equal("completed", ending.Status);
        Assert.Equal(running, ending.NextOperationId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACancelDuringPromotionLeavesTheEndingToThePromotion(bool startsDuplicate)
    {
        var (tracker, recorder) = CreateRecordingTracker();
        var queue = CreateQueue(tracker);
        var blocker = tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = await queue.EnqueueAsync(OperationType.GameDetection, ConflictScope.Bulk(), "Game Detection", async () =>
        {
            entered.TrySetResult();
            return await release.Task;
        }, CancellationToken.None);
        tracker.CompleteOperation(blocker, success: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(queued.OperationId));
        // The cancel callback runs on the thread pool; before the fix it ended the waiting run here.
        await Task.Delay(300);
        Assert.False(tracker.GetOperation(queued.OperationId)!.Status.IsTerminal());

        var duplicate = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource());
        release.SetResult(startsDuplicate ? duplicate : null);
        await queue.EnqueueAsync(OperationType.LogProcessing, ConflictScope.Bulk(), "Promotion barrier",
            () => Task.FromResult<Guid?>(Guid.NewGuid()), CancellationToken.None);

        var ending = SentRows(recorder).Last(row => row.OperationId == queued.OperationId);
        if (startsDuplicate)
        {
            Assert.Equal("completed", ending.Status);
            Assert.Equal(duplicate, ending.NextOperationId);
            Assert.Equal(OperationStatus.Cancelling, tracker.GetOperation(duplicate)!.Status);
        }
        else
        {
            Assert.Equal("cancelled", ending.Status);
            Assert.Null(ending.NextOperationId);
            Assert.Equal(OperationStatus.Running, tracker.GetOperation(duplicate)!.Status);
        }
    }

    // A row and its revision describe one state only when the row is stamped under its operation's
    // lock, so every send in the tracker sits inside a lock on the operation it sends. [91]
    [Fact]
    public void EveryPublishInTheTrackerRunsUnderALockOnTheSameOperation()
    {
        var path = Path.Combine(EndpointAuthorizationHost.FindRepositoryRoot(),
            "Api", "LancacheManager", "Core", "Services", "Operations", "UnifiedOperationTracker.cs");

        Assert.Empty(PublishesOutsideTheirLock(File.ReadAllText(path)));
    }

    // Walks outward from each call through every enclosing brace block; the nearest one may be an
    // if inside the lock.
    private static List<string> PublishesOutsideTheirLock(string source)
    {
        var unlocked = new List<string>();
        foreach (Match call in Regex.Matches(source, @"(?<![\w.])Publish\((\w+)\);"))
        {
            var target = call.Groups[1].Value;
            var locked = false;
            var depth = 0;
            for (var i = call.Index - 1; i >= 0 && !locked; i--)
            {
                if (source[i] == '}')
                {
                    depth++;
                }
                else if (source[i] == '{' && depth-- == 0)
                {
                    depth = 0;
                    var header = Regex.Match(source[..i], @"lock\s*\(\s*(\w+)\s*\)\s*$", RegexOptions.RightToLeft);
                    locked = header.Success && header.Groups[1].Value == target;
                }
            }

            if (!locked) unlocked.Add($"line {source[..call.Index].Count(c => c == '\n') + 1}: {call.Value}");
        }

        return unlocked;
    }

    private static Guid Ended(UnifiedOperationTracker tracker, OperationType type, RunNotice? notice, bool cancelled)
    {
        var id = tracker.RegisterOperation(type, type.ToString(), new CancellationTokenSource(), notice: notice);
        tracker.CompleteOperation(id, success: false, error: cancelled ? null : "Failed", cancelled: cancelled);
        return id;
    }

    private static OperationRun Run(UnifiedOperationTracker tracker, Guid id) =>
        Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == id);

    private static List<OperationRun> SentRows(RecordingNotificationProxy recorder)
    {
        while (true)
        {
            try
            {
                return recorder.Invocations.ToArray()
                    .Where(call => call.Method == nameof(ISignalRNotificationService.NotifyAdminAsync)
                        && (string)call.Args[0]! == SignalREvents.OperationUpdated)
                    .Select(call => (OperationRun)call.Args[1]!)
                    .ToList();
            }
            catch (ArgumentException)
            {
                // A drain on another thread appended while the unsynchronized list was copied.
            }
        }
    }

    // Bounded waits used as failure detectors: rows sent from another thread's drain arrive
    // shortly after the call that queued them returns.
    private static async Task<List<OperationRun>> WaitForRowsAsync(RecordingNotificationProxy recorder, int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (recorder.Invocations.Count < count && DateTime.UtcNow < deadline) await Task.Delay(10);
        return SentRows(recorder);
    }

    private static async Task<OperationRun> WaitForRowAsync(RecordingNotificationProxy recorder, Func<OperationRun, bool> match)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!SentRows(recorder).Any(match) && DateTime.UtcNow < deadline) await Task.Delay(10);
        return SentRows(recorder).First(match);
    }

    private static OperationQueueService CreateQueue(UnifiedOperationTracker tracker) => new(
        tracker,
        new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance),
        NullLogger<OperationQueueService>.Instance);

    private static UnifiedOperationTracker CreateTracker() =>
        new(new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);

    private static (UnifiedOperationTracker Tracker, RecordingNotificationProxy Recorder) CreateRecordingTracker()
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance, notifications);
        return (tracker, (RecordingNotificationProxy)(object)notifications);
    }
}
