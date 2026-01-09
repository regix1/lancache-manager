using System.Text.Json;
using LancacheManager.Core.Interfaces.Services;
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
    private readonly string _settingsFilePath;

    // Status tracking
    private DateTime? _lastRotationTime;
    private DateTime? _nextScheduledRotation;
    private bool _lastRotationSuccess;
    private string? _lastRotationError;
    private int _currentScheduleHours;
    private readonly object _statusLock = new();
    private CancellationTokenSource? _scheduleCts;

    public NginxLogRotationHostedService(
        NginxLogRotationService rotationService,
        IConfiguration configuration,
        ILogger<NginxLogRotationHostedService> logger,
        IPathResolver pathResolver)
    {
        _rotationService = rotationService;
        _configuration = configuration;
        _logger = logger;
        _settingsFilePath = Path.Combine(pathResolver.GetDataDirectory(), "log-rotation-settings.json");
        _currentScheduleHours = LoadScheduleHours();
    }

    private int LoadScheduleHours()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<LogRotationSettings>(json);
                if (settings != null)
                {
                    _logger.LogInformation("Loaded log rotation schedule: {Hours} hours", settings.ScheduleHours);
                    return settings.ScheduleHours;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load log rotation settings, using config default");
        }

        // Fall back to configuration
        return _configuration.GetValue<int>("NginxLogRotation:ScheduleHours", 24);
    }

    private async Task SaveScheduleHoursAsync(int hours)
    {
        try
        {
            var settings = new LogRotationSettings { ScheduleHours = hours };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_settingsFilePath, json);
            _logger.LogInformation("Saved log rotation schedule: {Hours} hours", hours);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save log rotation settings");
            throw;
        }
    }

    /// <summary>
    /// Update the schedule interval in hours
    /// </summary>
    public async Task<bool> UpdateScheduleAsync(int hours)
    {
        if (hours < 0 || hours > 168) // 0 = disabled, max 1 week
        {
            return false;
        }

        lock (_statusLock)
        {
            _currentScheduleHours = hours;
            if (hours > 0)
            {
                _nextScheduledRotation = DateTime.UtcNow.AddHours(hours);
            }
            else
            {
                _nextScheduledRotation = null;
            }
        }

        await SaveScheduleHoursAsync(hours);

        // Cancel the current schedule to restart with new interval
        _scheduleCts?.Cancel();

        _logger.LogInformation("Log rotation schedule updated to {Hours} hours", hours);
        return true;
    }

    /// <summary>
    /// Get the current status of log rotation
    /// </summary>
    public LogRotationStatus GetStatus()
    {
        lock (_statusLock)
        {
            var enabled = _configuration.GetValue<bool>("NginxLogRotation:Enabled", false);

            return new LogRotationStatus
            {
                Enabled = enabled,
                ScheduleHours = _currentScheduleHours,
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

        // Run scheduled rotation loop
        while (!stoppingToken.IsCancellationRequested)
        {
            int scheduleHours;
            lock (_statusLock)
            {
                scheduleHours = _currentScheduleHours;
            }

            if (scheduleHours <= 0)
            {
                _logger.LogDebug("Scheduled log rotation is disabled (ScheduleHours = 0), waiting for schedule change");
                // Wait for a schedule update or shutdown
                _scheduleCts = new CancellationTokenSource();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _scheduleCts.Token);
                try
                {
                    await Task.Delay(Timeout.Infinite, linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (stoppingToken.IsCancellationRequested)
                        break;
                    // Schedule was updated, continue loop
                    continue;
                }
            }

            _logger.LogInformation("Scheduled log rotation enabled: every {Hours} hour(s)", scheduleHours);
            UpdateNextScheduledRotation(scheduleHours);

            try
            {
                var delay = TimeSpan.FromHours(scheduleHours);
                _logger.LogDebug("Next log rotation in {Hours} hour(s) at {Time}",
                    scheduleHours, DateTime.UtcNow.Add(delay));

                // Create a new CTS for this schedule cycle
                _scheduleCts = new CancellationTokenSource();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _scheduleCts.Token);

                await Task.Delay(delay, linkedCts.Token);

                if (!stoppingToken.IsCancellationRequested && !_scheduleCts.IsCancellationRequested)
                {
                    await ExecuteRotationAsync("Scheduled");
                }
            }
            catch (OperationCanceledException)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;
                // Schedule was updated, restart the loop with new schedule
                _logger.LogDebug("Schedule updated, restarting rotation cycle");
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in log rotation schedule loop");
                // Wait a bit before retrying
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<bool> ExecuteRotationAsync(string trigger)
    {
        try
        {
            var result = await _rotationService.ReopenNginxLogsAsync();

            lock (_statusLock)
            {
                _lastRotationTime = DateTime.UtcNow;
                _lastRotationSuccess = result.Success;
                _lastRotationError = result.ErrorMessage;
            }

            if (result.Success)
            {
                _logger.LogInformation("Log rotation completed successfully (trigger: {Trigger})", trigger);
            }
            else
            {
                _logger.LogWarning("Log rotation failed (trigger: {Trigger}): {Error}", trigger, result.ErrorMessage);
            }

            return result.Success;
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

/// <summary>
/// Settings for nginx log rotation (persisted to file)
/// </summary>
public class LogRotationSettings
{
    public int ScheduleHours { get; set; } = 24;
}
