using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// One kept failure card per schedule: the newest kept failure of a schedule replaces the older ones,
/// carries the schedule's failure streak, and notes a later success without being closed by it. A
/// kept cancel, skip or warning is its own card, replaced only by a newer one of those, and never
/// closes it. Runs of no schedule keep one
/// card each. The registry's terminal handler is detached from the tracker and
/// called by each test, so every case names the order the handlers run in and none waits on timing.
/// </summary>
// The re-hold case holds a run, which the process-wide downloads-ended event would start mid-test.
[Collection(nameof(DownloadsEndedEventCollection))]
public sealed class KeptEndingPerScheduleTests
{
    private static readonly MethodInfo TerminalHandler = typeof(ServiceScheduleRegistry)
        .GetMethod("OnTrackedOperationTerminal", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void ThreeFailuresOfOneScheduleLeaveOneCardCountingThree()
    {
        var (tracker, recorder, handle) = Create();
        var failures = new[] { Fail(tracker), Fail(tracker), Fail(tracker) };
        foreach (var failure in failures) handle(failure);

        var kept = Assert.Single(Kept(tracker));
        Assert.Equal(failures[2].Id, kept.OperationId);
        Assert.Equal(3, kept.ConsecutiveFailures);
        Assert.Equal(2, SentRows(recorder).Count(row => row.Closed));
    }

    // The card still says how often the schedule failed; the success only notes itself on it.
    [Fact]
    public void ASuccessAfterThreeFailuresKeepsTheCountAndNotesTheSuccess()
    {
        var (tracker, _, handle) = Create();
        var failures = new[] { Fail(tracker), Fail(tracker), Fail(tracker) };
        foreach (var failure in failures) handle(failure);
        handle(Succeed(tracker));

        var kept = Assert.Single(Kept(tracker));
        Assert.Equal(failures[2].Id, kept.OperationId);
        Assert.Equal(3, kept.ConsecutiveFailures);
        Assert.True(kept.LatestRunSucceeded);
    }

    [Fact]
    public void ASuccessNotesItselfOnTheKeptFailureWithoutClosingItAndTheNextFailureStartsANewStreak()
    {
        var (tracker, _, handle) = Create();
        var first = Fail(tracker);
        handle(first);
        handle(Succeed(tracker));

        var noted = Assert.Single(Kept(tracker));
        Assert.Equal(first.Id, noted.OperationId);
        Assert.Equal(1, noted.ConsecutiveFailures);
        Assert.True(noted.LatestRunSucceeded);

        var second = Fail(tracker);
        handle(second);

        var kept = Assert.Single(Kept(tracker));
        Assert.Equal(second.Id, kept.OperationId);
        Assert.Equal(1, kept.ConsecutiveFailures);
        Assert.False(kept.LatestRunSucceeded);
    }

    [Theory]
    [InlineData("skipped")]
    [InlineData("warning")]
    public void ANewerKeptEndingThatIsNotAFailureLeavesTheKeptFailure(string ending)
    {
        var (tracker, _, handle) = Create();
        var failure = Fail(tracker);
        handle(failure);
        OperationInfo End() => ending switch
        {
            "skipped" => Skip(tracker, visible: true),
            _ => SucceedWithWarning(tracker)
        };
        var later = End();
        handle(later);

        var kept = Kept(tracker);
        Assert.Equal(new[] { failure.Id, later.Id }.Order(), kept.Select(run => run.OperationId).Order());
        var failed = Assert.Single(kept, run => run.OperationId == failure.Id);
        Assert.Equal(1, failed.ConsecutiveFailures);
        Assert.Equal(ending == "warning", failed.LatestRunSucceeded);

        // A newer ending of the same kind replaces the older one, so the schedule never collects a
        // card per run; the failure card still stays.
        var newer = End();
        handle(newer);
        Assert.Equal(new[] { failure.Id, newer.Id }.Order(), Kept(tracker).Select(run => run.OperationId).Order());
    }

    [Fact]
    public void FailuresOfTwoPrefillSchedulesKeepACardEach()
    {
        var (tracker, _, handle) = Create();
        var nightly = Fail(tracker, OperationType.ScheduledPrefill, PrefillState(Guid.NewGuid()));
        var weekly = Fail(tracker, OperationType.ScheduledPrefill, PrefillState(Guid.NewGuid()));
        handle(nightly);
        handle(weekly);

        Assert.Equal(
            new[] { nightly.Id, weekly.Id }.Order(),
            Kept(tracker, OperationType.ScheduledPrefill).Select(run => run.OperationId).Order());
    }

    [Fact]
    public void RunsOfNoScheduleKeepACardEach()
    {
        var (tracker, _, handle) = Create();
        var first = Fail(tracker, OperationType.EvictionRemoval);
        var second = Fail(tracker, OperationType.EvictionRemoval);
        handle(first);
        handle(second);

        var kept = Kept(tracker, OperationType.EvictionRemoval);
        Assert.Equal(2, kept.Count);
        Assert.All(kept, run => Assert.Equal(0, run.ConsecutiveFailures));
    }

    [Fact]
    public void HandlersRunningNewestFirstStillLeaveTheNewestCardCountingEveryFailure()
    {
        var (tracker, _, handle) = Create();
        var a = Fail(tracker);
        var b = Fail(tracker);
        var c = Fail(tracker);

        // C's pass closes and reaps A and B, so their own handlers run on endings the tracker has
        // already forgotten, and must still count them.
        handle(c);
        Assert.Null(tracker.GetOperation(a.Id));
        Assert.Null(tracker.GetOperation(b.Id));
        handle(a);
        handle(b);

        var kept = Assert.Single(Kept(tracker));
        Assert.Equal(c.Id, kept.OperationId);
        Assert.Equal(3, kept.ConsecutiveFailures);
    }

    [Fact]
    public void ASuccessHandledAfterALaterFailureStillEndsTheStreakBeforeIt()
    {
        var (tracker, _, handle) = Create();
        var first = Fail(tracker);
        var success = Succeed(tracker);
        var second = Fail(tracker);

        handle(first);
        handle(second);
        handle(success);

        var kept = Assert.Single(Kept(tracker));
        Assert.Equal(second.Id, kept.OperationId);
        Assert.Equal(1, kept.ConsecutiveFailures);
        Assert.False(kept.LatestRunSucceeded);
    }

    [Fact]
    public void ASkipThatKeptNoCardStillEndsTheStreak()
    {
        var (tracker, _, handle) = Create();
        handle(Fail(tracker));
        handle(Skip(tracker, visible: false));
        var last = Fail(tracker);
        handle(last);

        var kept = Assert.Single(Kept(tracker));
        Assert.Equal(last.Id, kept.OperationId);
        Assert.Equal(1, kept.ConsecutiveFailures);
    }

    [Fact]
    public void AWaitingRecordThatHandedItsWorkOnIsNotAnOutcome()
    {
        var (tracker, _, handle) = Create();
        handle(Fail(tracker));
        var (waiting, successor) = Promote(tracker, OperationType.EvictionScan);
        handle(waiting);
        tracker.CompleteOperation(successor, success: false, error: "Disk read failed");
        handle(tracker.GetOperation(successor)!);

        var kept = Assert.Single(Kept(tracker));
        Assert.Equal(successor, kept.OperationId);
        Assert.Equal(2, kept.ConsecutiveFailures);
        Assert.False(kept.LatestRunSucceeded);
    }

    [Fact]
    public void AScansDetectionPhaseIsNotAnOutcomeOfTheDetectionSchedule()
    {
        var (tracker, _, handle) = Create();
        var scheduled = Fail(tracker, OperationType.GameDetection);
        handle(scheduled);

        var scanId = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        var phaseId = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            new GameDetectionMetrics { ParentOperationId = scanId },
            parentOperationId: scanId, notice: new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled));
        tracker.CompleteOperation(phaseId, success: false, error: "Cache index could not be read");
        handle(tracker.GetOperation(phaseId)!);

