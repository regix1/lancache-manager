using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services.Base;

/// <summary>
/// Base class for background services that run on a runtime-configurable schedule.
/// Unlike ScheduledBackgroundService (which has a fixed interval), this allows the interval
/// to be changed at runtime via API calls. When the interval changes, the current sleep
/// is interrupted so the new interval takes effect immediately.
///
/// Used by services like SteamKit2Service and EpicMappingService that expose
/// REST endpoints to change their scheduling interval.
/// </summary>
public abstract class ConfigurableScheduledService : ScheduledServiceBase
{
    /// <summary>
    /// Fired whenever a configurable service's execution state changes: once when a run starts (after
    /// IsCurrentlyExecuting flips true, gated by <see cref="BroadcastRunStart"/>) and once when it ends
    /// (after IsCurrentlyExecuting flips false). Subscribers receive the service name. Firing on both
    /// edges is what lets the Schedules UI light the status dot live during a run and clear it the
    /// moment the run finishes; the end broadcast must come AFTER IsCurrentlyExecuting is cleared so
    /// GetAll() reports the run as finished.
    /// </summary>
    public static event Action<string>? ServiceExecutionStateChanged;

    /// <summary>
    /// Raises <see cref="ServiceExecutionStateChanged"/> for this service. Exposed so a service that
    /// opts out of the automatic per-tick start broadcast (<see cref="BroadcastRunStart"/> = false) can
    /// light the Schedules status dot itself the moment a genuine run begins - a derived type cannot
    /// invoke a base-declared event directly. Safe to call from inside ExecuteWorkAsync: the base loop
    /// has already set IsCurrentlyExecuting = true, so the resulting GetAll() snapshot reports running.
    /// </summary>
    protected void RaiseExecutionStateChanged() => ServiceExecutionStateChanged?.Invoke(ServiceName);

    private readonly TimeSpan _defaultInterval;
    private TimeSpan _interval;
    private CancellationTokenSource? _intervalChangedCts;
    private readonly object _intervalLock = new();
    private volatile bool _intervalJustChanged;

    /// <summary>
    /// Current scheduling interval. When the interval is zero, the service is considered disabled
    /// and ExecuteScheduledWorkAsync will not be called.
    /// Thread-safe: reads/writes are protected by a lock.
    /// </summary>
    public TimeSpan ConfiguredInterval
    {
        get { lock (_intervalLock) return _interval; }
    }

    /// <summary>
    /// Whether the loop broadcasts a run-START state change (which lights the Schedules status dot).
    /// Defaults to true. A poll-style service whose ExecuteWorkAsync fires on a short fixed cadence and
    /// no-ops most ticks (ScheduledPrefillService's 1-minute due-check) overrides this to false so the
    /// dot does not flash green every tick; it still emits the run-END broadcast, which keeps its
    /// Last/Next-run readouts fresh and reports the idle state correctly.
    /// </summary>
    protected virtual bool BroadcastRunStart => true;

    /// <summary>
    /// Services on this base run work on their very first loop iteration (i.e. at app startup):
    /// they map and refresh catalogs the rest of the app reads, so waiting a full interval after
    /// startup would leave that data stale for hours. Subclasses override this to express their
    /// intended default.
    /// </summary>
    public override bool DefaultRunOnStartup => true;

    protected ConfigurableScheduledService(ILogger logger, TimeSpan initialInterval)
        : base(logger)
    {
        _defaultInterval = initialInterval;
        _interval = initialInterval;
    }

    /// <inheritdoc />
    protected override void ApplyLoadedInterval(TimeSpan interval) => UpdateInterval(interval);

    /// <summary>
    /// Updates the scheduling interval at runtime. Wakes the loop so the new interval
    /// takes effect immediately rather than waiting for the old interval to expire.
    /// </summary>
    protected void UpdateInterval(TimeSpan newInterval)
    {
        lock (_intervalLock)
        {
            _interval = newInterval;
            _intervalJustChanged = true;

            // Cancel the current sleep to wake the loop - it will skip work and re-sleep with new interval
            CancelIntervalDelay();
        }

        _logger.LogInformation("{ServiceName} interval updated to {Hours:F1} hour(s)",
            ServiceName, newInterval.TotalHours);
    }

    /// <inheritdoc />
    protected override void WakeForScheduleChange()
    {
        lock (_intervalLock)
        {
            // Reuses the flag an interval change already owns: both mean "the sleep you are in was
            // computed from a value that has since changed", and the loop's response is the same.
            _intervalJustChanged = true;

            CancelIntervalDelay();
        }
    }

