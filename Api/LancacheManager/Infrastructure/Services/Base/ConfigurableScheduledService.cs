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
    protected readonly ILogger _logger;

    private TimeSpan _interval;
    private CancellationTokenSource? _intervalChangedCts;
    private readonly object _intervalLock = new();

    /// <summary>
    /// The name of this service for logging purposes.
    /// </summary>
    protected abstract string ServiceName { get; }

    /// <summary>
    /// Current scheduling interval. When the interval is zero, the service is considered disabled
    /// and ExecuteScheduledWorkAsync will not be called.
    /// Thread-safe: reads/writes are protected by a lock.
    /// </summary>
    protected TimeSpan ConfiguredInterval
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

            // Cancel the current sleep to wake the loop immediately
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

            if (interval > TimeSpan.Zero)
            {
                try
                {
                    await ExecuteScheduledWorkAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{ServiceName} error in scheduled work", ServiceName);
                }
            }

            // Sleep for the configured interval (or a fallback if disabled)
            // Use a linked CTS so UpdateInterval() can wake us up
            interval = ConfiguredInterval;
            var sleepDuration = interval > TimeSpan.Zero ? interval : TimeSpan.FromMinutes(5);

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