        var kept = Assert.Single(Kept(tracker, OperationType.GameDetection));
        Assert.Equal(scheduled.Id, kept.OperationId);
        Assert.Equal(1, kept.ConsecutiveFailures);

        // Nor is it a break: the next scheduled failure continues the same streak.
        var next = Fail(tracker, OperationType.GameDetection);
        handle(next);
        Assert.Equal(2, Assert.Single(Kept(tracker, OperationType.GameDetection)).ConsecutiveFailures);
    }

    [Fact]
    public void ClosingTheNewestFailureBeforeItsHandlerRunsLeavesNothingKept()
    {
        var (tracker, _, handle) = Create();
        var a = Fail(tracker);
        var b = Fail(tracker);
        handle(a);

        Assert.True(tracker.CloseRun(b.Id));
        Assert.Null(tracker.GetOperation(b.Id));
        handle(b);

        Assert.Empty(Kept(tracker));
        Assert.Null(tracker.GetOperation(a.Id));
    }

    [Fact]
    public void RestoredPlatformPrefillFailuresAreKeptAndGroupedByTheirSchedule()
    {
        var (tracker, _, handle) = Create();
        var containerId = tracker.RegisterOperation(OperationType.ScheduledPrefill, "Scheduled Prefill", new CancellationTokenSource(),
            new ScheduledPrefillOperationMetadata());
        var scheduleId = Guid.NewGuid();
        var first = Restore(tracker, containerId, scheduleId);
        var second = Restore(tracker, containerId, scheduleId);
        var other = Restore(tracker, containerId, Guid.NewGuid());
        handle(first);
        handle(second);
        handle(other);

        var kept = Kept(tracker, OperationType.ScheduledPrefill);
        Assert.Equal(2, kept.Count);
        var grouped = Assert.Single(kept, run => run.ScheduleId == scheduleId);
        Assert.Equal(second.Id, grouped.OperationId);
        Assert.Equal(2, grouped.ConsecutiveFailures);
        Assert.Equal(1, Assert.Single(kept, run => run.OperationId == other.Id).ConsecutiveFailures);
    }

    [Fact]
    public async Task ASkipHeldAgainForADownloadIsContinuedByTheHoldAndClosed()
    {
        var (tracker, recorder, handle) = Create(CacheScanGateHarness.Downloading());
        var older = Fail(tracker);
        handle(older);
        var skip = Skip(tracker, visible: true);
        handle(skip);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (tracker.GetOperation(skip.Id) is not null || !tracker.GetWaitingOperations().Any())
        {
            Assert.True(DateTime.UtcNow < deadline, "The skipped run was not held again and closed");
            await Task.Delay(10);
        }

        var hold = Assert.Single(tracker.GetWaitingOperations());
        Assert.Equal(skip.Id, hold.PreviousOperationId);
        Assert.Equal(skip.Id, SentRows(recorder).First(row => row.OperationId == hold.Id).PreviousOperationId);
        Assert.Contains(SentRows(recorder), row => row.OperationId == skip.Id && row.Closed);
        // A skip never closes the schedule's kept failure.
        Assert.Equal(older.Id, Assert.Single(Kept(tracker)).OperationId);
    }

    [Fact]
    public void AHandedOnWaitingRecordStaysNoOutcomeAfterItsSuccessorIsClosedAndReaped()
    {
        var (tracker, _, handle) = Create();
        handle(Fail(tracker));
        var (waiting, successor) = Promote(tracker, OperationType.CacheSizeScan);
        tracker.CompleteOperation(successor, success: false, error: "Disk read failed");
        handle(tracker.GetOperation(successor)!);

        // Reaping the successor removes the handoff link the tracker would otherwise answer from.
        Assert.True(tracker.CloseRun(successor));
        Assert.Null(tracker.GetOperation(successor));
        handle(waiting);
        var last = Fail(tracker);
        handle(last);

        var kept = Assert.Single(Kept(tracker));
        Assert.Equal(last.Id, kept.OperationId);
        Assert.Equal(2, kept.ConsecutiveFailures);
        Assert.False(kept.LatestRunSucceeded);
    }

    // Every order the terminal handlers can run in leaves the newest failure counting the failures
    // in a row that end at it, and noting a later success; one order runs the success's handler
    // before the failures it follows. [68]
    [Theory]
    [InlineData("FFS", 2, true)]
    [InlineData("FSFF", 2, false)]
    [InlineData("SFFF", 3, false)]
    public void TheStreakIsTheSameWhateverOrderTheHandlersRunIn(string sequence, int streak, bool latestRunSucceeded)
    {
        foreach (var order in Orders(sequence.Length))
        {
            var (tracker, _, handle) = Create();
            var endings = sequence.Select(ending => ending == 'F' ? Fail(tracker) : Succeed(tracker)).ToArray();
            foreach (var index in order) handle(endings[index]);

            var kept = Assert.Single(Kept(tracker));
            Assert.Equal(endings[sequence.LastIndexOf('F')].Id, kept.OperationId);
            Assert.Equal((streak, latestRunSucceeded), (kept.ConsecutiveFailures, kept.LatestRunSucceeded));
        }
    }

    // Closing the card does not reset the streak: the next failure counts on. [68]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingTheKeptFailureDoesNotResetTheStreak(bool newestFirst)
    {
        var (tracker, _, handle) = Create();
        var first = Fail(tracker);
        var second = Fail(tracker);
        handle(newestFirst ? second : first);
        handle(newestFirst ? first : second);
        Assert.True(tracker.CloseRun(Assert.Single(Kept(tracker)).OperationId));

        var third = Fail(tracker);
        handle(third);

        var kept = Assert.Single(Kept(tracker));
        Assert.Equal(third.Id, kept.OperationId);
        Assert.Equal(3, kept.ConsecutiveFailures);
    }

    // A kept ending replaces only the older kept ending of its own kind. [72]
    [Theory]
    [InlineData("skipped,warning")]
    [InlineData("warning,skipped")]
    [InlineData("failed,skipped,failed")]
    public void AKeptEndingNeverClosesOneOfAnotherKind(string sequence)
    {
        var (tracker, _, handle) = Create();
        var kept = new List<OperationInfo>();
        foreach (var kind in sequence.Split(','))
        {
            var ending = kind switch
            {
                "failed" => Fail(tracker),
                "skipped" => Skip(tracker, visible: true),
                _ => SucceedWithWarning(tracker)
            };
            handle(ending);
            kept.RemoveAll(older => older.Status == ending.Status);
            kept.Add(ending);

            Assert.Equal(kept.Select(run => run.Id).Order(), Kept(tracker).Select(run => run.OperationId).Order());
        }
    }

    // A mapping sign-in run belongs to no schedule: its failure and the schedule's failure both stay
    // kept, and the schedule's count holds only its own failures, in either arrival order. [70]
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMappingSignInFailureAndItsSchedulesFailureStayKeptApart(bool signInFirst)
    {
        var (tracker, _, handle) = Create();
        OperationInfo SignIn() => Fail(tracker, OperationType.XboxMapping, new Dictionary<string, object?>
        {
            ["integrationLogin"] = new IntegrationLogin(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(),
                DateTime.UtcNow.AddMinutes(5), Shared: false, Recover: false)
        });
        var first = signInFirst ? SignIn() : Fail(tracker, OperationType.XboxMapping);
        handle(first);
        var second = signInFirst ? Fail(tracker, OperationType.XboxMapping) : SignIn();
        handle(second);

        var kept = Kept(tracker, OperationType.XboxMapping);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), kept.Select(run => run.OperationId).Order());
        var scheduleFailure = signInFirst ? second : first;
        Assert.Equal(1, Assert.Single(kept, run => run.OperationId == scheduleFailure.Id).ConsecutiveFailures);
        Assert.Equal(0, Assert.Single(kept, run => run.IntegrationLogin).ConsecutiveFailures);
    }

    private static IEnumerable<int[]> Orders(int count)
    {
        if (count == 0)
        {
            yield return [];
            yield break;
        }

        foreach (var rest in Orders(count - 1))
        {
            for (var position = 0; position <= rest.Length; position++)
            {
                yield return [.. rest[..position], count - 1, .. rest[position..]];
            }
        }
    }

    /// <summary>
    /// A registry over a tracker that records every row it sends, with the registry's terminal handler
    /// detached so each test calls it in the order it names.
    /// </summary>
    private static (UnifiedOperationTracker Tracker, RecordingNotificationProxy Recorder, Action<OperationInfo> Handle) Create(
        CacheScanGate? gate = null)
    {
        var recorder = (RecordingNotificationProxy)(object)DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance, (ISignalRNotificationService)(object)recorder);

        // A registry built with a gate installs the process-wide schedule hooks, so they are put back.
        var previousGate = ScheduledServiceBase.ScheduleRunGate;
        var previousWait = ScheduledServiceBase.WaitForDownloadAnswer;
        ServiceScheduleRegistry schedules;
        try
        {
            schedules = new ServiceScheduleRegistry([], CacheScanGateHarness.VisibleClientsStateService(),
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(), tracker,
                activityRegistry: null, cacheScanGate: gate);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previousGate;
            ScheduledServiceBase.WaitForDownloadAnswer = previousWait;
        }

        var handle = (Action<OperationInfo>)Delegate.CreateDelegate(typeof(Action<OperationInfo>), schedules, TerminalHandler);
        tracker.OperationTerminal -= handle;
        return (tracker, recorder, handle);
    }

    private static OperationInfo Fail(UnifiedOperationTracker tracker, OperationType type = OperationType.EvictionScan, object? state = null)
    {
        var id = tracker.RegisterOperation(type, type.ToWireString(), new CancellationTokenSource(), state);
        tracker.CompleteOperation(id, success: false, error: "Disk read failed");
        return tracker.GetOperation(id)!;
    }

    private static OperationInfo Succeed(UnifiedOperationTracker tracker)
    {
        var id = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        tracker.CompleteOperation(id, success: true);
        return tracker.GetOperation(id)!;
    }

    // A visible skip shows a card and is kept; one that ran as a background row is not.
    private static OperationInfo Skip(UnifiedOperationTracker tracker, bool visible)
    {
        var id = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            notice: new RunNotice(visible ? NotificationMode.All : NotificationMode.Silent, RunTrigger.Scheduled));
        tracker.CompleteOperation(id, success: true, skipped: true);
        return tracker.GetOperation(id)!;
    }

    // The eviction scan whose detection phase failed: a success kept for its warning.
    private static OperationInfo SucceedWithWarning(UnifiedOperationTracker tracker)
    {
        var id = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            new Dictionary<string, object?>
            {
                ["context"] = new Dictionary<string, object?> { ["detectionError"] = "signalr.gameDetect.error.fatal" }
            });
        tracker.CompleteOperation(id, success: true);
        return tracker.GetOperation(id)!;
    }

    // A waiting eviction scan whose work is handed to a running operation, then completed as a
    // success the way the queue completes a promoted waiter.
    private static (OperationInfo Waiting, Guid Successor) Promote(UnifiedOperationTracker tracker, OperationType successorType)
    {
        var waitingId = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting);
        var successor = tracker.RegisterOperation(successorType, successorType.ToWireString(), new CancellationTokenSource());
        tracker.RecordHandoff(waitingId, successor);
        tracker.CompleteOperation(waitingId, success: true);
        var waiting = tracker.GetOperation(waitingId)!;
        Assert.Equal(successor, waiting.NextOperationId);
        return (waiting, successor);
    }

    private static OperationInfo Restore(UnifiedOperationTracker tracker, Guid containerId, Guid scheduleId)
    {
        var id = Guid.NewGuid();
        Assert.True(tracker.TryRestoreOperation(id, OperationType.ScheduledPrefill, "Scheduled Prefill - Steam",
            new CancellationTokenSource(), PrefillState(scheduleId), parentOperationId: containerId));
        tracker.CompleteOperation(id, success: false, error: "Login expired");
        return tracker.GetOperation(id)!;
    }

    private static ScheduledPrefillServiceRunState PrefillState(Guid scheduleId)
        => new(PrefillPlatform.Steam, scheduleId, "Nightly", new RunNotice(NotificationMode.All, RunTrigger.Manual));

    private static List<OperationRun> Kept(UnifiedOperationTracker tracker, OperationType type = OperationType.EvictionScan)
        => tracker.GetRuns().Runs
            .Where(run => run.Retained && !run.Closed && run.OperationType == type.ToWireString())
            .ToList();

    // Read only once the rows under test have been sent, so nothing is appending to the list.
    private static List<OperationRun> SentRows(RecordingNotificationProxy recorder)
        => recorder.Invocations
            .Where(call => call.Method == nameof(ISignalRNotificationService.NotifyAdminAsync)
                && (string?)call.Args[0] == SignalREvents.OperationUpdated)
            .Select(call => (OperationRun)call.Args[1]!)
            .ToList();
}
