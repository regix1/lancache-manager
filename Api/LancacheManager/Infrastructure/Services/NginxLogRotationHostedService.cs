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

    // Status tracking
    private DateTime? _lastRotationTime;
    private bool _lastRotationSuccess;
    private string? _lastRotationError;
    private readonly object _statusLock = new();

    // Default interval pulled from configuration on construction. Runtime overrides
    // (Schedules UI) come from state.json via the base class LoadStateOverrides helper.
    private readonly TimeSpan _defaultInterval;

    protected override string ServiceName => "NginxLogRotation";
    protected override TimeSpan StartupDelay => TimeSpan.Zero;
    protected override TimeSpan Interval => _defaultInterval;

    public override bool DefaultRunOnStartup => true;
    public override string ServiceKey => "logRotation";

    public NginxLogRotationHostedService(
        NginxLogRotationService rotationService,
        IConfiguration configuration,
        ILogger<NginxLogRotationHostedService> logger,
        IPathResolver pathResolver,
        IStateService stateService)
        : base(logger, configuration)
    {
        _rotationService = rotationService;

        var configHours = configuration.GetValue<int>("NginxLogRotation:ScheduleHours", 24);
        _defaultInterval = TimeSpan.FromHours(configHours);

        // One-time migration: copy any legacy log-rotation-settings.json value into state.json,
        // then delete the file. After this runs, state.json is the sole source of truth and
        // all schedule changes flow through the Schedules UI.
        MigrateLegacySettingsFile(pathResolver, stateService);

        // Apply persisted overrides (interval + run-on-startup) from state.json
        LoadStateOverrides(stateService);
    }

    private void MigrateLegacySettingsFile(IPathResolver pathResolver, IStateService stateService)
    {
        try
        {
            var legacyPath = pathResolver.GetSettingsPath("log-rotation-settings.json");
            if (!File.Exists(legacyPath))
            {
                return;
            }

            var stateInterval = stateService.GetServiceInterval(ServiceKey);
            if (stateInterval.HasValue)
            {
                // state.json already holds the canonical value — the legacy file is stale
                File.Delete(legacyPath);
                _logger.LogInformation(
                    "Removed stale legacy log-rotation-settings.json (state.json already has interval={Hours}h)",
                    stateInterval.Value);
                return;
            }

            var json = File.ReadAllText(legacyPath);
            var settings = JsonSerializer.Deserialize<LegacyLogRotationSettings>(json);
            if (settings != null && settings.ScheduleHours >= 0)
            {
                stateService.SetServiceInterval(ServiceKey, settings.ScheduleHours);
                _logger.LogInformation(
                    "Migrated legacy log-rotation-settings.json ({Hours}h) into state.json",
                    settings.ScheduleHours);
            }

            File.Delete(legacyPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to migrate legacy log-rotation-settings.json");
        }
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
                ScheduleHours = (int)EffectiveInterval.TotalHours,
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
/// Schema for the legacy log-rotation-settings.json file. Used only for one-time
/// migration into state.json — after migration this file is deleted and never
/// re-created. Do not reference outside the migration code path.
/// </summary>
internal class LegacyLogRotationSettings
{
    public int ScheduleHours { get; set; } = 24;
}
