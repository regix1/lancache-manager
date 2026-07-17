using LancacheManager.Core.Interfaces;
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
public abstract class ConfigurableScheduledService : BackgroundService
{
    /// <summary>
    /// Fired when a configurable service completes work. Subscribers receive the service key.
    /// </summary>
    public static event Action<string>? ServiceWorkCompleted;

    protected readonly ILogger _logger;

    private readonly TimeSpan _defaultInterval;
    private TimeSpan _interval;
    private CancellationTokenSource? _intervalChangedCts;
    private readonly object _intervalLock = new();
    private volatile bool _intervalJustChanged;

    // Trigger provenance for the run currently executing. This base class runs the startup pass, the
    // interval tick, and manual runs through the same ExecuteWorkAsync call site, so CurrentRunTrigger
    // is resolved just before that call from _pendingManualRun (set by TriggerImmediateRun) and a
    // one-shot startup flag. Lets a subclass gate notifications on whether a run was manual.
    private int _pendingManualRun;
    protected RunTrigger CurrentRunTrigger { get; private set; } = RunTrigger.Scheduled;

    // Schedule tracking properties
    public DateTime? LastRunUtc { get; private set; }
    public DateTime? NextRunUtc { get; private set; }
    public bool IsCurrentlyExecuting { get; private set; }

    /// <summary>
    /// The name of this service for logging purposes.
    /// </summary>
    protected abstract string ServiceName { get; }

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
    /// Delay before starting the service loop (allows app to initialize).
    /// </summary>
    protected virtual TimeSpan StartupDelay => TimeSpan.FromSeconds(5);

    /// <summary>
    /// Delay before retrying after a loop error. Mirrors ScheduledBackgroundService's
    /// ErrorRetryDelay so both scheduled-service base classes back off identically instead of
    /// tight-looping on a persistent error.
    /// </summary>
    protected virtual TimeSpan ErrorRetryDelay => TimeSpan.FromMinutes(1);

    /// <summary>
    /// Hardcoded default for whether the loop runs work on its very first iteration
    /// (i.e., at app startup). Subclasses override this to express their *intended* default.
    /// The user can override this at runtime via SetRunOnStartup() - typically loaded from
    /// IStateService and updated via the Schedules UI.
    /// </summary>
    public virtual bool DefaultRunOnStartup => true;

    /// <summary>
    /// User-controlled override for RunOnStartup (null = use DefaultRunOnStartup).
    /// </summary>
    private bool? _runOnStartupOverride;

    /// <summary>
    /// Effective value of RunOnStartup: user override if set, else DefaultRunOnStartup.
    /// </summary>
    public bool RunOnStartup => _runOnStartupOverride ?? DefaultRunOnStartup;

    /// <summary>
    /// Set the user-controlled RunOnStartup override. Pass null to clear and revert
    /// to DefaultRunOnStartup. Note: this only affects future startups - once a service
    /// has already started its loop, toggling this won't retroactively run or skip
    /// the startup pass.
    /// </summary>
    public void SetRunOnStartup(bool? value)
    {
        _runOnStartupOverride = value;
        _logger.LogDebug("{ServiceName} RunOnStartup override set to {Value}", ServiceName, value);
    }

    /// <summary>
    /// Hardcoded default notification mode for this service. Subclasses that emit lifecycle
    /// notifications override this to express their intended default; the user can override it at
    /// runtime via SetNotificationMode - typically loaded from IStateService and updated via the
    /// Schedules UI.
    /// </summary>
    protected virtual NotificationMode DefaultNotificationMode => NotificationMode.All;

    /// <summary>
    /// How this service's notifications render in the universal bar when the user has not
    /// picked a style: maintenance chores default to the condensed line so routine runs stay
    /// out of the way; a service whose runs deserve the full card overrides this.
    /// </summary>
    public virtual NotificationDisplayMode DefaultNotificationDisplayMode => NotificationDisplayMode.Condensed;

    /// <summary>
    /// User-controlled override for the notification mode (null = use DefaultNotificationMode).
    /// </summary>
    private NotificationMode? _notificationModeOverride;

    /// <summary>
    /// Effective notification mode: user override if set, else DefaultNotificationMode.
    /// </summary>
    public NotificationMode EffectiveNotificationMode => _notificationModeOverride ?? DefaultNotificationMode;

    /// <summary>
    /// Set the user-controlled notification-mode override. Pass null to clear and revert to
    /// DefaultNotificationMode.
    /// </summary>
    public void SetNotificationMode(NotificationMode? mode) => _notificationModeOverride = mode;

