using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Services.Scheduling;
using LancacheManager.Middleware;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

public class ServiceScheduleRegistry : IServiceScheduleRegistry
{
    private static readonly HashSet<string> _allowedServiceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "cacheReconciliation",
        "cacheSizeScan",
        "gameDetection",
        "gameImageFetch",
        "cacheSnapshot",
        "operationHistoryCleanup",
        "logRotation",
        "dashboardCacheWarmer"
    };

    // The operations that walk the cache directory tree, so they are the ones a client download can
    // pull the ground out from under. Every schedule asks the same question through the same code;
    // this is what makes the ANSWER differ, and it is reached through the key-to-OperationType map
    // (ScheduleOperationTypes) rather than by naming schedule keys.
    private static readonly HashSet<OperationType> _cacheReadingOperations =
    [
        OperationType.EvictionScan,
        OperationType.CacheSizeScan,
        OperationType.GameDetection,
    ];

    // The fallback for a run the queue refused before it started, which can be a download or another
    // heavy operation. It names neither, because the card shows this only when the refusal's own
    // message is missing and a wrong cause is worse than no cause.
    private const string SkippedBeforeStartStageKey = "management.gameDetection.skippedBeforeStart";

    // What every schedule route reports for a refused run: the Run Now response, the Run All summary,
    // and the card's own message. Shared with the eviction scan, which reads the gate again from inside
    // its own promoted run and has to say the same thing there.
    private const string QueuedUntilCacheIsFree = CacheScanGate.ScheduleQueuedReasonKey;

    // The one schedule whose run type the user chooses. Named here because both the setter and the
    // mapper below have to agree on which card carries a scan mode, and they are far apart.
    private const string ScanModeServiceKey = "gameDetection";

    // The terminal event each cache scan's own card already listens on. A refused run announces
    // itself on the key's existing event rather than on one of its own, so the browser needs no new
    // event name and a skip lands on the card that key would have used had it run.
    private static readonly Dictionary<string, string> _runCompleteEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cacheReconciliation"] = SignalREvents.EvictionScanComplete,
        ["cacheSizeScan"] = SignalREvents.CacheSizeScanComplete,
        ["gameDetection"] = SignalREvents.GameDetectionComplete,
        ["logRotation"] = SignalREvents.LogRotationComplete,
        ["gameImageFetch"] = SignalREvents.GameImageFetchComplete,
        ["cacheSnapshot"] = SignalREvents.CacheSnapshotComplete,
        ["operationHistoryCleanup"] = SignalREvents.OperationHistoryCleanupComplete,
        ["dashboardCacheWarmer"] = SignalREvents.DashboardCacheWarmerComplete,
        ["depotMapping"] = SignalREvents.DepotMappingComplete,
        ["epicMapping"] = SignalREvents.EpicMappingComplete,
        ["xboxMapping"] = SignalREvents.XboxMappingComplete,
        ["battleNetMapping"] = SignalREvents.BattleNetMappingComplete,
        ["riotMapping"] = SignalREvents.RiotMappingComplete,
        ["scheduledPrefill"] = SignalREvents.ScheduledPrefillCompleted,
    };

    private readonly Dictionary<string, ScheduledBackgroundService> _scheduledServices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConfigurableScheduledService> _configurableServices = new(StringComparer.OrdinalIgnoreCase);

    // ConfigurableScheduledService fires its static ServiceExecutionStateChanged event using the
    // protected ServiceName (see ConfigurableScheduledService.cs's ExecuteAsync loop), NOT the
    // ScheduleServiceKey that _configurableServices above is keyed by. Track each tracked configurable
    // service's ServiceName here too so OnServiceExecutionStateChangedAsync's tracked-service guard
    // recognizes the event when it arrives. It maps back to the schedule key, because the run gate
    // is also asked under the ServiceName and everything it looks the answer up in is keyed by the
    // schedule key.
    private readonly Dictionary<string, string> _configurableServiceNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly IStateService _stateService;
    private readonly ISignalRNotificationService _notifications;
    private readonly ScheduleExecutionService _scheduleExecutions;
    private readonly IUnifiedOperationTracker? _tracker;
    private readonly ILogger<ServiceScheduleRegistry> _logger;

    // Optional for the same reason as _tracker below: unit tests construct the registry directly.
    // When it is absent every schedule is allowed to run, which is the behaviour before this gate.
    private readonly CacheScanGate? _cacheScanGate;

    // Serializes the one-kept-card-per-schedule step. The tracker raises each terminal on its own
    // thread-pool task, so two endings of one schedule can reach it at once. Taken before any
    // operation lock and never held across an await or a schedule broadcast.
    private readonly object _keptEndingsLock = new();

    // Each schedule's recent endings, keyed by operation type and, for scheduled prefill, the
    // schedule id. Read and written only under _keptEndingsLock.
    private readonly Dictionary<(OperationType Type, Guid? ScheduleId), ScheduleOutcomes> _scheduleOutcomes = new();

    // Optional (like _tracker) so unit tests that construct the registry directly keep compiling; at
    // runtime DI always supplies it. Every schedule broadcast mirrors the running set into the unified
    // activity registry so the Schedules status dots read the one ActivityUpdated event.
    private readonly IActivityRegistry? _activityRegistry;

    // Serializes every SchedulesUpdated send (see BroadcastSchedulesAsync). Run start/end events fire
    // from many independent service-loop threads; without serialization two full-list snapshots could
    // be sent concurrently and delivered out of order, leaving a finished service stuck "running"
    // (green dot) indefinitely. One in-flight send at a time, draining _pendingSnapshots in FIFO order,
    // guarantees sends are both current and never out of order.
    private readonly SemaphoreSlim _broadcastLock = new(1, 1);

    // Snapshots captured (see BroadcastSchedulesAsync) but not yet sent, in capture order. A snapshot
    // is taken OUTSIDE _broadcastLock, right when a run-start/run-end event fires, rather than after
    // acquiring the lock - reading it only after winning a contended lock could delay the read
    // arbitrarily long past its own triggering event, silently reporting state as of whenever it
    // happens to run instead of as of the transition it was meant to capture. Whichever caller wins
    // _broadcastLock drains this queue to empty (see BroadcastSchedulesAsync) rather than sending only
    // its own snapshot and dropping whatever else queued up behind it - a fast run-start then run-finish
    // pair captured back to back while the lock was busy must both still be sent, in order, or the
    // run's brief "active" moment would never reach a client at all (not just be delayed).
    private readonly Queue<IReadOnlyList<ServiceScheduleInfo>> _pendingSnapshots = new();
    // Guards _pendingSnapshots's enqueue/dequeue (a plain lock is enough - both sides are short,
    // synchronous, in-memory operations; the actual async send is serialized separately by
    // _broadcastLock above, which this lock is never held across).
    private readonly object _snapshotLock = new();

    // The tracker is optional so existing unit tests that construct the registry without one keep
    // compiling; at runtime the DI container always supplies the registered singleton. GetRunStatus
    // reports "not running" when it is absent.
    public ServiceScheduleRegistry(
        IEnumerable<IHostedService> hostedServices,
        IStateService stateService,
        ISignalRNotificationService notifications,
        ScheduleExecutionService scheduleExecutions,
        IUnifiedOperationTracker? tracker = null,
        IActivityRegistry? activityRegistry = null,
        CacheScanGate? cacheScanGate = null,
        ILogger<ServiceScheduleRegistry>? logger = null)
    {
        _stateService = stateService;
        _notifications = notifications;
        _scheduleExecutions = scheduleExecutions;
        _tracker = tracker;
        _activityRegistry = activityRegistry;
        _cacheScanGate = cacheScanGate;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ServiceScheduleRegistry>.Instance;
        foreach (var service in hostedServices)
        {
            if (service is ScheduledBackgroundService scheduledService)
            {
                // Only include explicitly allowed user-configurable services.
                // Infrastructure services are excluded via the allowlist.
                if (_allowedServiceKeys.Contains(scheduledService.ServiceKey))
                {
                    _scheduledServices[scheduledService.ServiceKey] = scheduledService;
                }
            }
            else if (service is ConfigurableScheduledService configurableService)
            {
                var key = GetServiceKey(configurableService);
                _configurableServices[key] = configurableService;

                // Also index by the protected ServiceName used by the ServiceExecutionStateChanged event
                // (see _configurableServiceNames above) so the state-change guard can recognize it.
                var serviceName = (string?)GetPropertyValue(configurableService.GetType(), configurableService, "ServiceName", typeof(string));
                if (!string.IsNullOrEmpty(serviceName))
                {
                    _configurableServiceNames[serviceName] = key;
                }
            }
        }

        ScheduledBackgroundService.ServiceExecutionStateChanged += OnServiceExecutionStateChangedAsync;
        foreach (var (_, loop) in ScheduleLoops())
        {
            loop.RunCompleted = OnRunCompleted;
            loop.Tracker = _tracker;
        }
        ConfigurableScheduledService.ServiceExecutionStateChanged += OnServiceExecutionStateChangedAsync;

        // Same one-time static wiring as the two events above. The registry answers because it is the
        // only place that knows which keys are user-configurable schedules; every other subclass of
        // the scheduled bases, RustSpeedTrackerService included, is never asked and always runs.
        // A registry built without the download gate has no answer to give, so it leaves the hook
        // alone rather than replacing a working one with a function that always says yes.
        if (_cacheScanGate is not null)
        {
            ScheduledServiceBase.ScheduleRunGate = OnScheduleRunGateAsync;
            ScheduledServiceBase.WaitForDownloadAnswer = WaitForDownloadAnswer;
        }

        // Work-state ticks only fire around the scheduling LOOPS. A background run (a
        // fire-and-forget scan, or a wait-queued run promoted after its blocker finished)
        // starts and ends with no tick at all, so without this hook the running dot would
        // miss those runs entirely or stay lit after they finish. Terminal fires exactly
        // once per operation; the broadcast itself dedupes via the activity registry.
        if (_tracker is not null)
        {
            _tracker.OperationTerminal += OnTrackedOperationTerminal;
            _tracker.BlockerCleared += OnBlockerCleared;
            _tracker.EndingKept += RecordKeptEnding;
        }

        // The tracker sees downloads stop the moment it parses a snapshot with nothing in it, which
        // is the busy-to-idle edge itself rather than a schedule happening to ask later. Re-arming
        // the skip announcements from that edge is what stops a second download going unannounced
        // because no schedule polled in the quiet gap between the two. The edge is computed from the
        // unfiltered snapshot, the same set the gate reads: the visible one goes empty while a
        // hidden client is still writing, and re-arming there would announce a skip the gate is
        // still refusing.
        RustSpeedTrackerService.DownloadsEnded += OnDownloadsEnded;
    }

    // Runs on the schedule's run queue, which passes whether the run is kept for later: a run held
    // or queued again keeps its waiting card for the run still to come.
    private void OnRunCompleted(RunNotice notice, string serviceKey, string? error, bool cancelled, bool kept)
    {
        if (_tracker is null) return;
        if (_configurableServiceNames.TryGetValue(serviceKey, out var key)) serviceKey = key;
        if (notice.OperationId is { } operationId)
        {
            var operation = _tracker.GetOperation(operationId);
            if (!kept && operation?.Status == OperationStatus.Waiting && operationId == notice.PendingId)
                _tracker.CompleteOperation(operationId, error is null && !cancelled, error, cancelled, error is null && !cancelled);
            return;
        }
        if (error is null || cancelled || !ScheduleOperationTypes.ByServiceKey.TryGetValue(serviceKey, out var type)
            || !_runCompleteEvents.TryGetValue(serviceKey, out var completeEvent)) return;
        Guid failedId = default;
        failedId = _tracker.RegisterOperation(type, serviceKey, new CancellationTokenSource(),
            onTerminalEmit: outcome => _notifications.NotifyOperationFailedAsync(completeEvent,
                new ScheduledRunCompleteEvent(serviceKey, failedId, false, "", 0, outcome.Error, null,
                    false, OperationStatus.Failed)),
            notice: notice);
        notice.Attach(_tracker, failedId);
        _tracker.CompleteOperation(failedId, success: false, error: error);
    }

    private void OnTrackedOperationTerminal(OperationInfo operation)
    {
        // Subscribed only when the registry was given a tracker (see the constructor).
        var tracker = _tracker!;
        var scheduleId = (operation.Metadata as ScheduledPrefillServiceRunState)?.ScheduleId;
        ScheduleExecution? execution = null;
        lock (operation)
        {
            if (IsScheduleOutcome(operation, scheduleId)
                && operation.NextOperationId is null
                && operation.Status.IsTerminal()
                && operation.CompletedAt is not null
                && TryFindScheduleKey(operation.Type, out var serviceKey))
            {
                execution = _scheduleExecutions.Capture(operation, serviceKey);
            }
        }
        if (execution is not null)
        {
            QueueExecutionWrite(execution);
        }

        // Hybrid counts its week from the last full detection scan, and this is the only place that
        // learns a scan finished without the detection service taking a dependency on state. Stamped
        // after the scan finishes rather than when it starts, so a run that died partway through does
        // not push the next hybrid full scan out by a week. Manual full scans stamp it too: switching
        // to hybrid the day after any full scan should wait a week, not start a second one. Stamped
        // before the schedules broadcast, which carries the scan type Run Now would now request.
        if (operation.Type == OperationType.GameDetection
            && operation.Status == OperationStatus.Completed
            && operation.Metadata is GameDetectionMetrics { ScanType: DetectionScanType.Full })
        {
            _stateService.SetGameDetectionLastFullScan(DateTime.UtcNow);
        }

        if (ScheduleOperationTypes.ByServiceKey.Values.Contains(operation.Type))
        {
            NotifySchedulesChanged();
        }

        // Placed before the two branches below because both return, and their endings are outcomes too.
        RecordKeptEnding(operation);

        // A run declined before it started registers itself only to be reported and carries no
        // terminal broadcast of its own. This registry is already listening here and is the one
        // place that knows which card the schedule owns, so the refusal is announced from here
        // rather than by giving the queue a dependency on schedules.
        // A scan that got past the loop gate and only met the download when its start delegate finally
        // ran. That refusal is thrown inside the delegate and never reaches the run gate, so nothing
        // has held it and nothing will bring it back: the queue cannot park on a download, and this
        // terminal is the last anyone hears of the run. Held here instead, on the same card the other
        // refusals raise.
        // Not gated on the declined-before-start marker, because only one of the two ways in carries
        // it: a run refused the moment it is enqueued is marked, while one refused at promotion
        // completes the waiter itself and is not. Asked of the gate rather than read off the message
        // because this is also reached by heavy-operation conflicts, which the queue parks and retries
        // on its own and must keep doing. A skip that a download caused is decided on the run's own
        // refusal instead of the gate asked again (the eviction scan's outcome, and the queue's decline
        // at an immediate start and at promotion): a download that ended in between would otherwise
        // leave the run neither held nor owed and without a card. The hold's own re-check then
        // releases it at once. The gate is still asked for a skip that carries no mark, such as a start
        // gate that stayed busy. A registry with no gate never holds a run.
        if (operation.Status == OperationStatus.Skipped
            && _cacheScanGate is { } downloadGate
            && (operation.SkippedForDownload || downloadGate.CheckDownloadInProgress() is not null)
            && _cacheReadingOperations.Contains(operation.Type)
            && TryFindScheduleKey(operation.Type, out var heldKey))
        {
            // A service with no loop would hold the run on a card nothing ever takes, so the run ends
            // with the reason the service cannot run.
            if (FindScheduleLoop(heldKey) is ScheduledBackgroundService { DisabledInConfiguration: true } disabledLoop)
            {
                _ = EmitSkippedRunAsync(heldKey, operation.Id, reason: null, disabledLoop.DisabledStageKey);
                return;
            }

            // Off this callback: the tracker is mid-terminal for one operation and holding registers
            // another, which is the tracker re-entered from inside its own notify. The new hold
            // continues this skipped run's card, so the skip itself is closed once the hold is up.
            var heldType = operation.Type;
            var notice = operation.Notice
                ?? new RunNotice(FindScheduleLoop(heldKey)?.EffectiveNotificationMode ?? NotificationMode.All, RunTrigger.Scheduled);
            if (!notice.Cancelled) _ = Task.Run(async () =>
            {
                using (tracker.BeginPromotion(operation.Id, heldType))
                {
                    await HoldRefusedRunAsync(heldKey, heldType, notice);
                }
                tracker.CloseRun(operation.Id);
            });
            return;
        }

        if (operation.Status == OperationStatus.Skipped
            && ReadDeclinedBeforeStart(operation.Metadata)
            && TryFindScheduleKey(operation.Type, out var declinedKey))
        {
            // Passed through as it stands, empty or not. The card reads the reason as
            // error-or-stage-key, and an empty string is neither null nor undefined there, so
            // substituting one for a missing message would render a blank card instead of falling
            // back to the stage key.
            // Reached by a conflict with another operation as well as by a download, so the fallback
            // wording names neither. The specific reason travels on the message above; this is only
            // what the card shows when that message is missing, and blaming a download for a run a
            // heavy operation refused sends the reader looking for a download that was never there.
            // The message is not forwarded. The tracker stores the translated nothing-to-do key for a
            // skip that gave no reason, but the stage key beside it says why this run was refused,
            // which nothing-to-do does not.
            _ = EmitSkippedRunAsync(
                declinedKey,
                operation.Id,
                reason: null,
                SkippedBeforeStartStageKey);
        }
    }

    // One kept ending of each kind per schedule: the newest kept failure replaces the schedule's
    // older failures and carries its failure streak and whether a later run succeeded; a kept
    // skip or warning replaces only the older one of the same kind. A phase run under a parent,
    // the prefill run-level container, a waiting record that handed its work on and a mapping
    // sign-in are not outcomes of a schedule; runs of no schedule keep one card each. Runs from the
    // terminal handler, and again from EndingKept when a warning set after the run ended made it kept.
    private void RecordKeptEnding(OperationInfo operation)
    {
        var scheduleId = (operation.Metadata as ScheduledPrefillServiceRunState)?.ScheduleId;
        if (!IsScheduleOutcome(operation, scheduleId))
        {
            return;
        }

        // Subscribed only when the registry was given a tracker (see the constructor).
        var tracker = _tracker!;
        lock (_keptEndingsLock)
        {
            // Status, revision and handoff are frozen by the tracker at completion, so they read the same
            // whether or not a person has since closed this ending or the tracker has reaped it. Whether it
            // is kept can change once, when a warning set after completion makes it kept; the tracker then
            // raises EndingKept, which runs this pass again.
            OperationStatus status;
            long completedRevision;
            Guid? handedTo;
            bool wasKept;
            lock (operation)
            {
                status = operation.Status;
                completedRevision = operation.CompletedRevision;
                handedTo = operation.NextOperationId;
                wasKept = UnifiedOperationTracker.KeepsUntilClosed(operation);
            }

            if (handedTo is null)
            {
                if (!_scheduleOutcomes.TryGetValue((operation.Type, scheduleId), out var outcomes))
                {
                    outcomes = new ScheduleOutcomes();
                    _scheduleOutcomes.Add((operation.Type, scheduleId), outcomes);
                }

                // Every ending is recorded by the revision that orders it, so the streak is read
                // from the endings themselves and comes out the same whatever order the handlers
                // run in. Any ending other than a failure breaks the streak, whether or not it
                // kept a card.
                outcomes.Endings[completedRevision] = status;

                var typeWire = operation.Type.ToWireString();
                var failedWire = OperationStatus.Failed.ToWireString();
                var statusWire = status.ToWireString();
                var handledFailed = status == OperationStatus.Failed;
                var scheduleRuns = tracker.GetRuns().Runs
                    .Where(run => run.OperationType == typeWire && run.ScheduleId == scheduleId && !run.IntegrationLogin)
                    .ToList();
                var endings = scheduleRuns.Where(run => run.Retained && !run.Closed).ToList();

                // A kept ending replaces the older kept endings of its own kind only, so the
                // schedule never collects a card per run and an ending never closes one of
                // another kind. A pass never closes an ending newer than its own, so the result
                // is the same whatever order the handlers run in.
                if (wasKept)
                {
                    // A failed-out repair's card holds its Retry until the user closes it, and a
                    // repair still running, a retried one included, keeps its card until it ends.
                    foreach (var older in endings.Where(run => run.CompletedRevision < completedRevision
                        && run.Status == statusWire && run.RepairError is null && !run.Repairing))
                    {
                        tracker.CloseRun(older.OperationId);
                    }
                }

                var newest = endings
                    .Where(run => run.Status == failedWire && (!handledFailed || run.CompletedRevision >= completedRevision))
                    .MaxBy(run => run.CompletedRevision);
                if (newest?.CompletedRevision is { } newestRevision)
                {
                    var streak = outcomes.Endings
                        .Where(ending => ending.Key <= newestRevision)
                        .Reverse()
                        .TakeWhile(ending => ending.Value == OperationStatus.Failed)
                        .Count();
                    var latestRunSucceeded = outcomes.Endings
                        .Any(ending => ending.Key > newestRevision && ending.Value == OperationStatus.Completed);
                    tracker.UpdateKeptEnding(newest.OperationId, streak, latestRunSucceeded);
                }

                // A streak is only ever read from a listed ending, so everything below the break
                // just under the oldest listed ending (this one included) can go; the break stays
                // so a failure after it still stops counting there.
                var floor = scheduleRuns
                    .Select(run => run.CompletedRevision)
                    .OfType<long>()
                    .Append(completedRevision)
                    .Min();
                var lastBreak = outcomes.Endings
                    .Where(ending => ending.Key < floor && ending.Value != OperationStatus.Failed)
                    .Select(ending => ending.Key)
                    .DefaultIfEmpty()
                    .Max();
                foreach (var pruned in outcomes.Endings.Keys.Where(revision => revision < lastBreak).ToList())
                {
                    outcomes.Endings.Remove(pruned);
                }
            }
        }
    }

    // EndRepair always raises BlockerCleared. The terminal pass leaves a repairing ending open, so
    // once its repair ends well the ending is closed here when a newer failure of its schedule
    // replaced it, whether or not the newer card is still open.
    private void OnBlockerCleared()
    {
        // Subscribed only when the registry was given a tracker (see the constructor).
        var tracker = _tracker!;
        var failedWire = OperationStatus.Failed.ToWireString();
        lock (_keptEndingsLock)
        {
            foreach (var run in tracker.GetRuns().Runs.Where(run => run.Retained && !run.Closed
                && run.Status == failedWire && run.RepairError is null && !run.Repairing))
            {
                // Null when the run was reaped after the list was read.
                if (tracker.GetOperation(run.OperationId) is not { } operation) continue;
                var scheduleId = (operation.Metadata as ScheduledPrefillServiceRunState)?.ScheduleId;
                if (IsScheduleOutcome(operation, scheduleId)
                    && _scheduleOutcomes.TryGetValue((operation.Type, scheduleId), out var outcomes)
                    && outcomes.Endings.Any(ending => ending.Key > run.CompletedRevision
                        && ending.Value == OperationStatus.Failed))
                {
                    tracker.CloseRun(run.OperationId);
                }
            }
        }
    }

    private static bool IsScheduleOutcome(OperationInfo operation, Guid? scheduleId)
        => ScheduleOperationTypes.ByServiceKey.Values.Contains(operation.Type)
            && (operation.Type != OperationType.ScheduledPrefill || scheduleId is not null)
            && (operation.ParentOperationId is null || scheduleId is not null)
            && UnifiedOperationTracker.ReadIntegrationLogin(operation.Metadata) is null;

    private void QueueExecutionWrite(ScheduleExecution execution)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (await _scheduleExecutions.InsertAsync(execution))
                {
                    await BroadcastSchedulesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not finish schedule execution write {OperationId}", execution.OperationId);
            }
        });
    }

    /// <summary>
    /// Downloads have stopped, so every schedule may announce its next skip. The polled clear in
    /// <see cref="CheckScheduleRunAsync"/> stays as the backstop for the cases this edge deliberately
    /// does not cover: a process that starts up with a download already in flight, and a tracker
    /// that dies, which stops answering rather than reporting that anything finished.
    /// </summary>
    private void OnDownloadsEnded()
    {
        // Raised on the tracker's stdout thread. Each schedule's run queue decides its own held run;
        // a run that goes ahead only wakes its loop, so the work still happens on the loop's thread.
        ReleaseHolds();
    }

    /// <summary>
    /// Releases every run that was held while a download was in flight, posting the release to each
    /// schedule's run queue. Each queue handles messages in the order they were written, so a caller
    /// that asks a queue next sees the release.
    /// </summary>
    private void ReleaseHolds()
    {
        foreach (var (key, loop) in ScheduleLoops())
        {
            loop.ReleaseHeldRun(held => GetRunThatDoesTheWork(key, held.RequestedScanType) is { OperationId: { } id }
                && Guid.TryParse(id, out var running)
                    ? running
                    : null);
        }
    }

    private IEnumerable<(string Key, ScheduledServiceBase Loop)> ScheduleLoops()
        => _scheduledServices.Select(entry => (entry.Key, (ScheduledServiceBase)entry.Value))
            .Concat(_configurableServices.Select(entry => (entry.Key, (ScheduledServiceBase)entry.Value)));

    /// <summary>
    /// The loop that owns a schedule key, whichever of the two bases it runs on. Callers pick the
    /// trigger themselves because the two differ in how the run is attributed: a click is Manual, a
    /// run that was owed keeps the attribution it would have had.
    /// </summary>
    private ScheduledServiceBase? FindScheduleLoop(string serviceKey)
    {
        if (_scheduledServices.TryGetValue(serviceKey, out var scheduled))
        {
            return scheduled;
        }

        return _configurableServices.TryGetValue(serviceKey, out var configurable) ? configurable : null;
    }

    private static bool ReadDeclinedBeforeStart(object? metadata)
    {
        var value = metadata switch
        {
            IReadOnlyDictionary<string, object?> readOnly when readOnly.TryGetValue(DeclinedRunMetadata.Key, out var v) => v,
            IDictionary<string, object> mutable when mutable.TryGetValue(DeclinedRunMetadata.Key, out var v) => v,
            _ => null,
        };

        return value is true;
    }

    /// <summary>
    /// The schedule key whose card belongs to <paramref name="operationType"/>, searching only the
    /// keys that have a terminal event, which is the same set that can be declined.
    /// </summary>
    private static bool TryFindScheduleKey(OperationType operationType, out string serviceKey)
    {
        if (ScheduleOperationTypes.FindServiceKey(operationType) is { } key && _runCompleteEvents.ContainsKey(key))
        {
            serviceKey = key;
            return true;
        }

        serviceKey = string.Empty;
        return false;
    }

    private async void OnServiceExecutionStateChangedAsync(string serviceKey)
    {
        // Mirror the same allowlist gate applied when populating _scheduledServices/_configurableServices:
        // a service this registry doesn't track (excluded from _allowedServiceKeys, or an infrastructure
        // service like PersistentSessionExpiryService) must not trigger a Schedules broadcast either.
        // Without this check, ANY ScheduledBackgroundService/ConfigurableScheduledService subclass firing
        // this static event - tracked or not - would still spam every connected client on every tick.
        //
        // ConfigurableScheduledService fires this event keyed by ServiceName, not ScheduleServiceKey, so
        // _configurableServiceNames (populated alongside _configurableServices in the constructor) must
        // be checked too - otherwise every tracked configurable service's broadcast would be dropped here.
        if (!_scheduledServices.ContainsKey(serviceKey) &&
            !_configurableServices.ContainsKey(serviceKey) &&
            !_configurableServiceNames.ContainsKey(serviceKey))
        {
            return;
        }

        try
        {
            await BroadcastSchedulesAsync();
        }
        catch
        {
            // Non-fatal - SignalR broadcast failure should not affect service execution
        }
    }

    public void NotifySchedulesChanged()
    {
        // Re-use the same fire-and-forget SignalR broadcast path as work-state ticks so
        // schedule changes propagate to the Schedules UI without a page reload. Any error is
        // swallowed - matches existing pattern.
        _ = NotifySchedulesAsync();
    }

    private async Task NotifySchedulesAsync()
    {
        try
        {
            await BroadcastSchedulesAsync();
        }
        catch
        {
            // Non-fatal - SignalR broadcast failure should not affect service execution
        }
    }

    // The single serialized broadcast path. GetAll() is snapshotted and queued (see _pendingSnapshots)
    // before the async _broadcastLock below is even requested, so the payload reflects state as of
    // THIS call's own trigger, not whenever it eventually wins the lock. Whichever caller wins the lock
    // drains the ENTIRE queue in order - not just its own snapshot - so a snapshot enqueued by another
    // concurrent caller while this one waited is never skipped; every captured transition is sent.
    // Public so the controllers route their config/reset broadcasts through the same lock (see
    // IServiceScheduleRegistry).
    public async Task BroadcastSchedulesAsync()
    {
        lock (_snapshotLock)
        {
            _pendingSnapshots.Enqueue(GetAll());
        }

        await _broadcastLock.WaitAsync();
        try
        {
            while (true)
            {
                IReadOnlyList<ServiceScheduleInfo>? next;
                lock (_snapshotLock)
                {
                    next = _pendingSnapshots.Count > 0 ? _pendingSnapshots.Dequeue() : null;
                }
                if (next is null)
                {
                    break;
                }

                // NotifyAllAsync/ReplaceAsync already swallow their own failures internally, so this is
                // belt-and-suspenders: if a future change to either ever let an exception through, it
                // must not abandon the drain loop and strand every snapshot still queued behind this one
                // - the drain-then-send guarantee (see class doc) applies to the WHOLE queue, not just
                // whichever item happened to be dequeued first.
                try
                {
                    await _notifications.NotifyAllAsync(SignalREvents.SchedulesUpdated, next);

                    // Mirror the running set into the unified activity registry so the Schedules status
                    // dots read the one ActivityUpdated event. ReplaceAsync sets exactly the running
                    // services active and clears the rest, and only broadcasts on an actual change.
                    if (_activityRegistry is not null)
                    {
                        var running = next
                            .Where(s => s.IsRunning)
                            .ToDictionary(s => s.Key, _ => 1, StringComparer.Ordinal);
                        await _activityRegistry.ReplaceAsync(ActivityDomains.Schedule, ActivityAspects.Running, running);
                    }
                }
                catch
                {
                    // Non-fatal - move on to whatever else is queued rather than losing it too.
                }
            }
        }
        finally
        {
            _broadcastLock.Release();
        }
    }

    // Sent under the same lock as SchedulesUpdated, and it reads the stored value only once it holds
    // the lock, so when two admins change the setting at once the last push carries the last saved
    // value.
    public async Task PublishGlobalNotificationDisplayModeAsync()
    {
        await _broadcastLock.WaitAsync();
        try
        {
            await _notifications.NotifyAllAsync(
                SignalREvents.NotificationDisplayModeChanged,
                new GlobalNotificationDisplayMode(_stateService.GetGlobalNotificationDisplayMode()));
        }
        finally
        {
            _broadcastLock.Release();
        }
    }

    public IReadOnlyList<ServiceScheduleInfo> GetAll()
    {
        var results = new List<ServiceScheduleInfo>();

        foreach (var service in _scheduledServices.Values)
        {
            results.Add(MapScheduledService(service));
        }

        foreach (var service in _configurableServices.Values)
        {
            results.Add(MapConfigurableService(service));
        }

        return results;
    }

    public ServiceScheduleInfo? Get(string serviceKey)
    {
        if (_scheduledServices.TryGetValue(serviceKey, out var scheduled))
        {
            return MapScheduledService(scheduled);
        }

        if (_configurableServices.TryGetValue(serviceKey, out var configurable))
        {
            return MapConfigurableService(configurable);
        }

        return null;
    }

    public void SetInterval(string serviceKey, double intervalHours)
    {
        if (_scheduledServices.TryGetValue(serviceKey, out var scheduled))
        {
            scheduled.SetInterval(TimeSpan.FromHours(intervalHours));
            _stateService.SetServiceInterval(serviceKey, intervalHours);
            return;
        }

        if (_configurableServices.TryGetValue(serviceKey, out var configurable))
        {
            ApplyInterval(configurable, TimeSpan.FromHours(intervalHours));
            _stateService.SetServiceInterval(serviceKey, intervalHours);
            return;
        }
    }

    public void SetRunOnStartup(string serviceKey, bool runOnStartup)
    {
        if (_scheduledServices.TryGetValue(serviceKey, out var scheduled))
        {
            scheduled.SetRunOnStartup(runOnStartup);
            _stateService.SetServiceRunOnStartup(serviceKey, runOnStartup);
            return;
        }

        if (_configurableServices.TryGetValue(serviceKey, out var configurable))
        {
            configurable.SetRunOnStartup(runOnStartup);
            _stateService.SetServiceRunOnStartup(serviceKey, runOnStartup);
        }
    }

    public void SetNotificationMode(string serviceKey, NotificationMode mode)
    {
        if (_scheduledServices.TryGetValue(serviceKey, out var scheduled))
        {
            scheduled.SetNotificationMode(mode);
            _stateService.SetServiceNotificationMode(serviceKey, mode);
            return;
        }

        if (_configurableServices.TryGetValue(serviceKey, out var configurable))
        {
            configurable.SetNotificationMode(mode);
            _stateService.SetServiceNotificationMode(serviceKey, mode);
        }
    }

    public void SetNotificationDisplayMode(string serviceKey, NotificationDisplayMode mode)
    {
        if (!_scheduledServices.ContainsKey(serviceKey) && !_configurableServices.ContainsKey(serviceKey))
        {
            return;
        }

        // Unlike SetNotificationMode, no live service instance reads this value (MapScheduledService/
        // MapConfigurableService resolve it straight from state at read time), so persistence here is
        // the entire write path.
        _stateService.SetServiceNotificationDisplayMode(serviceKey, mode);
    }

    public void ClearNotificationDisplayMode(string serviceKey)
    {
        _stateService.ClearServiceNotificationDisplayMode(serviceKey);
    }

    public NotificationDisplayMode GetGlobalNotificationDisplayMode() => _stateService.GetGlobalNotificationDisplayMode();

    public async Task SetGlobalNotificationDisplayModeAsync(NotificationDisplayMode mode)
    {
        _stateService.SetGlobalNotificationDisplayMode(mode);
        await PublishGlobalNotificationDisplayModeAsync();
        // Every schedule without a style of its own now resolves to the new value.
        await BroadcastSchedulesAsync();
    }

    public bool SetScanMode(string serviceKey, GameDetectionScanMode mode)
    {
        if (!string.Equals(serviceKey, ScanModeServiceKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // As with SetNotificationDisplayMode, no live service instance holds this value: the
        // detection loop reads it from state at the top of each run, so persistence here is the
        // entire write path and a run already in flight keeps the mode it started with.
        _stateService.SetGameDetectionScanMode(mode);
        return true;
    }

    public bool SetCustomSchedule(string serviceKey, CustomSchedule? schedule)
    {
        // Both loop bases compute their next run through ScheduleTiming, so either kind of service can
        // act on a schedule. The one exception is below.
        ScheduledServiceBase? service = null;
        if (_scheduledServices.TryGetValue(serviceKey, out var scheduled))
        {
            service = scheduled;
        }
        else if (_configurableServices.TryGetValue(serviceKey, out var configurable))
        {
            service = configurable;
        }

        if (service is null)
        {
            return false;
        }

        // Scheduled prefill's own interval is a fixed 1-minute due-check poll, not a cadence the user
        // chose, and its real schedules live per platform in the prefill config. Driving that poll off
        // a cron expression would mean every platform's due-check only ran at the cron times.
        if (service is IScheduleEnabledGate)
        {
            return false;
        }

        service.UpdateCustomSchedule(schedule);

        if (schedule is null)
        {
            _stateService.ClearServiceCustomSchedule(serviceKey);
        }
        else
        {
            _stateService.SetServiceCustomSchedule(serviceKey, schedule);
        }

        return true;
    }

    public void ResetToDefaults()
    {
        foreach (var (key, service) in _scheduledServices)
        {
            service.ResetInterval();
            service.SetRunOnStartup(null);
            service.SetNotificationMode(null);
            // Cleared explicitly rather than by ResetInterval, which only owns the interval: a custom
            // schedule left behind here would keep overriding the interval the reset just restored,
            // so "Reset to Defaults" would visibly not reset the one thing the user changed.
            service.UpdateCustomSchedule(null);
            _stateService.ClearServiceInterval(key);
            _stateService.ClearServiceRunOnStartup(key);
            _stateService.ClearServiceNotificationMode(key);
            _stateService.ClearServiceNotificationDisplayMode(key);
            _stateService.ClearServiceCustomSchedule(key);
        }

        foreach (var (key, service) in _configurableServices)
        {
            service.ResetInterval();
            service.SetRunOnStartup(null);
            service.SetNotificationMode(null);
            // Cleared explicitly rather than by ResetInterval, which only owns the interval: a custom
            // schedule left behind here would keep overriding the interval the reset just restored,
            // so "Reset to Defaults" would visibly not reset the one thing the user changed.
            service.UpdateCustomSchedule(null);
            _stateService.ClearServiceInterval(key);
            _stateService.ClearServiceRunOnStartup(key);
            _stateService.ClearServiceNotificationMode(key);
            _stateService.ClearServiceNotificationDisplayMode(key);
            _stateService.ClearServiceCustomSchedule(key);
        }

        // Scheduled prefill keeps its cadence per-service in the config DTO + durable last-run map
        // (not in ServiceIntervals). Clearing that map alone would make the next poll treat every enabled
        // service as never-run and instant-run it, so ClearScheduledPrefillServiceLastRun also re-anchors
        // the currently-enabled services to now — a reset returns to a "wait one full interval" baseline
        // rather than leaving stale next-run times or triggering an immediate run.
        _stateService.ClearScheduledPrefillServiceLastRun();

        // Game detection's scan mode is a single value rather than a per-service entry, so the loops
        // above never reach it. Left behind, a schedule the user had switched to incremental would go
        // on running incrementally after a reset that restored everything else.
        _stateService.SetGameDetectionScanMode(GameDetectionScanMode.Full);

        // The global display default is one value too. The loops above cleared every schedule's own
        // style, so this is what they all render with after the reset; the caller sends it.
        _stateService.SetGlobalNotificationDisplayMode(NotificationDisplayMode.Condensed);

        // Scheduled prefill's notification mode lives per-platform in the config DTO, not in the
        // base-class override the loop above already reset (that reset is a no-op for this service -
        // ScheduledPrefillService never reads EffectiveNotificationMode). Reset each platform's mode
        // explicitly or a platform left on Manual/Silent survives "Reset to Defaults" unchanged.
        var prefillConfig = _stateService.GetScheduledPrefillConfig();
        _stateService.SetScheduledPrefillConfig(ScheduledPrefillConfigFactory.ResetNotificationModes(prefillConfig));
    }

    /// <summary>
    /// Whether the schedule behind <paramref name="serviceKey"/> may start a run right now: the reason
    /// it may not, or null when it may. Only the keys in <see cref="_allowedServiceKeys"/> are asked,
    /// which is what keeps every other subclass of the two scheduled bases - the speed tracker that
    /// produces the download answer among them - running exactly as it did.
    /// </summary>
    private async Task<ScheduleRunCheck> CheckScheduleRunAsync(string serviceKey, RunNotice notice)
    {
        if (_cacheScanGate is null || !ScheduleOperationTypes.ByServiceKey.TryGetValue(serviceKey, out var operationType))
        {
            return new ScheduleRunCheck(null, notice);
        }

        var downloadDenial = _cacheScanGate.CheckDownloadInProgress();
        if (downloadDenial is null)
        {
            // The backstop for the tracker's own downloads-ended edge, which is missed when the
            // process starts with a download already running, and when the tracker dies and stops
            // answering rather than reporting that anything finished. Without this the schedules
            // waiting on that edge would sit until their next ordinary interval. This schedule's own
            // held run joins the run asking now when the loop already took that run, or the request
            // that follows when the caller is Run Now or Run All.
            ReleaseHolds();
            return new ScheduleRunCheck(null, notice);
        }

        // A job that never walks the cache tree has no reason to wait for a download to finish, so it
        // gets the same question and a different answer.
        if (!_cacheReadingOperations.Contains(operationType))
        {
            return new ScheduleRunCheck(null, notice);
        }

        // Held here rather than at each caller because every route that refuses a run comes through
        // this one method: the schedule loops, Run Now, and Run All. Holding it at the callers is
        // what left Run All reporting three schedules as simply not run.
        var held = await HoldRefusedRunAsync(serviceKey, operationType, notice);

        // Not the gate's own sentence, which ends in "try again once it finishes" and is written for
        // the controllers, where a refused scan really is over. A schedule's run is kept, so telling
        // the person to come back and repeat it describes work they do not have to do. The gate's two
        // causes, a download writing and the tracker not having reported yet, are deliberately not
        // repeated: they differ in what a person would do about them, and here there is nothing to do
        // about either. The controllers still get the gate's own wording.
        return new ScheduleRunCheck(QueuedUntilCacheIsFree, held);
    }

    // The gate's refusal (null when the run may start) and the notice that stands for the run: the hold
    // the run joined when it was refused, or the caller's own notice.
    private sealed record ScheduleRunCheck(string? Denial, RunNotice Notice);

    /// <summary>
    /// Waits, before a startup run is asked about, for the tracker to have something to say. Only
    /// the schedules whose work walks the cache tree can be refused for a download, so every other
    /// service starts as promptly as it did before: the live log monitor and the dashboard warmer
    /// both run on startup and neither has any reason to wait for a download answer.
    /// </summary>
    private Task WaitForDownloadAnswer(string announcedKey, CancellationToken cancellationToken)
    {
        var serviceKey = _configurableServiceNames.TryGetValue(announcedKey, out var scheduleKey)
            ? scheduleKey
            : announcedKey;

        if (_cacheScanGate is null
            || !ScheduleOperationTypes.ByServiceKey.TryGetValue(serviceKey, out var operationType)
            || !_cacheReadingOperations.Contains(operationType))
        {
            return Task.CompletedTask;
        }

        return _cacheScanGate.WaitForDownloadAnswerAsync(cancellationToken);
    }

    /// <summary>
    /// The answer given to a service loop, which declines above its own run bookkeeping and so
    /// registers nothing itself. A refusal is recorded here as one skipped operation carrying the
    /// reason, because the loop's own broadcast has no field that could carry it. The two manual
    /// routes below ask <see cref="CheckScheduleRunAsync"/> directly instead: they return the reason on
    /// the response the caller is waiting for, so a second record would double-report one click.
    /// </summary>
    private async Task<string?> OnScheduleRunGateAsync(string announcedKey, RunTrigger trigger)
    {
        // The two loop bases call themselves different things. ScheduledBackgroundService announces
        // its ServiceKey, which is already the schedule key; ConfigurableScheduledService announces
        // its ServiceName, which is not, so it is translated through the same index the
        // execution-state guard uses. Without this every configurable schedule missed the lookup and
        // was silently never asked.
        var serviceKey = _configurableServiceNames.TryGetValue(announcedKey, out var scheduleKey)
            ? scheduleKey
            : announcedKey;

        var loop = FindScheduleLoop(serviceKey);
        var notice = loop?.CurrentRunNotice ?? new RunNotice(NotificationMode.All, trigger);
        var check = await CheckScheduleRunAsync(serviceKey, notice);
        loop?.SelectRunNotice(check.Notice);
        return check.Denial;
    }

    /// <summary>
    /// Puts the schedule's run on the waiting card that stays in the notification bar until the run
    /// actually starts, which is the same card a run blocked by another heavy operation already gets
    /// from the operation queue. The schedule's run queue keeps one held run: a schedule already
    /// holding one does not raise a second card, so a loop refused every interval for an hour shows one.
    /// </summary>
    private async Task<RunNotice> HoldRefusedRunAsync(string serviceKey, OperationType operationType, RunNotice notice)
    {
        // A registry built without this schedule's loop has nothing to hold the run on.
        if (FindScheduleLoop(serviceKey) is not { } loop) return notice;
        // Read here, never on the run queue's reader: reading the download gate can raise the
        // downloads-ended edge on the thread that reads it.
        // The prefill is the only blocker the app tracks that can be writing to the cache, so it is
        // the only one that can be named - but the gate answers whether bytes are landing, not whose.
        // Naming it while a machine on the LAN is also downloading would be a guess, and a wrong one
        // sends the reader to watch the prefill finish while the scan stays held for the other
        // download. So it is named only when the prefill is the single client on the wire; anything
        // else leaves this null and the card says it waits for downloads, which is true whoever is
        // writing. Carried on the tracked row, so a card rebuilt after a page refresh says the same thing.
        var blocker = _tracker is not null && _cacheScanGate?.ActiveDownloadingClients() == 1
            ? _tracker
                .GetActiveOperations(OperationType.ScheduledPrefill)
                .FirstOrDefault(op => op.Metadata is not ScheduledPrefillServiceRunState)
            : null;
        // A run held again from its skipped ending names that ending as the card it continues through
        // the promotion scope its caller set. That scope lives in this call's execution context, and
        // the card is raised on the run queue's reader, so the context goes with it. Flow is never
        // suppressed on these callers, so there is always a context to capture. Captured before the
        // first await, while the caller's scope is still current.
        var context = ExecutionContext.Capture()!;
        var held = await loop.HoldRunAsync(notice, card =>
            ExecutionContext.Run(context, _ => RaiseHoldCard(loop, serviceKey, operationType, card, blocker), null));
        // A download that ended between the gate's answer and this hold released nothing, so this hold
        // would wait for the next edge. A registry with no gate never holds a run, so it has nothing to release.
        if (_cacheScanGate is { } gate)
        {
            if (gate.CheckDownloadInProgress() is null) ReleaseHolds();
            else _ = ReleaseHoldsWhenTrackerAnswersAsync(gate);
        }
        return held;
    }

    // A run refused in the first seconds after the tracker starts is held before any download was seen, so
    // no downloads-ended edge follows it. Once the tracker has answered, or its silence stops counting as an
    // answer, a gate that now allows the scan releases the hold.
    private async Task ReleaseHoldsWhenTrackerAnswersAsync(CacheScanGate gate)
    {
        await gate.WaitForDownloadAnswerAsync(CancellationToken.None);
        if (gate.CheckDownloadInProgress() is null) ReleaseHolds();
    }

    // Runs on the schedule's run queue, which decided this run is the schedule's new hold.
    private void RaiseHoldCard(ScheduledServiceBase loop, string serviceKey, OperationType operationType, RunNotice notice,
        OperationInfo? blocker)
    {
        if (_tracker is null) return;
        var displayName = ScheduleTitles.ByServiceKey[serviceKey];

        var cts = new CancellationTokenSource();

        var heldId = _tracker.RegisterOperation(
            operationType,
            displayName,
            cts,
            // Waiting is deliberately excluded from GetActiveOperations, so this held run cannot
            // block the conflict check or light the Schedules running dot before it starts.
            initialStatus: OperationStatus.Waiting,
            blockedByName: blocker?.Name,
            notice: notice,
            blockedByType: blocker?.Type,
            blockedByTarget: blocker?.Target,
            // The scan the held run will request, so the card names it and Run All and Run Now see a waiting
            // scan of the schedule's own type.
            detectionScanType: loop.ScanTypeOf(notice),
            waitingForDownload: true);
        notice.Token = cts.Token;
        notice.PendingId = heldId;
        notice.Attach(_tracker, heldId);
        DropWhenDismissed(loop, _tracker, cts.Token, heldId, notice);
    }

    // Runs on the schedule's run queue, inside the admission of a run queued behind a busy one.
    private void AcknowledgeRun(ScheduledServiceBase loop, string serviceKey, OperationType operationType, RunNotice notice)
    {
        if (_tracker is null || serviceKey is "depotMapping" or "scheduledPrefill" || notice.Cancelled) return;
        if (notice.PendingId is null)
        {
            var cts = new CancellationTokenSource();
            var pendingId = _tracker.RegisterOperation(operationType, ScheduleTitles.ByServiceKey[serviceKey], cts,
                initialStatus: OperationStatus.Waiting, notice: notice, detectionScanType: loop.ScanTypeOf(notice));
            notice.PendingId = pendingId;
            notice.Token = cts.Token;
            DropWhenDismissed(loop, _tracker, cts.Token, pendingId, notice);
        }
        if (notice.OperationId is null) notice.Attach(_tracker, notice.PendingId.Value);
        // The waiting run's row reads the notice, whose trigger a Run Now may have just raised.
        _tracker.RefreshRun(notice.PendingId.Value);
    }

    /// <summary>
    /// A waiting card ends here only while it is still parked; once a worker runs under its id, that
    /// worker reports the terminal after releasing its gate. Dropping the request is the other half -
    /// without it the person dismisses the card and the run still fires later. The notice is marked
    /// at once, so a run queue reading it before the dismissal arrives already treats it as gone.
    /// </summary>
    private static void DropWhenDismissed(
        ScheduledServiceBase loop, IUnifiedOperationTracker tracker, CancellationToken token, Guid cardId, RunNotice notice)
        => token.Register(() =>
        {
            notice.Cancel(tracker, cardId);
            loop.DismissCard(cardId, notice);
        });

    /// <summary>
    /// Announces a refused run on the schedule's own terminal event: a translation key for the card
    /// to render, and the gate's own sentence beside it for anything that reports the raw reason.
    /// A stage key with a placeholder in it needs <paramref name="context"/> filled, or the card
    /// shows the placeholder; a key that reads on its own leaves it null.
    /// </summary>
    private async Task EmitSkippedRunAsync(
        string serviceKey,
        Guid operationId,
        string? reason,
        string stageKey,
        Dictionary<string, object?>? context = null)
    {
        if (!_runCompleteEvents.TryGetValue(serviceKey, out var completeEvent))
        {
            return;
        }

        var terminal = new ScheduledRunCompleteEvent(
            serviceKey,
            operationId,
            Success: true,
            StageKey: stageKey,
            // A run that was refused did nothing, so there is no progress to claim.
            PercentComplete: 0,
            Error: reason,
            Context: context,
            Cancelled: false,
            Status: OperationStatus.Skipped);

        await _notifications.NotifyAllAsync(completeEvent, terminal);
    }

    public async Task<(ScheduleRunStatus Status, string? SkippedReason, bool FollowUpQueued)> TriggerRunAsync(
        string serviceKey,
        ScheduleActor? actor = null)
    {
        var loop = FindScheduleLoop(serviceKey);
        // A scheduled prefill with no schedule enabled has nothing to run, so the click is refused
        // instead of starting a run that ends at once.
        if (loop is IScheduleEnabledGate gate && !gate.HasAnyServiceEnabled())
        {
            throw new ConflictException("Enable a prefill schedule to run it")
            {
                StageKey = "management.schedules.services.scheduledPrefill.runNowNoSchedule"
            };
        }
        // A service turned off in configuration has no loop to take the run, so the click is refused rather
        // than answered as started.
        if (loop is ScheduledBackgroundService { DisabledInConfiguration: true } disabledLoop)
        {
            throw new ConflictException("This service cannot run")
            {
                StageKey = disabledLoop.DisabledStageKey
            };
        }

        var notice = new RunNotice(
            loop?.EffectiveNotificationMode ?? NotificationMode.All,
            RunTrigger.Manual,
            actor);
        var check = await CheckScheduleRunAsync(serviceKey, notice);
        if (check.Denial is not null)
        {
            return (new ScheduleRunStatus { IsRunning = false }, check.Denial, false);
        }

        var statusBeforeTrigger = GetRunThatDoesTheWork(serviceKey) ?? new ScheduleRunStatus { IsRunning = false };
        var followUpQueued = false;
        if (loop is not null)
        {
            // The card goes up inside the admission, so the loop cannot take the run before it has one,
            // and this answers only after the card exists.
            var admission = await loop.TryTriggerImmediateRunAsync(check.Notice, (retained, followUp) =>
            {
                if (followUp && ScheduleOperationTypes.ByServiceKey.TryGetValue(serviceKey, out var pendingType))
                    AcknowledgeRun(loop, serviceKey, pendingType, retained);
            });
            followUpQueued = admission.FollowUpQueued;
            if (!admission.Admitted || followUpQueued) statusBeforeTrigger.IsRunning = true;
        }

        return (statusBeforeTrigger, null, followUpQueued);
    }

    public ScheduleRunStatus? GetRunStatus(string serviceKey)
    {
        if (!ScheduleOperationTypes.ByServiceKey.TryGetValue(serviceKey, out var operationType))
        {
            // Unknown key: the caller (controller) turns this into a 404.
            return null;
        }

        // Scheduled prefill registers one tracked operation per due platform beside its run-level
        // one, so several operations of the same type are active at once during a run. This card
        // reports the RUN, so the per-platform operations are filtered out here: a bare
        // FirstOrDefault would otherwise hand the card whichever operation the tracker happened to
        // enumerate first, and its id is what the card's Cancel targets.
        // A child step (an eviction scan's own detection) shares the type but serves only its parent, the
        // same rule the operation queue applies, so it is never the schedule's run.
        var active = _tracker?
            .GetActiveOperations(operationType)
            .FirstOrDefault(op => op.Metadata is not ScheduledPrefillServiceRunState && op.ParentOperationId is null);
        if (active == null)
        {
            return new ScheduleRunStatus { IsRunning = false };
        }

        // The run reporter carries the stage key as the operation's Message. How the run is drawn
        // comes from its row in the run list, not from this status.
        return new ScheduleRunStatus
        {
            IsRunning = true,
            OperationId = active.Id.ToString(),
            Status = active.Status.ToWireString(),
            PercentComplete = active.PercentComplete,
            StageKey = string.IsNullOrEmpty(active.Message) ? null : active.Message,
            Context = UnifiedOperationTracker.ReadContext(active.Metadata),
        };
    }

    /// <summary>
    /// The schedule's run that a new request would only repeat. A cache scan that is being canceled
    /// stops before it does the work, and a new request for it waits in the queue until it has ended
    /// (OperationQueueService.GetDuplicateId), so it does not count. Any other schedule's run counts
    /// until it ends: its work does not go through the queue, so a second run would not wait for it.
    /// GetRunStatus keeps reporting the canceled scan, because the Schedules card shows it until it
    /// stops.
    /// </summary>
    private ScheduleRunStatus? GetRunThatDoesTheWork(string serviceKey, DetectionScanType? requestedScanType = null)
    {
        var status = GetRunStatus(serviceKey);
        if (status is not { IsRunning: true }) return null;
        var operationType = ScheduleOperationTypes.ByServiceKey[serviceKey];
        var canceledScan = status.Status == OperationStatus.Cancelling.ToWireString()
            && _cacheReadingOperations.Contains(operationType);
        if (canceledScan) return null;
        // A detection of the other scan type does the other scan's work: the request waits and runs its own
        // scan after it, as the queue does. The request's type is the one a person asked for on the Games
        // page, or the schedule's own when nobody did.
        if (operationType == OperationType.GameDetection
            && Guid.TryParse(status.OperationId, out var runningId)
            && !IsRequestedScanType(_tracker?.GetOperation(runningId)?.DetectionScanType, requestedScanType))
        {
            return null;
        }
        return status;
    }

    // Whether a detection of <paramref name="scanType"/> is the scan a request asked for: the type a person
    // chose on the Games page, or the schedule's own when nobody did.
    private bool IsRequestedScanType(DetectionScanType? scanType, DetectionScanType? requestedScanType)
        => scanType == (requestedScanType ?? GameDetectionService.ResolveScanType(_stateService));

    public async Task<(int TriggeredCount, int AlreadyRunningCount, int SkippedCount, string? SkippedReason)> TriggerAllAsync(
        ScheduleActor? actor = null)
    {
        var triggeredCount = 0;
        var alreadyRunningCount = 0;
        var skippedCount = 0;
        // Every schedule asks the identical question, so every skip in one call has the identical
        // answer. One string says why without telling the reader which keys the answer applies to.
        string? skippedReason = null;

        foreach (var (key, loop) in ScheduleLoops())
        {
            // A schedule whose run is going or queued keeps that one run. The run queue covers a run
            // the loop took that has not registered its operation yet (game detection's startup run
            // waits for setup and log processing first). The tracker covers a run started elsewhere,
            // such as the Storage page Scan button, and one parked in the queue behind another job. A
            // run held for a download is left to the download gate below, which reports it as skipped
            // with its reason; a held detection is held by scan type, so its card counts here as waiting
            // when it is the schedule's own type. Asking before that gate also keeps a running schedule from gaining a
            // held card. A cache scan being canceled stops before it does the work, so its schedule
            // takes a new run that starts once the canceled one ends; any other canceled run still
            // counts as running until it ends. A scheduled prefill with no schedule enabled has
            // nothing to run, and a service turned off in configuration has no loop to run it, so
            // both are left out of every count.
            if (loop is IScheduleEnabledGate enabledGate && !enabledGate.HasAnyServiceEnabled()
                || loop is ScheduledBackgroundService { DisabledInConfiguration: true }) continue;
            var status = await loop.ReadRunStatusAsync();
            var operationType = ScheduleOperationTypes.ByServiceKey[key];
            var canceledScan = status.Run is { Cancelled: true } && _cacheReadingOperations.Contains(operationType);
            var queued = _tracker is not null
                && _tracker.GetWaitingOperations().Any(operation => operation.Type == operationType
                    && operation.Status == OperationStatus.Waiting
                    && operation.Id != status.HeldCard
                    && (operationType != OperationType.GameDetection
                        || IsRequestedScanType(operation.DetectionScanType, requestedScanType: null)));
            if (status.Run is not null && !canceledScan || GetRunThatDoesTheWork(key) is not null || queued)
            {
                alreadyRunningCount++;
                continue;
            }

            // Cache deferral and the run queue's admission decision own separate counts.
            var check = await CheckScheduleRunAsync(key, new RunNotice(loop.EffectiveNotificationMode, RunTrigger.RunAll, actor));
            if (check.Denial is not null)
            {
                skippedCount++;
                skippedReason ??= check.Denial;
                continue;
            }

            // Run All is admitted behind a busy run only when that run is being canceled, so an admitted
            // request is always a new run, shown on its own waiting card the way Run Now shows one.
            var admission = await loop.TryTriggerImmediateRunAsync(check.Notice, (retained, followUp) =>
            {
                if (followUp) AcknowledgeRun(loop, key, operationType, retained);
            });
            if (admission.Admitted) triggeredCount++;
            else alreadyRunningCount++;
        }

        return (triggeredCount, alreadyRunningCount, skippedCount, skippedReason);
    }

    private ServiceScheduleInfo MapScheduledService(ScheduledBackgroundService service)
    {
        // Services whose loop only FIRES the work (e.g. the cache size scan starts a background
        // scan and returns) drop IsCurrentlyExecuting back to false while the real work runs for
        // minutes as a tracked operation - leaving the running dot dark for the whole run, and a
        // notification-silent run completely invisible. Same tracker fallback the configurable
        // branch below already uses: the loop flag OR a live tracked operation of the mapped type.
        var scheduledIsRunning = service.IsCurrentlyExecuting;
        if (!scheduledIsRunning
            && _tracker is not null
            && ScheduleOperationTypes.ByServiceKey.TryGetValue(service.ServiceKey, out var scheduledOpType))
        {
            scheduledIsRunning = _tracker.GetActiveOperations(scheduledOpType).Any();
        }

        var detectsScans = string.Equals(service.ServiceKey, ScanModeServiceKey, StringComparison.OrdinalIgnoreCase);
        if (detectsScans) ArmHybridWeekBroadcast();

        return new ServiceScheduleInfo
        {
            Key = service.ServiceKey,
            IntervalHours = service.EffectiveInterval.TotalHours,
            RunOnStartup = service.RunOnStartup,
            NotificationMode = service.EffectiveNotificationMode,
            NotificationDisplayMode = _stateService.GetServiceNotificationDisplayMode(service.ServiceKey) ?? _stateService.GetGlobalNotificationDisplayMode(),
            NotificationDisplayModeOverridden = _stateService.GetServiceNotificationDisplayMode(service.ServiceKey) is not null,
            // Left null on every other card so its presence alone tells the browser which card
            // renders the scan-mode dropdown, the way PendingFullScan and AwaitingSignIn already do.
            ScanMode = detectsScans
                ? _stateService.GetGameDetectionScanMode()
                : null,
            RunNowScanType = detectsScans
                ? GameDetectionService.ResolveScanType(_stateService)
                : null,
            SupportsNotifications = (bool?)GetPropertyValue(service.GetType(), service, "SupportsNotifications", typeof(bool)) ?? false,
            IsRunning = scheduledIsRunning,
            LastRunUtc = service.LastRunUtc,
            NextRunUtc = service.NextRunUtc,
            // Read from the live service rather than from state so the card shows what the loop is
            // actually sleeping on. The two only differ in the window between a save and the loop
            // waking, and during it the loop is still the truth.
            CustomSchedule = service.ConfiguredCustomSchedule,
        };
    }

    // Hybrid mode's scan type flips when the last full scan turns a week old, and no run starts or ends
    // then, so no broadcast would tell the browser its Run Now scan type changed. One timer, kept for the
    // boundary the settings give now, sends the schedules just after it. Every settings change and every
    // finished full scan ends in a status read, which re-arms it for the new boundary.
    private readonly object _hybridWeekLock = new();
    private Timer? _hybridWeekTimer;
    private DateTime? _hybridWeekBoundary;

    private void ArmHybridWeekBroadcast()
    {
        DateTime? boundary = _stateService.GetGameDetectionScanMode() == GameDetectionScanMode.Hybrid
            && _stateService.GetGameDetectionLastFullScan() is { } lastFullScan
                ? lastFullScan + GameDetectionScanModeExtensions.HybridWeek
                : null;
        // A boundary more than a week away comes from a stored scan time ahead of the clock, and a timer cannot
        // wait as long as that; the first status read after the clock catches up arms it.
        if (boundary - DateTime.UtcNow > GameDetectionScanModeExtensions.HybridWeek) boundary = null;
        lock (_hybridWeekLock)
        {
            if (boundary == _hybridWeekBoundary) return;
            _hybridWeekBoundary = boundary;
            _hybridWeekTimer?.Dispose();
            _hybridWeekTimer = null;
            if (boundary is not { } at || at <= DateTime.UtcNow) return;
            // A second past the boundary, so a timer that fires early still reads the new scan type.
            _hybridWeekTimer = new Timer(_ => NotifySchedulesChanged(), null,
                at - DateTime.UtcNow + TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    private ServiceScheduleInfo MapConfigurableService(ConfigurableScheduledService service)
    {
        var key = GetServiceKey(service);

        // A service like ScheduledPrefillService implements IScheduleEnabledGate because its own
        // ConfiguredInterval is a fixed outer poll cadence (a 1-minute due-check) that re-stamps
        // LastRun/NextRun every tick and never reflects the real per-service schedule. For such a service,
        // derive the outer card's timing from the per-service reality instead of leaking the poll cadence.
        if (service is IScheduleEnabledGate gate)
        {
            // The outer card's timing is derived entirely from per-service reality, so scan the per-service
            // configs ONCE up front and reuse the result in every branch below:
            //   latestLastRun  = MAX per-service GENUINE last-run over ALL services (the honest "Last run").
            //                    This reads the actual-run map, NOT the schedule-basis map: the basis is
            //                    stamped by first-run anchoring and advanced on every skipped attempt, so it
            //                    holds a time before a service has ever truly run (and service.LastRunUtc is
            //                    even worse - the 1-minute poll re-stamps it every no-op tick). Null when no
            //                    service has ever genuinely run -> UI shows "Never run".
            //   soonestNextRun = MIN per-service next-run over ENABLED recurring services, computed from the
            //                    schedule BASIS (reusing ScheduledPrefillRunGates.ComputeNextRunUtc so the
            //                    outer card and the per-service detail agree). Null when nothing enabled is
            //                    recurring.
            var config = _stateService.GetScheduledPrefillConfig();
            DateTime? soonestNextRun = null;
            DateTime? latestLastRun = null;
            // Named schedules keep independent styles; platform entries support older clients and
            // recovered cards without a schedule ID.
            var platformDisplayModes = new Dictionary<string, NotificationDisplayMode>(StringComparer.Ordinal);

            var records = config.GetSchedulesInRunOrder();
            var activeScheduleIds = _tracker?
                .GetActiveOperations(OperationType.ScheduledPrefill)
                .Select(operation => operation.Metadata as ScheduledPrefillServiceRunState)
                .Where(state => state is not null)
                .Select(state => state!.ScheduleId)
                .ToHashSet() ?? [];

            foreach (var platformRecords in records.GroupBy(record => record.ServiceId))
            {
                var displayRecord = platformRecords.FirstOrDefault(record => activeScheduleIds.Contains(record.ScheduleId))
                    ?? platformRecords.FirstOrDefault(record => record.Enabled)
                    ?? platformRecords.First();
                platformDisplayModes[platformRecords.Key.ToString()] =
                    displayRecord.NotificationDisplayMode ?? NotificationDisplayMode.Full;
            }

            foreach (var record in records)
            {
                platformDisplayModes[$"{record.ServiceId}:{record.ScheduleId:D}"] =
                    record.NotificationDisplayMode ?? NotificationDisplayMode.Full;
                var scheduleKey = record.ScheduleId.ToString("N");
                var actualLastRun = _stateService.GetScheduledPrefillServiceLastActualRun(scheduleKey);
                if (actualLastRun is not null && (latestLastRun is null || actualLastRun.Value > latestLastRun.Value))
                {
                    latestLastRun = actualLastRun;
                }

                if (!record.Enabled)
                {
                    continue;
                }

                var scheduleBasis = _stateService.GetScheduledPrefillServiceLastRun(scheduleKey);
                var nextRun = ScheduledPrefillRunGates.ComputeNextRunUtc(
                    record.IntervalHours,
                    scheduleBasis,
                    record.CustomSchedule);
                if (nextRun is not null && (soonestNextRun is null || nextRun.Value < soonestNextRun.Value))
                {
                    soonestNextRun = nextRun;
                }
            }

            // The 1-minute outer poll briefly flips IsCurrentlyExecuting on EVERY tick, including no-op
            // ticks with nothing due, so it is not a trustworthy "a prefill is actually running" signal
            // (an unrelated broadcast landing in that ms window would otherwise flash the card green).
            // Derive the running state from the tracked ScheduledPrefill operation instead - the same
            // source GetRunStatus uses - so only a genuine run lights the dot. Fall back to the base flag
            // when no tracker is wired (unit tests construct the registry without one).
            var isRunning = _tracker is not null
                ? _tracker.GetActiveOperations(OperationType.ScheduledPrefill).Any()
                : service.IsCurrentlyExecuting;

            // Nothing enabled: report paused (interval 0, no next-run) — the same representation the frontend
            // already renders for any interval-0 service (dimmed card, "Disabled" label, disabled Run Now).
            // LastRunUtc is the per-service MAX (null if nothing ever ran), never the poll stamp.
            if (!gate.HasAnyServiceEnabled())
            {
                return new ServiceScheduleInfo
                {
                    Key = key,
                    IntervalHours = 0,
                    RunOnStartup = service.RunOnStartup,
                    NotificationMode = service.EffectiveNotificationMode,
                    NotificationDisplayMode = _stateService.GetServiceNotificationDisplayMode(key) ?? _stateService.GetGlobalNotificationDisplayMode(),
                    NotificationDisplayModeOverridden = _stateService.GetServiceNotificationDisplayMode(key) is not null,
                    PlatformNotificationDisplayModes = platformDisplayModes.Count > 0 ? platformDisplayModes : null,
                    SupportsNotifications = (bool?)GetPropertyValue(service.GetType(), service, "SupportsNotifications", typeof(bool)) ?? false,
                    IsRunning = isRunning,
                    LastRunUtc = latestLastRun,
                    NextRunUtc = null,
                };
            }

            // Enabled, but no enabled service has a recurring next-run (every enabled one is startup-only -1
            // or paused 0, so ComputeNextRunUtc returned null for all). Report IntervalHours = -1, NOT 0:
            // interval 0 both dims the card AND disables Run Now (SchedulesSection.tsx: isDimmed :190,
            // Run Now disabled={isDisabled || isDimmed} :313) — but a service IS enabled here, so the user
            // must still be able to Run Now. -1 is the only value that keeps Run Now enabled without inventing
            // a countdown: CountdownDisplay short-circuits -1 to the "Startup only" label (:48-53) before the
            // countdown block, so a null NextRunUtc never renders a fake "Soon" (a positive interval would,
            // since useCountdownTimer(null) => 0 => "soon"). -1 is exactly truthful for the common
            // all-startup-only case; for a mixed startup-only/paused set it slightly over-states "startup
            // only", but that is the least-wrong of the three renderings the frontend offers (0 kills Run Now,
            // positive fabricates a countdown). NextRunUtc stays null — there is genuinely no scheduled run.
            if (soonestNextRun is null)
            {
                return new ServiceScheduleInfo
                {
                    Key = key,
                    IntervalHours = -1d,
                    RunOnStartup = service.RunOnStartup,
                    NotificationMode = service.EffectiveNotificationMode,
                    NotificationDisplayMode = _stateService.GetServiceNotificationDisplayMode(key) ?? _stateService.GetGlobalNotificationDisplayMode(),
                    NotificationDisplayModeOverridden = _stateService.GetServiceNotificationDisplayMode(key) is not null,
                    PlatformNotificationDisplayModes = platformDisplayModes.Count > 0 ? platformDisplayModes : null,
                    SupportsNotifications = (bool?)GetPropertyValue(service.GetType(), service, "SupportsNotifications", typeof(bool)) ?? false,
                    IsRunning = isRunning,
                    LastRunUtc = latestLastRun,
                    NextRunUtc = null,
                };
            }

            // Enabled with a real recurring next-run: surface the soonest per-service next-run and the most
            // recent per-service last-run instead of the outer poll cadence.
            return new ServiceScheduleInfo
            {
                Key = key,
                IntervalHours = service.ConfiguredInterval.TotalHours,
                RunOnStartup = service.RunOnStartup,
                NotificationMode = service.EffectiveNotificationMode,
                NotificationDisplayMode = _stateService.GetServiceNotificationDisplayMode(key) ?? _stateService.GetGlobalNotificationDisplayMode(),
                NotificationDisplayModeOverridden = _stateService.GetServiceNotificationDisplayMode(key) is not null,
                PlatformNotificationDisplayModes = platformDisplayModes.Count > 0 ? platformDisplayModes : null,
                SupportsNotifications = (bool?)GetPropertyValue(service.GetType(), service, "SupportsNotifications", typeof(bool)) ?? false,
                IsRunning = isRunning,
                LastRunUtc = latestLastRun,
                NextRunUtc = soonestNextRun,
            };
        }

        // A manual REST trigger (the depot "rebuild now" endpoint -> SteamKit2Service.TryStartRebuild)
        // starts work WITHOUT going through the base loop that flips IsCurrentlyExecuting, so also treat
        // the service as running when it has an active tracked operation of its mapped type. This covers
        // both the scheduled crawl and a manual rebuild with one source of truth.
        var configurableIsRunning = service.IsCurrentlyExecuting;
        if (!configurableIsRunning && _tracker is not null && ScheduleOperationTypes.ByServiceKey.TryGetValue(key, out var runningOpType))
        {
            configurableIsRunning = _tracker.GetActiveOperations(runningOpType).Any();
        }

        return new ServiceScheduleInfo
        {
            Key = key,
            IntervalHours = service.ConfiguredInterval.TotalHours,
            RunOnStartup = service.RunOnStartup,
            NotificationMode = service.EffectiveNotificationMode,
            NotificationDisplayMode = _stateService.GetServiceNotificationDisplayMode(key) ?? _stateService.GetGlobalNotificationDisplayMode(),
            NotificationDisplayModeOverridden = _stateService.GetServiceNotificationDisplayMode(key) is not null,
            SupportsNotifications = (bool?)GetPropertyValue(service.GetType(), service, "SupportsNotifications", typeof(bool)) ?? false,
            IsRunning = configurableIsRunning,
            LastRunUtc = service.LastRunUtc,
            NextRunUtc = service.NextRunUtc,
            // Read from the live service rather than from state so the card shows what the loop is
            // actually sleeping on. The two only differ in the window between a save and the loop
            // waking, and during it the loop is still the truth.
            CustomSchedule = service.ConfiguredCustomSchedule,
            // Only the depot mapping service declares this property, and only while it has given up on
            // an incremental scan; every other service reads back null. Same reflection route as
            // SupportsNotifications above, so the registry keeps its distance from the concrete types.
            PendingFullScan = (FullScanRequirement?)GetPropertyValue(service.GetType(), service, "PendingFullScan", typeof(FullScanRequirement)),
            // Only the Xbox mapping service declares this one, and only while its device-code sign-in
            // is waiting for approval. That wait is what put the row into the running state above, so
            // the row needs it to say why Run Now is unavailable. Same reflection route again.
            AwaitingSignIn = (bool?)GetPropertyValue(service.GetType(), service, "AwaitingSignIn", typeof(bool)),
        };
    }

    private static string GetServiceKey(ConfigurableScheduledService service)
    {
        var serviceType = service.GetType();
        return (string?)GetPropertyValue(serviceType, service, "ScheduleServiceKey", typeof(string)) ?? serviceType.Name;
    }

    /// <summary>
    /// Reads a property of type <paramref name="expectedType"/> by name, including protected
    /// declarations (ScheduleServiceKey/ServiceName are public/protected respectively;
    /// SupportsNotifications is protected and only present on leaf types that override it). Returns
    /// null if the property is absent, wrong-typed, or not overridden - callers cast to their
    /// expected nullable type and apply their own default for "absent" (e.g. the base class's own
    /// default value). Returns `object?` rather than a generic `T?` because an unconstrained `T?`
    /// does not reliably resolve to `Nullable<T>` for a value-type `T` (observed: `bool` call sites
    /// got a non-nullable `bool` return, breaking `?? false`) - callers cast explicitly instead.
    /// </summary>
    private static object? GetPropertyValue(Type type, object instance, string propertyName, Type expectedType)
    {
        var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null || property.PropertyType != expectedType)
        {
            return null;
        }
        return property.GetValue(instance);
    }

    private static void ApplyInterval(ConfigurableScheduledService service, TimeSpan interval)
    {
        // UpdateInterval is protected in ConfigurableScheduledService.
        // Services may expose a public wrapper (e.g., UpdateInterval(TimeSpan)).
        // Fall back to reflection on the protected method.
        var serviceType = service.GetType();

        var publicMethod = serviceType.GetMethod("UpdateInterval",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
            new[] { typeof(TimeSpan) });

        if (publicMethod != null)
        {
            publicMethod.Invoke(service, new object[] { interval });
            return;
        }

        var protectedMethod = serviceType.GetMethod("UpdateInterval",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            new[] { typeof(TimeSpan) });

        protectedMethod?.Invoke(service, new object[] { interval });
    }
}
