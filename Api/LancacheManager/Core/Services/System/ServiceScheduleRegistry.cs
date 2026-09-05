using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Services.Scheduling;
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

    // Maps a schedule service key to the operation type the run-status endpoint queries on the tracker.
    // Covers both the pipeline-less maintenance services (each owns its own operation type) and the
    // existing pipelines (eviction/cache-size/detection/depot/epic/xbox/prefill) so a single generic
    // recovery route can rehydrate any card's in-progress bar after a refresh.
    private static readonly Dictionary<string, OperationType> _runStatusOperationTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["logRotation"] = OperationType.LogRotation,
        ["gameImageFetch"] = OperationType.GameImageFetch,
        ["cacheSnapshot"] = OperationType.CacheSnapshot,
        ["operationHistoryCleanup"] = OperationType.OperationHistoryCleanup,
        ["dashboardCacheWarmer"] = OperationType.DashboardCacheWarmer,
        ["cacheReconciliation"] = OperationType.EvictionScan,
        ["cacheSizeScan"] = OperationType.CacheSizeScan,
        ["gameDetection"] = OperationType.GameDetection,
        ["depotMapping"] = OperationType.DepotMapping,
        ["epicMapping"] = OperationType.EpicMapping,
        ["xboxMapping"] = OperationType.XboxMapping,
        ["battleNetMapping"] = OperationType.BattleNetMapping,
        ["riotMapping"] = OperationType.RiotMapping,
        ["scheduledPrefill"] = OperationType.ScheduledPrefill,
    };

    // The operations that walk the cache directory tree, so they are the ones a client download can
    // pull the ground out from under. Every schedule asks the same question through the same code;
    // this is what makes the ANSWER differ, and it is reached through the key-to-OperationType map
    // above rather than by naming schedule keys.
    private static readonly HashSet<OperationType> _cacheReadingOperations =
    [
        OperationType.EvictionScan,
        OperationType.CacheSizeScan,
        OperationType.GameDetection,
    ];

    // Translation key for the card a refused scan puts up. It has to be a key rather than the gate's
    // own English sentence: the three cache scans render their terminal card through
    // i18n.t(event.stageKey), so a sentence there would only appear because i18next echoes an unknown
    // key back, and would turn into an empty message or a fragment of itself the moment a missing-key
    // handler is configured. The gate's sentence still travels, on the error field beside it.
    private const string SkippedWhileDownloadingStageKey = "management.gameDetection.blockedWhileDownloading";

    // The terminal event each cache scan's own card already listens on. A refused run announces
    // itself on the key's existing event rather than on one of its own, so the browser needs no new
    // event name and a skip lands on the card that key would have used had it run.
    private static readonly Dictionary<string, string> _runCompleteEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cacheReconciliation"] = SignalREvents.EvictionScanComplete,
        ["cacheSizeScan"] = SignalREvents.CacheSizeScanComplete,
        ["gameDetection"] = SignalREvents.GameDetectionComplete,
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
    private readonly IUnifiedOperationTracker? _tracker;

    // Optional for the same reason as _tracker below: unit tests construct the registry directly.
    // When it is absent every schedule is allowed to run, which is the behaviour before this gate.
    private readonly CacheScanGate? _cacheScanGate;

    // Schedules that have already announced a skip for the download activity going on right now. A
    // key lands here when its skip is announced, and two things clear it, both of them the same
    // fact arriving by different routes: the tracker parsing the snapshot where downloads stop (see
    // OnDownloadsEnded), and a schedule later asking and being told nothing is downloading (see
    // CheckScheduleRun), which covers the cases that edge cannot. Nothing else re-arms it: not the
    // next cycle, not another schedule being refused, and not the download moving to a different
    // client or game, because the gate reports one plain "something is downloading" either way. So a
    // person who dismisses the notice does not see that schedule again until downloads have stopped
    // and a later one begins. Guarded by locking on the set itself, which is private to this class:
    // schedule loops ask from many threads at once.
    private readonly HashSet<string> _announcedSkips = new(StringComparer.OrdinalIgnoreCase);

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
    public ServiceScheduleRegistry(IEnumerable<IHostedService> hostedServices, IStateService stateService, ISignalRNotificationService notifications, IUnifiedOperationTracker? tracker = null, IActivityRegistry? activityRegistry = null, CacheScanGate? cacheScanGate = null)
    {
        _stateService = stateService;
        _notifications = notifications;
        _tracker = tracker;
        _activityRegistry = activityRegistry;
        _cacheScanGate = cacheScanGate;
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
        ConfigurableScheduledService.ServiceExecutionStateChanged += OnServiceExecutionStateChangedAsync;

        // Same one-time static wiring as the two events above. The registry answers because it is the
        // only place that knows which keys are user-configurable schedules; every other subclass of
        // the scheduled bases, RustSpeedTrackerService included, is never asked and always runs.
        // A registry built without the download gate has no answer to give, so it leaves the hook
        // alone rather than replacing a working one with a function that always says yes.
        if (_cacheScanGate is not null)
        {
            ScheduledServiceBase.ScheduleRunGate = OnScheduleRunGate;
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

    private void OnTrackedOperationTerminal(OperationInfo operation)
    {
        if (_runStatusOperationTypes.ContainsValue(operation.Type))
        {
            NotifySchedulesChanged();
        }

        // A run declined before it started registers itself only to be reported and carries no
        // terminal broadcast of its own. This registry is already listening here and is the one
        // place that knows which card the schedule owns, so the refusal is announced from here
        // rather than by giving the queue a dependency on schedules.
        if (operation.Status == OperationStatus.Skipped
            && ReadDeclinedBeforeStart(operation.Metadata)
            && TryFindScheduleKey(operation.Type, out var declinedKey))
        {
            // Passed through as it stands, empty or not. The card reads the reason as
            // error-or-stage-key, and an empty string is neither null nor undefined there, so
            // substituting one for a missing message would render a blank card instead of falling
            // back to the stage key.
            _ = EmitSkippedRunAsync(declinedKey, operation.Id, operation.Message);
        }
    }

    /// <summary>
    /// Downloads have stopped, so every schedule may announce its next skip. The polled clear in
    /// <see cref="CheckScheduleRun"/> stays as the backstop for the cases this edge deliberately
    /// does not cover: a process that starts up with a download already in flight, and a tracker
    /// that dies, which stops answering rather than reporting that anything finished.
    /// </summary>
    private void OnDownloadsEnded()
    {
        // Raised on the tracker's stdout thread, so this has to be safe off the loop threads. It
        // takes the same lock every other reader of the set takes and does nothing else.
        lock (_announcedSkips)
        {
            _announcedSkips.Clear();
        }
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
        foreach (var key in _runCompleteEvents.Keys)
        {
            if (_runStatusOperationTypes.TryGetValue(key, out var mapped) && mapped == operationType)
            {
                serviceKey = key;
                return true;
            }
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
    private string? CheckScheduleRun(string serviceKey)
    {
        if (_cacheScanGate is null || !_runStatusOperationTypes.TryGetValue(serviceKey, out var operationType))
        {
            return null;
        }

        var downloadDenial = _cacheScanGate.CheckDownloadInProgress();
        if (downloadDenial is null)
        {
            // Nothing is downloading, so every schedule may announce its next skip. This is the
            // polled half of what re-arms the announcements described on _announcedSkips;
            // OnDownloadsEnded is the other, and clears them off the edge itself.
            lock (_announcedSkips)
            {
                _announcedSkips.Clear();
            }

            return null;
        }

        // A job that never walks the cache tree has no reason to wait for a download to finish, so it
        // gets the same question and a different answer.
        return _cacheReadingOperations.Contains(operationType) ? downloadDenial : null;
    }

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
            || !_runStatusOperationTypes.TryGetValue(serviceKey, out var operationType)
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
    /// routes below ask <see cref="CheckScheduleRun"/> directly instead: they return the reason on
    /// the response the caller is waiting for, so a second record would double-report one click.
    /// </summary>
    private string? OnScheduleRunGate(string announcedKey, RunTrigger trigger)
    {
        // The two loop bases call themselves different things. ScheduledBackgroundService announces
        // its ServiceKey, which is already the schedule key; ConfigurableScheduledService announces
        // its ServiceName, which is not, so it is translated through the same index the
        // execution-state guard uses. Without this every configurable schedule missed the lookup and
        // was silently never asked.
        var serviceKey = _configurableServiceNames.TryGetValue(announcedKey, out var scheduleKey)
            ? scheduleKey
            : announcedKey;

        var denial = CheckScheduleRun(serviceKey);
        if (denial is null)
        {
            return null;
        }

        // A run someone asked for is always reported. By the time the loop asks, it has already taken
        // the pending Run Now flag, so the click is spent: staying quiet here because this schedule
        // already announced a skip earlier in the same download would leave the person with a
        // response that said the run started and nothing at all afterwards.
        if (trigger == RunTrigger.Manual || ClaimSkipAnnouncement(serviceKey))
        {
            RecordSkippedRun(serviceKey, denial);
        }

        return denial;
    }

    /// <summary>
    /// True the first time <paramref name="serviceKey"/> is refused during the download activity
    /// going on right now, false for every later refusal of the same schedule. See
    /// <see cref="_announcedSkips"/> for what re-arms it, which is downloads stopping, reported
    /// either by the tracker's own edge or by the next schedule to ask. The manual routes never come
    /// through here: a person pressing a button is waiting on an answer and gets one every time.
    /// </summary>
    private bool ClaimSkipAnnouncement(string serviceKey)
    {
        lock (_announcedSkips)
        {
            return _announcedSkips.Add(serviceKey);
        }
    }

    private void RecordSkippedRun(string serviceKey, string reason)
    {
        if (_tracker is null || !_runStatusOperationTypes.TryGetValue(serviceKey, out var operationType))
        {
            return;
        }

        // Assigned by the call below and read only when the operation completes, which is the line
        // after that. The tracker adopts the token source and disposes it there too.
        Guid operationId = default;
        operationId = _tracker.RegisterOperation(
            operationType,
            serviceKey,
            new CancellationTokenSource(),
            onTerminalEmit: (OperationTerminalInfo _) => EmitSkippedRunAsync(serviceKey, operationId, reason));
        _tracker.CompleteOperation(operationId, success: true, error: reason, skipped: true);
    }

    /// <summary>
    /// Announces a refused run on the schedule's own terminal event: a translation key for the card
    /// to render, and the gate's own sentence beside it for anything that reports the raw reason.
    /// </summary>
    private async Task EmitSkippedRunAsync(string serviceKey, Guid operationId, string? reason)
    {
        if (!_runCompleteEvents.TryGetValue(serviceKey, out var completeEvent))
        {
            return;
        }

        var terminal = new ScheduledRunCompleteEvent(
            serviceKey,
            operationId,
            Success: true,
            StageKey: SkippedWhileDownloadingStageKey,
            // A run that was refused did nothing, so there is no progress to claim.
            PercentComplete: 0,
            Error: reason,
            Context: null,
            // Always shown. A schedule set to Silent is asking for its routine runs to stay out of
            // the way, and a run that was refused is not one of those.
            ShowNotification: true,
            Cancelled: false,
            Status: OperationStatus.Skipped);

        await _notifications.NotifyAllAsync(completeEvent, terminal);
    }

    public Task<(ScheduleRunStatus Status, string? SkippedReason)> TriggerRunAsync(string serviceKey)
    {
        // Asked before either TriggerImmediateRun below, so a run that would be declined never arms
        // the pending-run flag and the loop is not woken only to turn around.
        var runDenial = CheckScheduleRun(serviceKey);
        if (runDenial is not null)
        {
            return Task.FromResult<(ScheduleRunStatus, string?)>(
                (new ScheduleRunStatus { IsRunning = false, ShowNotification = true }, runDenial));
        }

        // Read the run state BEFORE arming the trigger. TriggerImmediateRun on a service that is
        // already running only sets ScheduledServiceBase's single pending-run flag for one follow-up
        // run - it cannot start a second one - so the state observed here is exactly the run this
        // call collides with, not a state the trigger call itself could have changed.
        var statusBeforeTrigger = GetRunStatus(serviceKey) ?? new ScheduleRunStatus { IsRunning = false, ShowNotification = true };

        if (_scheduledServices.TryGetValue(serviceKey, out var scheduled))
        {
            scheduled.TriggerImmediateRun();
        }
        else if (_configurableServices.TryGetValue(serviceKey, out var configurable))
        {
            configurable.TriggerImmediateRun();
        }

        return Task.FromResult<(ScheduleRunStatus, string?)>((statusBeforeTrigger, null));
    }

    public ScheduleRunStatus? GetRunStatus(string serviceKey)
    {
        if (!_runStatusOperationTypes.TryGetValue(serviceKey, out var operationType))
        {
            // Unknown key: the caller (controller) turns this into a 404.
            return null;
        }

        // Scheduled prefill registers one tracked operation per due platform beside its run-level
        // one, so several operations of the same type are active at once during a run. This card
        // reports the RUN, so the per-platform operations are filtered out here: a bare
        // FirstOrDefault would otherwise hand the card whichever operation the tracker happened to
        // enumerate first, and its id is what the card's Cancel targets. [28]
        var active = _tracker?
            .GetActiveOperations(operationType)
            .FirstOrDefault(op => op.Metadata is not ScheduledPrefillServiceRunState);
        if (active == null)
        {
            // An idle service is visible by default: recovery must stale-complete a persisted running
            // card on reconnect after a missed terminal, not delete it. Only an ACTIVE silent run
            // (ShowNotification=false below) is a legitimate skip.
            return new ScheduleRunStatus { IsRunning = false, ShowNotification = true };
        }

        // The run reporter carries the stage key as the operation's Message and persists the run's
        // immutable display flag into the operation metadata under "showNotification". A run without
        // that key (an operation registered by a non-reporter caller) is treated as visible so
        // recovery never drops a legitimately visible card.
        return new ScheduleRunStatus
        {
            IsRunning = true,
            OperationId = active.Id.ToString(),
            PercentComplete = active.PercentComplete,
            StageKey = string.IsNullOrEmpty(active.Message) ? null : active.Message,
            Context = ReadContext(active.Metadata),
            ShowNotification = ReadShowNotification(active.Metadata),
        };
    }

    private static bool ReadShowNotification(object? metadata)
    {
        var value = metadata switch
        {
            IReadOnlyDictionary<string, object?> readOnly when readOnly.TryGetValue("showNotification", out var v) => v,
            IDictionary<string, object> mutable when mutable.TryGetValue("showNotification", out var v) => v,
            _ => null,
        };

        return value is not bool show || show;
    }

    // The reporter mirrors each run's latest interpolation context into the operation metadata under
    // "context" so a mid-run page refresh can rehydrate the card with its {{processed}}/{{total}}
    // values instead of rendering a bare stage key.
    private static IReadOnlyDictionary<string, object?>? ReadContext(object? metadata)
    {
        var value = metadata switch
        {
            IReadOnlyDictionary<string, object?> readOnly when readOnly.TryGetValue("context", out var v) => v,
            IDictionary<string, object> mutable when mutable.TryGetValue("context", out var v) => v,
            _ => null,
        };

        return value as IReadOnlyDictionary<string, object?>;
    }

    public Task<(int TriggeredCount, int AlreadyRunningCount, int SkippedCount, string? SkippedReason)> TriggerAllAsync()
    {
        var triggeredCount = 0;
        var alreadyRunningCount = 0;
        var skippedCount = 0;
        // Every schedule asks the identical question, so every skip in one call has the identical
        // answer. One string says why without telling the reader which keys the answer applies to.
        string? skippedReason = null;

        foreach (var (key, service) in _scheduledServices)
        {
            // Same before-trigger read as the single-service TriggerRunAsync. A refused service is
            // counted and dropped here rather than triggered, which is what the skipped count
            // reports. For everything else the trigger call runs whether or not the service is
            // mid-run: one that is keeps its own single pending-run flag, so this arms one
            // follow-up run instead of starting a second concurrent one, and the already-running
            // count reports what was running when this fan-out reached it.
            var scheduledDenial = CheckScheduleRun(key);
            if (scheduledDenial is not null)
            {
                skippedCount++;
                skippedReason ??= scheduledDenial;
                continue;
            }

            var alreadyRunning = GetRunStatus(key)?.IsRunning == true;
            service.TriggerImmediateRun();
            if (alreadyRunning)
            {
                alreadyRunningCount++;
            }
            else
            {
                triggeredCount++;
            }
        }

        foreach (var (key, service) in _configurableServices)
        {
            var configurableDenial = CheckScheduleRun(key);
            if (configurableDenial is not null)
            {
                skippedCount++;
                skippedReason ??= configurableDenial;
                continue;
            }

            var alreadyRunning = GetRunStatus(key)?.IsRunning == true;
            service.TriggerImmediateRun();
            if (alreadyRunning)
            {
                alreadyRunningCount++;
            }
            else
            {
                triggeredCount++;
            }
        }

        return Task.FromResult((triggeredCount, alreadyRunningCount, skippedCount, skippedReason));
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
            && _runStatusOperationTypes.TryGetValue(service.ServiceKey, out var scheduledOpType))
        {
            scheduledIsRunning = _tracker.GetActiveOperations(scheduledOpType).Any();
        }

        return new ServiceScheduleInfo
        {
            Key = service.ServiceKey,
            IntervalHours = service.EffectiveInterval.TotalHours,
            RunOnStartup = service.RunOnStartup,
            NotificationMode = service.EffectiveNotificationMode,
            NotificationDisplayMode = _stateService.GetServiceNotificationDisplayMode(service.ServiceKey) ?? service.DefaultNotificationDisplayMode,
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
            // Only the platforms that have chosen a style. The rest are left out so the notification
            // bar falls back to this schedule's own display mode rather than being told "full" by a
            // value nobody set.
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
                    NotificationDisplayMode = _stateService.GetServiceNotificationDisplayMode(key) ?? service.DefaultNotificationDisplayMode,
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
                    NotificationDisplayMode = _stateService.GetServiceNotificationDisplayMode(key) ?? service.DefaultNotificationDisplayMode,
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
                NotificationDisplayMode = _stateService.GetServiceNotificationDisplayMode(key) ?? service.DefaultNotificationDisplayMode,
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
        if (!configurableIsRunning && _tracker is not null && _runStatusOperationTypes.TryGetValue(key, out var runningOpType))
        {
            configurableIsRunning = _tracker.GetActiveOperations(runningOpType).Any();
        }

        return new ServiceScheduleInfo
        {
            Key = key,
            IntervalHours = service.ConfiguredInterval.TotalHours,
            RunOnStartup = service.RunOnStartup,
            NotificationMode = service.EffectiveNotificationMode,
            NotificationDisplayMode = _stateService.GetServiceNotificationDisplayMode(key) ?? service.DefaultNotificationDisplayMode,
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
