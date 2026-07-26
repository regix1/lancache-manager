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
    private CancellationTokenSource? _intervalChangedCts;
    private readonly object _intervalLock = new();
    private volatile bool _intervalJustChanged;

    // Schedule metadata - override in subclasses to register as user-configurable
    public virtual string ServiceKey => GetType().Name;

    /// <summary>
    /// The effective interval used by the loop: override if set, otherwise the abstract default.
    /// </summary>
    public TimeSpan EffectiveInterval
    {
        get
        {
            lock (_intervalLock)
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
        lock (_intervalLock)
        {
            _intervalOverride = newInterval;
            _intervalJustChanged = true;

            CancelIntervalDelay();
        }

        _logger.LogDebug("{ServiceName} interval changed to {Interval}", ServiceName, newInterval);
    }

    /// <summary>
    /// Clear the runtime interval override, reverting to the hardcoded default.
    /// </summary>
    public void ResetInterval()
    {
        lock (_intervalLock)
        {
            _intervalOverride = null;
            _intervalJustChanged = true;

            CancelIntervalDelay();
        }

        _logger.LogDebug("{ServiceName} interval reset to default ({Interval})", ServiceName, Interval);
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
                // Already disposed - will be recreated on next loop iteration
            }
        }
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
                // one-interval sleep, so set the countdown to that before the END broadcast rather than
                // shipping a null "Soon". The skip-first sleep re-sets this authoritatively.
                var startupNextInterval = EffectiveInterval;
                NextRunUtc = startupNextInterval > TimeSpan.Zero
                    ? DateTime.UtcNow + startupNextInterval
                    : null;
                // Broadcast the end AFTER clearing the flag so GetAll() reports the run finished and the
                // dot clears - including on the failure path above.
                ServiceExecutionStateChanged?.Invoke(ServiceKey);
            }
        }

        // Discard any "_intervalJustChanged" flag that was set during construction or
        // InitializeAsync - e.g. LoadStateOverrides → SetInterval sets that flag to wake
        // a sleeping loop, but there's no loop yet, so the flag is meaningless here and
        // must not leak into the first iteration (it would cause the first real work run
        // to be delayed by an extra full interval).
        _intervalJustChanged = false;

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
            // - checking _intervalJustChanged/skipFirstExecution first and only reading
            // _pendingManualRun in the other branch silently drops a same-tick Run Now (no work
            // happens) AND leaves the flag stale to misattribute a LATER genuinely scheduled tick as
            // Manual. Mirrors ConfigurableScheduledService's ordering.
            var manualPending = ConsumePendingManualRun();

            if (skipFirstExecution && !manualPending)
            {
                skipFirstExecution = false;
                var skipInterval = EffectiveInterval;
                if (skipInterval > TimeSpan.Zero)
                {
                    NextRunUtc = DateTime.UtcNow + skipInterval;
                    await InterruptibleDelayAsync(skipInterval, stoppingToken);
                }
                continue;
            }

            // Skip work if woken by an interval change with no manual run pending - just re-sleep
            // with the new interval.
            if (_intervalJustChanged && !manualPending)
            {
                _intervalJustChanged = false;
            }
            else
            {
                skipFirstExecution = false;
                _intervalJustChanged = false;
                var runFailed = false;
                try
                {
                    IsCurrentlyExecuting = true;
                    CurrentRunTrigger = manualPending ? RunTrigger.Manual : RunTrigger.Scheduled;
                    // Broadcast the start so the Schedules status dot lights up for the whole run.
                    ServiceExecutionStateChanged?.Invoke(ServiceKey);
                    await ExecuteWorkAsync(stoppingToken);
                    LastRunUtc = DateTime.UtcNow;
                    // Advance NextRunUtc now so the run-END broadcast in the finally carries the fresh
                    // next-run instead of the just-elapsed one. The bottom-of-loop sleep re-sets this
                    // authoritatively; this only keeps the END snapshot from shipping a stale countdown.
                    var nextInterval = EffectiveInterval;
                    NextRunUtc = nextInterval > TimeSpan.Zero ? DateTime.UtcNow + nextInterval : null;
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
                    _logger.LogError(ex, "{ServiceName} error in execution loop", ServiceName);
                    runFailed = true;
                    // The next attempt is the retry below, not the elapsed schedule - point the
                    // countdown in the run-END broadcast at the retry deadline.
                    NextRunUtc = DateTime.UtcNow + ErrorRetryDelay;
                }
                finally
                {
                    IsCurrentlyExecuting = false;
                    // Broadcast the end AFTER clearing the flag so GetAll() reports the run finished and
                    // the dot clears - including on the failed-run path.
                    ServiceExecutionStateChanged?.Invoke(ServiceKey);
                }

                // Back off AFTER the finally above has cleared the flag and broadcast the end, so a
                // failed run does not sit falsely "running" (green dot) for the whole retry delay.
                if (runFailed)
                {
                    await SafeDelayAsync(ErrorRetryDelay, stoppingToken);
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

            var interval = EffectiveInterval;
            if (interval.TotalHours < 0 || interval == TimeSpan.Zero)
            {
                // Zero = disabled, negative = startup only - sleep until interval is changed or service stops
                NextRunUtc = null;
                await InterruptibleDelayAsync(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            else
            {
                NextRunUtc = DateTime.UtcNow + interval;
                await InterruptibleDelayAsync(interval, stoppingToken);
            }
        }

        _logger.LogInformation("{ServiceName} stopped", ServiceName);
    }

    /// <summary>
    /// Delay that can be interrupted by SetInterval() or TriggerImmediateRun().
    /// On interruption (not a shutdown), the loop continues immediately to pick up the change.
    /// </summary>
    private async Task InterruptibleDelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
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

            await Task.Delay(delay, linkedCts.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Interval changed or immediate run triggered - loop back to pick up the change
            _logger.LogDebug("{ServiceName} sleep interrupted by interval change or trigger", ServiceName);
        }
        catch (OperationCanceledException)
        {
            // Service is shutting down - let the loop exit
        }
        finally
        {
            linkedCts?.Dispose();
        }
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
