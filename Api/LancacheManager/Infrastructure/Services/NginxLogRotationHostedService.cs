using Microsoft.Extensions.Hosting;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Hosted service that runs nginx log rotation at startup and on a configurable schedule.
/// This ensures logs are properly rotated even if the system was down during the scheduled time.
/// </summary>
public class NginxLogRotationHostedService : BackgroundService
{
    private readonly NginxLogRotationService _rotationService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NginxLogRotationHostedService> _logger;

    // Status tracking
    private DateTime? _lastRotationTime;
    private DateTime? _nextScheduledRotation;
    private bool _lastRotationSuccess;
    private string? _lastRotationError;
    private readonly object _statusLock = new();

    public NginxLogRotationHostedService(
        NginxLogRotationService rotationService,
        IConfiguration configuration,
        ILogger<NginxLogRotationHostedService> logger)
    {
        _rotationService = rotationService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Get the current status of log rotation
    /// </summary>
    public LogRotationStatus GetStatus()
    {
        lock (_statusLock)
        {
            var enabled = _configuration.GetValue<bool>("NginxLogRotation:Enabled", false);
            var scheduleHours = _configuration.GetValue<int>("NginxLogRotation:ScheduleHours", 0);

            return new LogRotationStatus
            {
                Enabled = enabled,
                ScheduleHours = scheduleHours,
                LastRotationTime = _lastRotationTime,
                NextScheduledRotation = _nextScheduledRotation,
                LastRotationSuccess = _lastRotationSuccess,
                LastRotationError = _lastRotationError
            };
        }
    }

    /// <summary>
    /// Force an immediate log rotation
    /// </summary>
    public async Task<bool> ForceRotationAsync()
    {
        _logger.LogInformation("Force log rotation requested");
        return await ExecuteRotationAsync("Manual trigger");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _configuration.GetValue<bool>("NginxLogRotation:Enabled", false);

        if (!enabled)
        {
            _logger.LogInformation("Nginx log rotation is disabled in configuration");
            return;
        }

        // Run rotation at startup to ensure clean slate
        _logger.LogInformation("Running nginx log rotation at startup...");
        await ExecuteRotationAsync("Startup");

        // Check if scheduled rotation is enabled
        var scheduleHours = _configuration.GetValue<int>("NginxLogRotation:ScheduleHours", 0);

        if (scheduleHours <= 0)
        {
            _logger.LogInformation("Scheduled log rotation is disabled (ScheduleHours = 0)");
            return;
        }

        _logger.LogInformation("Scheduled log rotation enabled: every {Hours} hour(s)", scheduleHours);

        // Calculate next rotation time
        UpdateNextScheduledRotation(scheduleHours);

        // Run scheduled rotation loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = TimeSpan.FromHours(scheduleHours);
                _logger.LogDebug("Next log rotation in {Hours} hour(s) at {Time}",
                    scheduleHours, DateTime.UtcNow.Add(delay));

                await Task.Delay(delay, stoppingToken);

                if (!stoppingToken.IsCancellationRequested)
                {
                    await ExecuteRotationAsync("Scheduled");
                    UpdateNextScheduledRotation(scheduleHours);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in log rotation schedule loop");
                // Wait a bit before retrying
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private async Task<bool> ExecuteRotationAsync(string trigger)
    {
        try
        {
            var success = await _rotationService.ReopenNginxLogsAsync();

            lock (_statusLock)
            {
                _lastRotationTime = DateTime.UtcNow;
                _lastRotationSuccess = success;
                _lastRotationError = success ? null : "Rotation command failed";
            }

            if (success)
            {
                _logger.LogInformation("Log rotation completed successfully (trigger: {Trigger})", trigger);
            }
            else
            {
                _logger.LogWarning("Log rotation failed (trigger: {Trigger})", trigger);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing log rotation (trigger: {Trigger})", trigger);

            lock (_statusLock)
            {
                _lastRotationTime = DateTime.UtcNow;
                _lastRotationSuccess = false;
                _lastRotationError = ex.Message;
            }

            return false;
        }
    }

    private void UpdateNextScheduledRotation(int scheduleHours)
    {
        if (scheduleHours > 0)
        {
            lock (_statusLock)
            {
                _nextScheduledRotation = DateTime.UtcNow.AddHours(scheduleHours);
            }
        }
    }
}

/// <summary>
/// Status information for nginx log rotation
/// </summary>
public class LogRotationStatus
{
    public bool Enabled { get; set; }
    public int ScheduleHours { get; set; }
    public DateTime? LastRotationTime { get; set; }
    public DateTime? NextScheduledRotation { get; set; }
    public bool LastRotationSuccess { get; set; }
    public string? LastRotationError { get; set; }
}
