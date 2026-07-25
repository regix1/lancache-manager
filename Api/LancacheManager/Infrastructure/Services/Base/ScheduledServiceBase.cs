using LancacheManager.Core.Interfaces;
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
    }

    /// <summary>
    /// Applies an interval loaded from the state store. Each loop base routes this to its own
    /// interval setter, which differ in what they treat as the default to fall back to.
    /// </summary>
    protected abstract void ApplyLoadedInterval(TimeSpan interval);

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
