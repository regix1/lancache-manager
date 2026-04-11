using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Services.Base;

/// <summary>
/// Base class for background services that run on a schedule.
/// Provides common functionality for startup delay, configuration checking,
/// error handling, and interval-based execution.
/// Supports runtime-configurable intervals via SetInterval() and TriggerImmediateRun().
/// </summary>
public abstract class ScheduledBackgroundService : BackgroundService
{
    /// <summary>
    /// Fired whenever a scheduled service completes a unit of work (startup or interval run).
    /// Subscribers receive the ServiceKey of the service that completed.
    /// </summary>
    public static event Action<string>? ServiceWorkCompleted;
    protected readonly ILogger _logger;
    protected readonly IConfiguration _configuration;

    // Runtime interval override state
    private TimeSpan? _intervalOverride;
    private CancellationTokenSource? _intervalChangedCts;
    private readonly object _intervalLock = new();
    private volatile bool _intervalJustChanged;

    // Schedule tracking properties
    public DateTime? LastRunUtc { get; private set; }
    public DateTime? NextRunUtc { get; private set; }
    public bool IsCurrentlyExecuting { get; private set; }

    // Schedule metadata — override in subclasses to register as user-configurable
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
    /// The name of this service for logging purposes.
    /// </summary>
    protected abstract string ServiceName { get; }

    /// <summary>
    /// Delay before starting the service (allows app to initialize).
    /// Default: 5 seconds.
    /// </summary>
    protected virtual TimeSpan StartupDelay => TimeSpan.FromSeconds(5);

    /// <summary>
    /// Default time between work executions. Return TimeSpan.Zero to run continuously.
    /// Use EffectiveInterval in the loop — this is the hardcoded default only.
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

    /// <summary>
    /// Delay before retrying after an error.
    /// </summary>
    protected virtual TimeSpan ErrorRetryDelay => TimeSpan.FromMinutes(1);

    protected ScheduledBackgroundService(ILogger logger, IConfiguration configuration)
    {
        _logger = logger;
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

            try
            {
                _intervalChangedCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed — will be recreated on next loop iteration
            }
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

            try
            {
                _intervalChangedCts?.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        _logger.LogDebug("{ServiceName} interval reset to default ({Interval})", ServiceName, Interval);
    }

    /// <summary>
    /// Wake the service immediately — cancels the current sleep so work runs on the next loop.
    /// </summary>
    public void TriggerImmediateRun()
    {
        lock (_intervalLock)
        {
            try
            {
                _intervalChangedCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed — will be recreated on next loop iteration
            }
        }

        _logger.LogDebug("{ServiceName} immediate run triggered", ServiceName);
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
                await OnStartupAsync(stoppingToken);
                LastRunUtc = DateTime.UtcNow;
                ServiceWorkCompleted?.Invoke(ServiceKey);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{ServiceName} startup execution failed", ServiceName);
            }
            finally
            {
                IsCurrentlyExecuting = false;
            }
        }

        // Main execution loop.
        // Always sleep one interval before the first ExecuteWorkAsync — this honors both:
        //   1. RunOnStartup=true: OnStartupAsync already ran above, so we skip back-to-back work
        //   2. RunOnStartup=false: user explicitly opted out of startup runs, so ExecuteWorkAsync
        //      must NOT fire on the first iteration either (otherwise "disabling startup" is a lie)
        // The first ExecuteWorkAsync only runs after the interval has elapsed (or via Run Now).
        bool skipFirstExecution = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (skipFirstExecution)
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

            // Skip work if woken by an interval change — just re-sleep with the new interval
            if (_intervalJustChanged)
            {
                _intervalJustChanged = false;
            }
            else
            {
                try
                {
                    IsCurrentlyExecuting = true;
                    await ExecuteWorkAsync(stoppingToken);
                    LastRunUtc = DateTime.UtcNow;
                    ServiceWorkCompleted?.Invoke(ServiceKey);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{ServiceName} error in execution loop", ServiceName);
                    await SafeDelayAsync(ErrorRetryDelay, stoppingToken);
                    continue;
                }
                finally
                {
                    IsCurrentlyExecuting = false;
                }
            }

            var interval = EffectiveInterval;
            if (interval.TotalHours < 0 || interval == TimeSpan.Zero)
            {
                // Zero = disabled, negative = startup only — sleep until interval is changed or service stops
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
            // Interval changed or immediate run triggered — loop back to pick up the change
            _logger.LogDebug("{ServiceName} sleep interrupted by interval change or trigger", ServiceName);
        }
        catch (OperationCanceledException)
        {
            // Service is shutting down — let the loop exit
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
    /// Hardcoded default for whether OnStartupAsync runs before the main loop.
    /// Subclasses override this to express their *intended* default. The user can override
    /// this at runtime via SetRunOnStartup() — typically loaded from IStateService in
    /// each service's constructor and updated via the Schedules UI.
    /// </summary>
    public virtual bool DefaultRunOnStartup => false;

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
    /// to DefaultRunOnStartup. Note: this only affects future startups — once a service
    /// has already started its loop, toggling this won't retroactively run or skip
    /// the startup pass.
    /// </summary>
    public void SetRunOnStartup(bool? value)
    {
        _runOnStartupOverride = value;
        _logger.LogDebug("{ServiceName} RunOnStartup override set to {Value}", ServiceName, value);
    }

    /// <summary>
    /// Convenience helper for subclass constructors: applies any user-saved interval and
    /// run-on-startup overrides for this ServiceKey from the state store. Call this from
    /// the constructor after the base constructor has run, to avoid duplicating the same
    /// load-from-state pattern in every scheduled service.
    /// </summary>
    protected void LoadStateOverrides(IStateService stateService)
    {
        var savedInterval = stateService.GetServiceInterval(ServiceKey);
        if (savedInterval.HasValue)
        {
            SetInterval(TimeSpan.FromHours(savedInterval.Value));
        }

        var savedRunOnStartup = stateService.GetServiceRunOnStartup(ServiceKey);
        if (savedRunOnStartup.HasValue)
        {
            SetRunOnStartup(savedRunOnStartup.Value);
        }
    }

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

    /// <summary>
    /// Safely delay, catching cancellation exceptions.
    /// </summary>
    protected async Task SafeDelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
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