    /// <summary>
    /// Whether this service emits lifecycle notifications the user can gate from the Schedules UI.
    /// Only services that actually notify override this to true; every other schedule card hides
    /// the Notifications control.
    /// </summary>
    protected virtual bool SupportsNotifications => false;

    protected ConfigurableScheduledService(ILogger logger, TimeSpan initialInterval)
    {
        _logger = logger;
        _defaultInterval = initialInterval;
        _interval = initialInterval;
    }

    /// <summary>
    /// Convenience helper for subclass constructors: applies any user-saved interval and
    /// run-on-startup overrides for this service from the state store. Pass the same
    /// service key the registry uses (typically the subclass's ScheduleServiceKey property).
    /// </summary>
    protected void LoadStateOverrides(IStateService stateService, string serviceKey)
    {
        var savedInterval = stateService.GetServiceInterval(serviceKey);
        if (savedInterval.HasValue)
        {
            UpdateInterval(TimeSpan.FromHours(savedInterval.Value));
        }

        var savedRunOnStartup = stateService.GetServiceRunOnStartup(serviceKey);
        if (savedRunOnStartup.HasValue)
        {
            SetRunOnStartup(savedRunOnStartup.Value);
        }

        var savedNotificationMode = stateService.GetServiceNotificationMode(serviceKey);
        if (savedNotificationMode.HasValue)
        {
            SetNotificationMode(savedNotificationMode.Value);
        }
    }

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
            try
            {
                _intervalChangedCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, will be recreated on next loop iteration
            }
        }

        _logger.LogInformation("{ServiceName} interval updated to {Hours:F1} hour(s)",
            ServiceName, newInterval.TotalHours);
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

    /// <summary>
    /// Wake the service immediately - cancels the current sleep so work runs on the next loop.
    /// Unlike UpdateInterval, this does NOT set _intervalJustChanged, so the loop will
    /// execute work rather than just re-sleeping.
    /// </summary>
    public virtual void TriggerImmediateRun()
    {
        // Mark the next work run as manually triggered. Set before waking the loop so the woken
        // iteration observes it when it computes CurrentRunTrigger.
        Interlocked.Exchange(ref _pendingManualRun, 1);

        lock (_intervalLock)
        {
            try
            {
                _intervalChangedCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed - will be recreated on next loop iteration
            }
        }

        _logger.LogDebug("{ServiceName} immediate run triggered", ServiceName);
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

            // A pending manual run must always be honored this iteration, even if it lands
            // alongside an interval change or the skip-first-execution pass, and even while the
            // schedule itself is paused (interval <= 0) - Run Now overrides all of those the same
            // way TriggerImmediateRun's old due-check bypass used to. Reading it before the branch
            // (rather than only inside the interval>0 branch) is what makes that possible; checking
            // the other conditions first and only reading _pendingManualRun in the innermost branch
            // silently drops a same-tick Run Now AND leaves the flag stale to misattribute a LATER
            // genuinely scheduled tick as Manual.
            var manualPending = Interlocked.Exchange(ref _pendingManualRun, 0) == 1;

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
            else if (manualPending || interval > TimeSpan.Zero)
            {
                _intervalJustChanged = false;
                skipFirstExecution = false;
                try
                {
                    IsCurrentlyExecuting = true;
                    CurrentRunTrigger = manualPending
                        ? RunTrigger.Manual
                        : startupRunPending ? RunTrigger.Startup : RunTrigger.Scheduled;
                    await ExecuteWorkAsync(stoppingToken);
                    LastRunUtc = DateTime.UtcNow;
                    ServiceWorkCompleted?.Invoke(ServiceName);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Shutdown - end the loop cleanly. A non-shutdown OCE (e.g. an inner
                    // per-iteration timeout) falls through to the Exception handler below
                    // instead of silently ending the service loop.
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{ServiceName} error in scheduled work", ServiceName);
                    await SafeDelayAsync(ErrorRetryDelay, stoppingToken);
                    continue;
                }
                finally
                {
                    IsCurrentlyExecuting = false;
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
            if (Interlocked.CompareExchange(ref _pendingManualRun, 0, 0) == 1)
            {
                continue;
            }

            // Sleep for the configured interval (or indefinitely if disabled)
            // Use a linked CTS so UpdateInterval() can wake us up
            interval = ConfiguredInterval;
            var sleepDuration = interval > TimeSpan.Zero ? interval : Timeout.InfiniteTimeSpan;
            NextRunUtc = interval > TimeSpan.Zero ? DateTime.UtcNow + interval : null;

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

    /// <summary>
    /// Safely delay, catching cancellation exceptions.
    /// </summary>
    protected static async Task SafeDelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown or interval change
        }
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