    /// <summary>
    /// Cancels the sleep this loop is currently in. Taken under the interval lock so an interval
    /// change and the wake it triggers are seen together; the lock is reentrant, so callers that
    /// already hold it stay atomic.
    /// </summary>
    protected override void CancelIntervalDelay()
    {
        lock (_intervalLock)
        {
            try
            {
                _intervalChangedCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, will be recreated on next loop iteration
            }
        }
    }

    /// <summary>
    /// Resets the scheduling interval to the constructor's initial value.
    /// Wakes the loop so the default interval takes effect immediately.
    /// </summary>
    public void ResetInterval()
    {
        UpdateInterval(_defaultInterval);
        _logger.LogInformation("{ServiceName} interval reset to default ({Hours:F1} hour(s))",
            ServiceName, _defaultInterval.TotalHours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (StartupDelay > TimeSpan.Zero)
        {
            _logger.LogDebug("{ServiceName} waiting {Delay} before starting", ServiceName, StartupDelay);
            await SafeDelayAsync(StartupDelay, stoppingToken);
        }

        _logger.LogInformation("{ServiceName} scheduling loop started", ServiceName);

        // Discard any "_intervalJustChanged" flag that was set during construction or
        // InitializeAsync - e.g. LoadStateOverrides → UpdateInterval sets that flag to
        // wake a sleeping loop, but there's no loop yet, so the flag is meaningless here
        // and must not leak into the first iteration (it would eat the skip-first-execution
        // check and delay the first real work run by an extra full interval).
        _intervalJustChanged = false;

        // If RunOnStartup is false, skip the very first work execution and go straight
        // to the sleep - work will only run after the first interval has elapsed (or
        // when TriggerImmediateRun() is called manually).
        bool skipFirstExecution = !RunOnStartup;

        // When RunOnStartup is true, the very first ExecuteWorkAsync IS the startup pass; mark it so
        // CurrentRunTrigger reports Startup for it (unless a manual trigger claims that first run).
        // Cleared after the first real execution so every later run is Scheduled or Manual.
        bool startupRunPending = RunOnStartup;

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = ConfiguredInterval;
            var schedule = ConfiguredCustomSchedule;

            var workIsDue = IsWorkDue(schedule, interval);

            // A pending manual run must always be honored this iteration, even if it lands
            // alongside an interval change or the skip-first-execution pass, and even while the
            // schedule itself is paused (interval <= 0) - Run Now overrides all of those the same
            // way TriggerImmediateRun's old due-check bypass used to. Reading it before the branch
            // (rather than only inside the interval>0 branch) is what makes that possible; checking
            // the other conditions first and only reading _pendingManualRun in the innermost branch
            // silently drops a same-tick Run Now AND leaves the flag stale to misattribute a LATER
            // genuinely scheduled tick as Manual.
            var manualPending = ConsumePendingManualRun();

            // Skip work if woken by an interval change with no manual run pending - just re-sleep
            // with the new interval.
            if (_intervalJustChanged && !manualPending)
            {
                _intervalJustChanged = false;
            }
            else if (skipFirstExecution && !manualPending)
            {
                // Honor user's "do not run on startup" preference for this very first iteration only
                skipFirstExecution = false;
                _logger.LogInformation("{ServiceName} skipping startup run (RunOnStartup is false)", ServiceName);
            }
            else if (manualPending || workIsDue)
            {
                _intervalJustChanged = false;
                skipFirstExecution = false;
                var runFailed = false;
                var shuttingDown = false;
                try
                {
                    IsCurrentlyExecuting = true;
                    CurrentRunTrigger = manualPending
                        ? RunTrigger.Manual
                        : startupRunPending ? RunTrigger.Startup : RunTrigger.Scheduled;
                    // Broadcast the start so the Schedules status dot lights up for the whole run.
                    // Poll-style services (see BroadcastRunStart) opt out to avoid a per-tick flash and
                    // raise the start themselves only when a real run begins.
                    if (BroadcastRunStart)
                    {
                        ServiceExecutionStateChanged?.Invoke(ServiceName);
                    }
                    await ExecuteWorkAsync(stoppingToken);
                    // Advance NextRunUtc now so the run-END broadcast in the finally carries the fresh
                    // next-run instead of the just-elapsed one. The bottom-of-loop sleep re-sets this
                    // authoritatively; this only keeps the END snapshot from shipping a stale countdown.
                    // (Ignored by services like ScheduledPrefill whose card derives timing elsewhere.)
                    NextRunUtc = ComputeNextRun(ConfiguredCustomSchedule, ConfiguredInterval);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Shutdown - end the loop cleanly. A non-shutdown OCE (e.g. an inner
                    // per-iteration timeout) falls through to the Exception handler below
                    // instead of silently ending the service loop.
                    shuttingDown = true;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{ServiceName} error in scheduled work", ServiceName);
                    runFailed = true;
                    // The next attempt is the retry below, not the elapsed schedule - point the
                    // countdown in the run-END broadcast at the retry deadline.
                    NextRunUtc = DateTime.UtcNow + ErrorRetryDelay;
                }
                finally
                {
                    // A run that threw still ran, so it stamps here rather than after ExecuteWorkAsync:
                    // the Schedules page reads this for "Last run", and the frontend also clears its
                    // optimistic Run Now flag when this value moves. Stamping only on success left a
                    // failed run's end-broadcast carrying an unchanged time, which held that button
                    // disabled until a safety timeout expired. Shutdown is excluded because the work
                    // never reached a terminal state - the service is stopping, not finishing.
                    if (!shuttingDown)
                    {
                        LastRunUtc = DateTime.UtcNow;
                    }
                    IsCurrentlyExecuting = false;
                    // Broadcast the end AFTER clearing the flag so GetAll() reports the run finished and
                    // the dot clears - including on the failed-run path.
                    ServiceExecutionStateChanged?.Invoke(ServiceName);
                }

                // Back off AFTER the finally above has cleared the flag and broadcast the end, so a
                // failed run does not sit falsely "running" (green dot) for the whole retry delay.
                if (runFailed)
                {
                    await SafeDelayAsync(ErrorRetryDelay, stoppingToken);
                    continue;
                }
            }

            // Only the very first loop iteration can legitimately be "the startup run" - clear
            // this unconditionally here (not only inside the interval>0 branch above) so a service
            // that starts paused (interval=0) doesn't carry a stale Startup attribution forward to
            // whatever iteration eventually does its first real work, possibly days later.
            startupRunPending = false;

            // A Run Now that arrived while ExecuteWorkAsync was running cancelled the interval CTS
            // that belonged to the already-finished prior sleep, so it cannot interrupt the sleep we
            // are about to enter below. Detect it here and loop straight into another run instead of
            // sleeping - otherwise a positive-interval service defers it to the next natural wake
            // (mislabelled Manual) and a paused service (interval <= 0) sleeps forever, dropping the
            // accepted run entirely. The flag is consumed exactly once at the loop top, which re-reads
            // it and tags the follow-up run Manual; peek without consuming here.
            if (HasPendingManualRun())
            {
                continue;
            }

            // Sleep until the next run (or indefinitely if disabled)
            // Use a linked CTS so UpdateInterval() can wake us up
            interval = ConfiguredInterval;
            schedule = ConfiguredCustomSchedule;
            NextRunUtc = ComputeNextRun(schedule, interval);

            // A custom schedule names an absolute instant, so sleep until that instant rather than for
            // a duration measured from now. An interval restarts its countdown at process start, which
            // shifts every later run by however long the app was down; a schedule lands on the same
            // wall-clock time whether or not a restart happened in between.
            TimeSpan sleepDuration;
            if (schedule is not null && NextRunUtc is not null)
            {
                sleepDuration = TimeUntil(NextRunUtc.Value);
            }
            else
            {
                // A schedule with no next run at all cannot be waited for. Fall back to the ordinary
                // interval sleep so the loop idles instead of recomputing the same null in a tight
                // loop, and it still wakes the moment the schedule or interval is changed.
                if (schedule is not null)
                {
                    WarnScheduleNeverFires(schedule);
                }

                sleepDuration = interval > TimeSpan.Zero ? interval : Timeout.InfiniteTimeSpan;
            }

            CancellationTokenSource? linkedCts = null;
            try
            {
                lock (_intervalLock)
                {
                    _intervalChangedCts?.Dispose();
                    _intervalChangedCts = new CancellationTokenSource();
                }

                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken, _intervalChangedCts!.Token);

                await Task.Delay(sleepDuration, linkedCts.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Interval was changed - loop back to check the new interval
                _logger.LogDebug("{ServiceName} sleep interrupted by interval change", ServiceName);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        _logger.LogInformation("{ServiceName} scheduling loop stopped", ServiceName);
    }

    /// <summary>
    /// The main work to execute on each scheduled interval.
    /// Only called when the configured interval is greater than zero (service is enabled).
    /// </summary>
    protected abstract Task ExecuteWorkAsync(CancellationToken stoppingToken);

    /// <summary>
    /// Override to run initialization before the scheduling loop starts.
    /// Called from StartAsync before base.StartAsync kicks off the loop.
    /// </summary>
    protected virtual Task InitializeAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    /// <summary>
    /// Override to run cleanup when the service is stopping.
    /// Called from StopAsync after base.StopAsync cancels the loop.
    /// </summary>
    protected virtual Task CleanupAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await CleanupAsync(cancellationToken);
    }

    public override void Dispose()
    {
        lock (_intervalLock)
        {
            _intervalChangedCts?.Dispose();
            _intervalChangedCts = null;
        }
        base.Dispose();
    }
}
