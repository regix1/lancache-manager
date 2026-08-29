using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services.Base;

/// <summary>
/// Base class for background services that run on a schedule.
/// Provides common functionality for startup delay, configuration checking,
/// error handling, and interval-based execution.
/// Supports runtime-configurable intervals via SetInterval() and TriggerImmediateRun().
/// </summary>
public abstract class ScheduledBackgroundService : ScheduledServiceBase
{
    /// <summary>
    /// Fired whenever a scheduled service's execution state changes: once when a run starts (after
    /// IsCurrentlyExecuting flips true) and once when it ends (after IsCurrentlyExecuting flips false),
    /// for startup, interval and manual runs alike. Subscribers receive the ServiceKey. Firing on both
    /// edges is what lets the Schedules UI light the status dot live while a run is in progress and
    /// clear it the moment the run finishes - the end broadcast must come AFTER IsCurrentlyExecuting is
    /// cleared so GetAll() reports the run as finished.
    /// </summary>
    public static event Action<string>? ServiceExecutionStateChanged;
    protected readonly IConfiguration _configuration;

    // Runtime interval override state
    private TimeSpan? _intervalOverride;

    // Schedule metadata - override in subclasses to register as user-configurable
    public virtual string ServiceKey => GetType().Name;

    /// <summary>
    /// The effective interval used by the loop: override if set, otherwise the abstract default.
    /// </summary>
    public TimeSpan EffectiveInterval
    {
        get
        {
            lock (IntervalLock)
            {
                return _intervalOverride ?? Interval;
            }
        }
    }

    /// <summary>
    /// Default time between work executions. Return TimeSpan.Zero to run continuously.
    /// Use EffectiveInterval in the loop - this is the hardcoded default only.
    /// </summary>
    protected abstract TimeSpan Interval { get; }

    /// <summary>
    /// Configuration key to check if service is enabled.
    /// Return null if service is always enabled.
    /// </summary>
    protected virtual string? EnabledConfigKey => null;

    /// <summary>
    /// Whether service is enabled by default if config key not found.
    /// </summary>
    protected virtual bool EnabledByDefault => true;

    protected ScheduledBackgroundService(ILogger logger, IConfiguration configuration)
        : base(logger)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Override the scheduling interval at runtime. Interrupts any current sleep
    /// so the new interval takes effect immediately on the next loop.
    /// </summary>
    public void SetInterval(TimeSpan newInterval)
    {
        lock (IntervalLock)
        {
            _intervalOverride = newInterval;

            // Wake the loop under the same lock hold (it is reentrant), so the change and the wake
            // it triggers are seen together.
            WakeForScheduleChange();
        }

        _logger.LogDebug("{ServiceName} interval changed to {Interval}", ServiceName, newInterval);
    }

