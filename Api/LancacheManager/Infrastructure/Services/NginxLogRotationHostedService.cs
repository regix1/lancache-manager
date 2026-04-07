using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.Base;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Hosted service that runs nginx log rotation at startup and on a configurable schedule.
/// Uses the base class ScheduledBackgroundService loop — ExecuteWorkAsync performs one rotation
/// and returns; the base class handles the sleep/interval between runs.
/// </summary>
public class NginxLogRotationHostedService : ScheduledBackgroundService
{
    private readonly NginxLogRotationService _rotationService;
    private readonly string _settingsFilePath;

    // Status tracking
    private DateTime? _lastRotationTime;
    private bool _lastRotationSuccess;
    private string? _lastRotationError;
    private int _currentScheduleHours;
    private readonly object _statusLock = new();

    protected override string ServiceName => "NginxLogRotation";
    protected override TimeSpan StartupDelay => TimeSpan.Zero;
    protected override TimeSpan Interval => TimeSpan.FromHours(24);

    public override bool RunOnStartup => true;
    public override string ServiceKey => "logRotation";

    public NginxLogRotationHostedService(
        NginxLogRotationService rotationService,
        IConfiguration configuration,
        ILogger<NginxLogRotationHostedService> logger,
        IPathResolver pathResolver)
        : base(logger, configuration)
    {
        _rotationService = rotationService;
        _settingsFilePath = pathResolver.GetSettingsPath("log-rotation-settings.json");
        _currentScheduleHours = LoadScheduleHours();

        // Set the base class interval from the persisted schedule
        ApplyScheduleInterval(_currentScheduleHours);
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
            var settingsDir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(settingsDir) && !Directory.Exists(settingsDir))
            {
                Directory.CreateDirectory(settingsDir);
            }

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
    /// Apply the schedule hours to the base class interval.
    /// Hours &lt;= 0 sets a negative interval which the base class treats as "sleep until changed".
    /// </summary>
    private void ApplyScheduleInterval(int hours)
    {
        if (hours > 0)
        {
            SetInterval(TimeSpan.FromHours(hours));
        }
        else
        {
            // Negative TimeSpan tells the base class to sleep indefinitely until interval is changed
            SetInterval(TimeSpan.FromHours(-1));
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
        }

        await SaveScheduleHoursAsync(hours);

        // Update the base class interval — this also wakes the sleep loop
        ApplyScheduleInterval(hours);

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
                NextScheduledRotation = NextRunUtc,
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

    protected override bool IsEnabled()
        => _configuration.GetValue<bool>("NginxLogRotation:Enabled", false);

    /// <summary>
    /// Runs once at startup via the base class OnStartupAsync mechanism.
    /// </summary>
    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Running nginx log rotation at startup...");
        await ExecuteRotationAsync("Startup");
    }

    /// <summary>
    /// Performs a single rotation cycle and returns.
    /// The base class loop handles the sleep/interval between runs.
    /// </summary>
    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        await ExecuteRotationAsync("Scheduled");
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
