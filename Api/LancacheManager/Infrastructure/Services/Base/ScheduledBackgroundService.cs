using Microsoft.Extensions.Hosting;

namespace LancacheManager.Infrastructure.Services.Base;

/// <summary>
/// Base class for background services that run on a schedule.
/// Provides common functionality for startup delay, configuration checking,
/// error handling, and interval-based execution.
/// </summary>
public abstract class ScheduledBackgroundService : BackgroundService
{
    protected readonly ILogger Logger;
    protected readonly IConfiguration Configuration;

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
    /// Time between work executions. Return TimeSpan.Zero to run continuously.
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
        Logger = logger;
        Configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check if enabled
        if (!IsEnabled())
        {
            Logger.LogInformation("{ServiceName} is disabled", ServiceName);
            return;
        }

        // Startup delay
        if (StartupDelay > TimeSpan.Zero)
        {
            Logger.LogDebug("{ServiceName} waiting {Delay} before starting",
                ServiceName, StartupDelay);
            await Task.Delay(StartupDelay, stoppingToken);
        }

        Logger.LogInformation("{ServiceName} started", ServiceName);

        // Optional: Run once at startup
        if (RunOnStartup)
        {
            try
            {
                await OnStartupAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "{ServiceName} startup execution failed", ServiceName);
            }
        }

        // Main execution loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteWorkAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{ServiceName} error in execution loop", ServiceName);
                await SafeDelayAsync(ErrorRetryDelay, stoppingToken);
                continue;
            }

            if (Interval > TimeSpan.Zero)
            {
                await SafeDelayAsync(Interval, stoppingToken);
            }
        }

        Logger.LogInformation("{ServiceName} stopped", ServiceName);
    }

    /// <summary>
    /// Override to run work on startup before the main loop.
    /// </summary>
    protected virtual Task OnStartupAsync(CancellationToken stoppingToken)
        => Task.CompletedTask;

    /// <summary>
    /// Whether to run OnStartupAsync before the main loop.
    /// </summary>
    protected virtual bool RunOnStartup => false;

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

        return Configuration.GetValue<bool>(EnabledConfigKey, EnabledByDefault);
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
}