    /// <summary>
    /// Clear the runtime interval override, reverting to the hardcoded default.
    /// </summary>
    public void ResetInterval()
    {
        lock (IntervalLock)
        {
            _intervalOverride = null;

            WakeForScheduleChange();
        }

        _logger.LogDebug("{ServiceName} interval reset to default ({Interval})", ServiceName, Interval);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check if enabled
        if (!IsEnabled())
        {
            _logger.LogInformation("{ServiceName} is disabled", ServiceName);
            return;
        }

        // Startup delay
        if (StartupDelay > TimeSpan.Zero)
        {
            _logger.LogDebug("{ServiceName} waiting {Delay} before starting",
                ServiceName, StartupDelay);
            await Task.Delay(StartupDelay, stoppingToken);
        }

        _logger.LogInformation("{ServiceName} started", ServiceName);

        // Optional: Run once at startup
        if (RunOnStartup)
        {
            try
            {
                IsCurrentlyExecuting = true;
                // Manual takes priority over Startup (same ternary as ConfigurableScheduledService):
                // a Run Now landing around startup must be consumed here, or the stale flag would
                // misattribute a later scheduled tick as Manual.
                CurrentRunTrigger = ConsumePendingManualRun()
                    ? RunTrigger.Manual
                    : RunTrigger.Startup;
                // Broadcast the start so the Schedules status dot lights up for the whole run.
                ServiceExecutionStateChanged?.Invoke(ServiceKey);
                await OnStartupAsync(stoppingToken);
                LastRunUtc = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{ServiceName} startup execution failed", ServiceName);
            }
            finally
            {
                IsCurrentlyExecuting = false;
                // Whether startup succeeded or failed, the next thing is the main loop's skip-first
                // sleep, so set the countdown to that before the END broadcast rather than shipping a
                // null "Soon". The skip-first sleep re-sets this authoritatively.
                NextRunUtc = ComputeNextRun(ConfiguredCustomSchedule, EffectiveInterval);
                // Broadcast the end AFTER clearing the flag so GetAll() reports the run finished and the
                // dot clears - including on the failure path above.
                ServiceExecutionStateChanged?.Invoke(ServiceKey);
            }
        }

        // Discard any "IntervalJustChanged" flag that was set during construction or
        // InitializeAsync - e.g. LoadStateOverrides → SetInterval sets that flag to wake
        // a sleeping loop, but there's no loop yet, so the flag is meaningless here and
        // must not leak into the first iteration (it would cause the first real work run
        // to be delayed by an extra full interval).
        IntervalJustChanged = false;

        // Main execution loop.
        // Always sleep one interval before the first ExecuteWorkAsync - this honors both:
        //   1. RunOnStartup=true: OnStartupAsync already ran above, so we skip back-to-back work
        //   2. RunOnStartup=false: user explicitly opted out of startup runs, so ExecuteWorkAsync
        //      must NOT fire on the first iteration either (otherwise "disabling startup" is a lie)
        // The first ExecuteWorkAsync only runs after the interval has elapsed (or via Run Now).
        bool skipFirstExecution = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            // A pending manual run must always be honored this iteration, even if an interval
            // change ALSO woke the loop (both call CancelAndRecreateDelay, so either or both can
            // be true on the same wake) AND even during the skip-first-execution pass. Reading it
            // before the branches, rather than inside the "else" below, is what makes that possible
            // - checking IntervalJustChanged/skipFirstExecution first and only reading
            // _pendingManualRun in the other branch silently drops a same-tick Run Now (no work
            // happens) AND leaves the flag stale to misattribute a LATER genuinely scheduled tick as
            // Manual. Mirrors ConfigurableScheduledService's ordering.
            var manualPending = ConsumePendingManualRun();

            var schedule = ConfiguredCustomSchedule;
            var interval = EffectiveInterval;

            if (skipFirstExecution && !manualPending)
            {
                skipFirstExecution = false;
                NextRunUtc = ComputeNextRun(schedule, interval);

                // A custom schedule names an absolute instant, so wait until that instant rather than
                // for a duration measured from now. An interval restarts its countdown at process
                // start, which shifts every later run by however long the app was down; a schedule
                // lands on the same wall-clock time whether or not a restart happened in between.
                if (schedule is not null && NextRunUtc is not null)
                {
                    await InterruptibleDelayAsync(TimeUntil(NextRunUtc.Value), stoppingToken);
                    continue;
                }

                // A schedule with no next run at all cannot be waited for, so it falls back to the
                // ordinary interval sleep below and re-checks on every wake.
                if (schedule is not null)
                {
                    WarnScheduleNeverFires(schedule);
                }

                if (interval > TimeSpan.Zero)
                {
                    await InterruptibleDelayAsync(interval, stoppingToken);
                }
                continue;
            }

            // Skip work if woken by an interval change with no manual run pending - just re-sleep
            // with the new interval.
            if (IntervalJustChanged && !manualPending)
            {
                IntervalJustChanged = false;
            }
            // `schedule is null` short-circuits to the unchanged behaviour: without a schedule this
            // branch is still taken unconditionally, and a paused service is stopped by the sleep at
            // the bottom rather than here. With a schedule, the loop only reaches this after sleeping
            // to an occurrence, so the gate is whether the schedule can fire at all - a schedule with
            // no next run idles on the interval sleep, and running the work then would run it on
            // exactly the schedule the user set to stop it.
            else if (manualPending || schedule is null || IsWorkDue(schedule, interval))
            {
                skipFirstExecution = false;
                IntervalJustChanged = false;

                var (shuttingDown, runFailed) = await RunScheduledWorkAsync(
                    async () =>
                    {
                        CurrentRunTrigger = manualPending ? RunTrigger.Manual : RunTrigger.Scheduled;
                        // Broadcast the start so the Schedules status dot lights up for the whole run.
                        ServiceExecutionStateChanged?.Invoke(ServiceKey);
                        await ExecuteWorkAsync(stoppingToken);
                        // Advance NextRunUtc now so the run-END broadcast carries the fresh next-run
                        // instead of the just-elapsed one. The bottom-of-loop sleep re-sets this
                        // authoritatively; this only keeps the END snapshot from shipping a stale
                        // countdown.
                        NextRunUtc = ComputeNextRun(ConfiguredCustomSchedule, EffectiveInterval);
                    },
                    stoppingToken,
                    "{ServiceName} error in execution loop",
                    () => ServiceExecutionStateChanged?.Invoke(ServiceKey));

                if (shuttingDown)
                {
                    break;
                }

                if (runFailed)
                {
                    continue;
                }
            }

            // A Run Now that arrived while ExecuteWorkAsync was running cancelled the delay CTS that
            // belonged to the already-finished prior sleep, so it cannot interrupt the sleep we are
            // about to start below. Detect it here and loop straight into another run instead of
            // sleeping - otherwise a positive-interval service defers it to the next natural wake
            // (mislabelled Manual) and a paused service (interval <= 0) sleeps forever, dropping the
            // accepted run entirely. The flag is consumed exactly once at the loop top, which re-reads
            // it and tags the follow-up run Manual; peek without consuming here.
            if (HasPendingManualRun())
            {
                continue;
            }

            interval = EffectiveInterval;
            schedule = ConfiguredCustomSchedule;
            NextRunUtc = ComputeNextRun(schedule, interval);

            if (schedule is not null && NextRunUtc is not null)
            {
                // Sleep to the schedule's own instant. This is what makes a restart leave a schedule
                // where it was: the interval branch below counts from now, so downtime pushes every
                // later run out, while an occurrence is the same wall-clock time either way.
                await InterruptibleDelayAsync(TimeUntil(NextRunUtc.Value), stoppingToken);
            }
            else
            {
                // A schedule with no next run at all cannot be waited for, so it idles on the ordinary
                // interval sleep and re-checks on every wake rather than recomputing the same null in
                // a tight loop. Below that, unchanged interval behaviour: zero disables and negative
                // means startup-only, and both wait until the schedule or interval changes.
                if (schedule is not null)
                {
                    WarnScheduleNeverFires(schedule);
                }

                var idleDelay = interval.TotalHours < 0 || interval == TimeSpan.Zero
                    ? Timeout.InfiniteTimeSpan
                    : interval;
                await InterruptibleDelayAsync(idleDelay, stoppingToken);
            }
        }

