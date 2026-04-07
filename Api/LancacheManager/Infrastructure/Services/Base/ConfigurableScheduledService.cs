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

    protected ConfigurableScheduledService(ILogger logger, TimeSpan initialInterval)
    {
        _logger = logger;
        _defaultInterval = initialInterval;
        _interval = initialInterval;
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

            // Cancel the current sleep to wake the loop — it will skip work and re-sleep with new interval
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
    /// Wake the service immediately — cancels the current sleep so work runs on the next loop.
    /// Unlike UpdateInterval, this does NOT set _intervalJustChanged, so the loop will
    /// execute work rather than just re-sleeping.
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
        if (StartupDelay > TimeSpan.Zero)
        {
            _logger.LogDebug("{ServiceName} waiting {Delay} before starting", ServiceName, StartupDelay);
            await SafeDelayAsync(StartupDelay, stoppingToken);
        }

        _logger.LogInformation("{ServiceName} scheduling loop started", ServiceName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = ConfiguredInterval;

            // Skip work if woken by an interval change — just re-sleep with the new interval
            if (_intervalJustChanged)
            {
                _intervalJustChanged = false;
            }
            else if (interval > TimeSpan.Zero)
            {
                try
                {
                    IsCurrentlyExecuting = true;
                    await ExecuteScheduledWorkAsync(stoppingToken);
                    LastRunUtc = DateTime.UtcNow;
                    ServiceWorkCompleted?.Invoke(ServiceName);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{ServiceName} error in scheduled work", ServiceName);
                }
                finally
                {
                    IsCurrentlyExecuting = false;
                }
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
                // Interval was changed — loop back to check the new interval
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
    protected abstract Task ExecuteScheduledWorkAsync(CancellationToken stoppingToken);

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
