using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.Scheduling;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services.Base;

/// <summary>
/// Shared foundation for the scheduled-service base classes. Owns the parts of a scheduled service
/// that do not depend on how its schedule is stored or advanced: the user-facing run-on-startup and
/// notification preferences, run tracking, the manual-run flag the Schedules "Run Now" button sets,
/// and the state-store load helper.
///
/// The two loop implementations (<see cref="ScheduledBackgroundService"/> and
/// <see cref="ConfigurableScheduledService"/>) keep their own interval storage and sleep handling,
/// and each declares its own <see cref="DefaultRunOnStartup"/>: the two hierarchies ship different
/// values, so this class leaves the property abstract rather than picking a default that would
/// silently change startup behaviour for whichever hierarchy did not expect it.
/// </summary>
public abstract class ScheduledServiceBase : BackgroundService
{
    protected readonly ILogger _logger;

    // Trigger provenance for the run currently executing. Set by TriggerImmediateRun and consumed by
    // the loop just before it calls ExecuteWorkAsync, so a subclass can gate notifications on whether
    // a run was manual. Accessed only through Interlocked, so the loop and an HTTP-thread Run Now can
    // race on it safely.
    private int _pendingManualRun;

    protected ScheduledServiceBase(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The name of this service for logging purposes.
    /// </summary>
    protected abstract string ServiceName { get; }

    /// <summary>
    /// Delay before starting the service (allows app to initialize).
    /// Default: 5 seconds.
    /// </summary>
    protected virtual TimeSpan StartupDelay => TimeSpan.FromSeconds(5);

    /// <summary>
    /// Delay before retrying after a loop error, so a persistent failure backs off instead of
    /// tight-looping.
    /// </summary>
    protected virtual TimeSpan ErrorRetryDelay => TimeSpan.FromMinutes(1);

    // Schedule tracking properties
    public DateTime? LastRunUtc { get; protected set; }
    public DateTime? NextRunUtc { get; protected set; }

    // Written on the service loop thread, read cross-thread by the HTTP GET /schedules path
    // (ServiceScheduleRegistry.GetAll). volatile publishes the write so a status read on another
    // thread cannot latch a stale value and leave the Schedules dot wrong.
    private volatile bool _isCurrentlyExecuting;
    public bool IsCurrentlyExecuting
    {
        get => _isCurrentlyExecuting;
        protected set => _isCurrentlyExecuting = value;
    }

    /// <summary>
    /// Trigger provenance for the run currently executing. Each loop resolves this immediately
    /// before calling ExecuteWorkAsync, so a subclass reading it inside that call sees the trigger
    /// for its own run.
    /// </summary>
    protected RunTrigger CurrentRunTrigger { get; set; } = RunTrigger.Scheduled;

    /// <summary>
    /// Takes the pending manual-run flag, clearing it. Returns true when a Run Now is waiting to be
    /// honored by this loop iteration. Consuming it here (rather than leaving it set) is what stops a
    /// later, genuinely scheduled tick from being misattributed as Manual.
    /// </summary>
    protected bool ConsumePendingManualRun() => Interlocked.Exchange(ref _pendingManualRun, 0) == 1;

    /// <summary>
    /// Reads the pending manual-run flag without clearing it, so the loop can spot a Run Now that
    /// arrived mid-run and go straight into another iteration instead of sleeping. The loop top
    /// consumes the flag and tags the follow-up run Manual.
    /// </summary>
    protected bool HasPendingManualRun() => Interlocked.CompareExchange(ref _pendingManualRun, 0, 0) == 1;

    /// <summary>
    /// Wake the service immediately - cancels the current sleep so work runs on the next loop.
    /// </summary>
    public virtual void TriggerImmediateRun()
    {
        // Mark the next work run as manually triggered. Set before waking the loop so the woken
        // iteration observes it when it computes CurrentRunTrigger.
        Interlocked.Exchange(ref _pendingManualRun, 1);

        CancelIntervalDelay();

        _logger.LogDebug("{ServiceName} immediate run triggered", ServiceName);
    }

    /// <summary>
    /// Cancels the loop's current sleep so it wakes on the next iteration. Implemented by each loop
    /// against its own delay token source; safe to call while already holding that loop's interval
    /// lock, which is how an interval change stays atomic with the wake it triggers.
    /// </summary>
    protected abstract void CancelIntervalDelay();

    /// <summary>
    /// Hardcoded default for whether work runs at startup, before the first interval elapses.
    /// Left abstract because the two loop base classes ship different defaults - a shared default
    /// here would silently flip startup behaviour for one of the two hierarchies. Individual
    /// services override it to express their intended default; the user can override it at runtime
    /// via SetRunOnStartup - typically loaded from IStateService in each service's constructor and
    /// updated via the Schedules UI.
    /// </summary>
    public abstract bool DefaultRunOnStartup { get; }

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
    /// runtime via SetNotificationMode - typically loaded from IStateService in each service's
    /// constructor and updated via the Schedules UI.
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

    /// <summary>
    /// Convenience helper for subclass constructors: applies any user-saved interval, run-on-startup
    /// and notification overrides for this service from the state store. Pass the same service key
    /// the registry uses, so the values written by the Schedules UI are the ones read back.
    /// </summary>
    protected void LoadStateOverrides(IStateService stateService, string serviceKey)
    {
        var savedInterval = stateService.GetServiceInterval(serviceKey);
        if (savedInterval.HasValue)
        {
            ApplyLoadedInterval(TimeSpan.FromHours(savedInterval.Value));
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

        var savedCustomSchedule = stateService.GetServiceCustomSchedule(serviceKey);
        if (savedCustomSchedule is not null)
        {
            UpdateCustomSchedule(savedCustomSchedule);
        }
    }

    /// <summary>
    /// Applies an interval loaded from the state store. Each loop base routes this to its own
    /// interval setter, which differ in what they treat as the default to fall back to.
    /// </summary>
    protected abstract void ApplyLoadedInterval(TimeSpan interval);

    private readonly object _customScheduleLock = new();
    private CustomSchedule? _customSchedule;
    private volatile bool _unreachableScheduleLogged;

    /// <summary>
    /// Custom schedule currently driving this service, or null when it runs on its plain interval. A
    /// schedule wins outright over the interval, and the interval is left untouched so clearing the
    /// schedule puts the service straight back on the cadence it had.
    /// Thread-safe: the loop reads it every iteration while an HTTP save writes it.
    /// </summary>
    public CustomSchedule? ConfiguredCustomSchedule
    {
        get { lock (_customScheduleLock) return _customSchedule; }
    }

    /// <summary>
    /// Sets or clears the custom schedule at runtime, waking the loop so the change takes effect
    /// immediately rather than after the sleep computed from the previous value. Public because the
    /// schedule registry already holds the typed service instance and can apply the value straight
    /// through, and because both loop bases expose the same setter.
    /// </summary>
    public void UpdateCustomSchedule(CustomSchedule? schedule)
    {
        lock (_customScheduleLock)
        {
            _customSchedule = schedule;
            // Re-arm the never-fires warning so a corrected schedule that still cannot fire says so
            // again instead of staying silent behind the previous one.
            _unreachableScheduleLogged = false;
        }

        WakeForScheduleChange();

        if (schedule is null)
        {
            _logger.LogInformation("{ServiceName} custom schedule cleared, back on its interval", ServiceName);
        }
        else
        {
            _logger.LogInformation("{ServiceName} custom schedule set to '{Expression}' ({TimeZoneId})",
                ServiceName, schedule.Expression, schedule.TimeZoneId);
        }
    }

    /// <summary>
    /// Tells the loop that the value its current sleep was computed from has changed: the loop must
    /// skip work on the wake this causes and re-sleep on the new value. Each loop owns its own flag
    /// and delay source, which is why this cannot live here.
    /// </summary>
    protected abstract void WakeForScheduleChange();

    /// <summary>
    /// The instant this service should next run: the custom schedule's next occurrence when one is
    /// set, otherwise the interval counted from now. Null means nothing is scheduled - the service is
    /// paused (interval at or below zero), or the schedule can never fire again.
    /// </summary>
    protected static DateTime? ComputeNextRun(CustomSchedule? schedule, TimeSpan interval)
    {
        if (schedule is not null)
        {
            return ScheduleTiming.ComputeNextRun(schedule, DateTime.UtcNow);
        }

        return interval > TimeSpan.Zero ? DateTime.UtcNow + interval : null;
    }

    /// <summary>
    /// How long to wait until <paramref name="nextRunUtc"/>, never negative. The next run and this
    /// wait read the clock a moment apart, so an occurrence that has just passed clamps to zero rather
    /// than asking for a negative delay.
    /// </summary>
    protected static TimeSpan TimeUntil(DateTime nextRunUtc)
    {
        var remaining = nextRunUtc - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Whether this iteration may do work. A schedule replaces the interval outright, so a service
    /// that has one is gated on whether the schedule can fire at all rather than on its interval: a
    /// loop only reaches its work branch after sleeping to an occurrence, while a schedule with no
    /// next run (an expression that can never land inside its own window) idles on the ordinary
    /// interval sleep instead. Testing the interval in that case would run the work on exactly the
    /// schedule the user set to stop it.
    /// </summary>
    protected static bool IsWorkDue(CustomSchedule? schedule, TimeSpan interval)
    {
        return schedule is not null
            ? ScheduleTiming.ComputeNextRun(schedule, DateTime.UtcNow) is not null
            : interval > TimeSpan.Zero;
    }

    /// <summary>
    /// Warns, at most once per schedule, that a schedule has no next run at all - an expression with
    /// no future occurrence, or one that can never land inside its own window. A loop reaches this on
    /// every wake, so warning per iteration would fill the log while the service sits idle.
    /// </summary>
    protected void WarnScheduleNeverFires(CustomSchedule schedule)
    {
        if (_unreachableScheduleLogged)
        {
            return;
        }

        _unreachableScheduleLogged = true;
        _logger.LogWarning(
            "{ServiceName} custom schedule '{Expression}' ({TimeZoneId}) has no next run and will not fire until it is changed",
            ServiceName, schedule.Expression, schedule.TimeZoneId);
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
}