        _logger.LogInformation("{ServiceName} stopped", ServiceName);
    }

    /// <summary>
    /// Override to run work on startup before the main loop.
    /// </summary>
    protected virtual Task OnStartupAsync(CancellationToken stoppingToken)
        => Task.CompletedTask;

    /// <summary>
    /// Services on this base do not run at startup unless they say otherwise: the loop's first
    /// pass is OnStartupAsync, which most of these services use for cleanup or monitoring work
    /// that has no reason to fire the moment the app comes up. Subclasses override this to
    /// express their intended default.
    /// </summary>
    public override bool DefaultRunOnStartup => false;

    /// <summary>
    /// Convenience helper for subclass constructors: applies any user-saved overrides for this
    /// ServiceKey from the state store. Call this from the constructor after the base constructor
    /// has run, to avoid duplicating the same load-from-state pattern in every scheduled service.
    /// </summary>
    protected void LoadStateOverrides(IStateService stateService)
        => LoadStateOverrides(stateService, ServiceKey);

    /// <inheritdoc />
    protected override void ApplyLoadedInterval(TimeSpan interval) => SetInterval(interval);

    /// <summary>
    /// The main work to execute on each interval.
    /// </summary>
    protected abstract Task ExecuteWorkAsync(CancellationToken stoppingToken);

    /// <summary>
    /// Check if the service is enabled based on configuration.
    /// </summary>
    protected virtual bool IsEnabled()
    {
        if (string.IsNullOrEmpty(EnabledConfigKey))
            return true;

        return _configuration.GetValue<bool>(EnabledConfigKey, EnabledByDefault);
    }
}
