using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Hosted service that runs nginx log rotation at startup and on a configurable schedule.
/// Uses the base class ScheduledBackgroundService loop - ExecuteWorkAsync performs one rotation
/// and returns; the base class handles the sleep/interval between runs.
/// </summary>
public class NginxLogRotationHostedService : ScheduledBackgroundService
{
    private readonly NginxLogRotationService _rotationService;
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly OperationStateService _operationStateService;

    private const string StageBase = "signalr.scheduledRun.logRotation";
    private static readonly ScheduledRunEventNames _eventNames = new(
        SignalREvents.LogRotationStarted,
        SignalREvents.LogRotationProgress,
        SignalREvents.LogRotationComplete);

    // Default interval pulled from configuration on construction. Runtime overrides
    // (Schedules UI) come from state.json via the base class LoadStateOverrides helper.
    private readonly TimeSpan _defaultInterval;

    protected override string ServiceName => "NginxLogRotation";
    protected override TimeSpan StartupDelay => TimeSpan.Zero;
    protected override TimeSpan Interval => _defaultInterval;

    public override bool DefaultRunOnStartup => false;
    public override string ServiceKey => "logRotation";
    protected override bool SupportsNotifications => true;

    // Routine background chore: scheduled runs stay quiet by default; manually triggered runs
    // still notify.
    protected override NotificationMode DefaultNotificationMode => NotificationMode.Manual;

    public NginxLogRotationHostedService(
        NginxLogRotationService rotationService,
        IConfiguration configuration,
        ILogger<NginxLogRotationHostedService> logger,
        IPathResolver pathResolver,
        IStateService stateService,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker operationTracker,
        OperationStateService operationStateService)
        : base(logger, configuration)
    {
        _rotationService = rotationService;
        _notifications = notifications;
        _operationTracker = operationTracker;
        _operationStateService = operationStateService;

        var configHours = configuration.GetValue<int>("NginxLogRotation:ScheduleHours", 24);
        _defaultInterval = TimeSpan.FromHours(configHours);

        // One-time migration: copy any legacy log-rotation-settings.json value into state.json,
        // then delete the file. After this runs, state.json is the sole source of truth and
        // all schedule changes flow through the Schedules UI.
        MigrateLegacySettings(pathResolver, stateService);

        // Apply persisted overrides (interval + run-on-startup) from state.json
        LoadStateOverrides(stateService);
    }

    private void MigrateLegacySettings(IPathResolver pathResolver, IStateService stateService)
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
                // state.json already holds the canonical value - the legacy file is stale
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

    protected override bool IsEnabled()
        => _configuration.GetValue<bool>("NginxLogRotation:Enabled", false);

    /// <summary>
    /// Runs once at startup via the base class OnStartupAsync mechanism.
    /// </summary>
    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Running nginx log rotation at startup...");
        await RotateAsync("Startup", stoppingToken);
    }

    /// <summary>
    /// Performs a single rotation cycle and returns.
    /// The base class loop handles the sleep/interval between runs.
    /// </summary>
    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        await RotateAsync("Scheduled", stoppingToken);
    }

    private async Task RotateAsync(string trigger, CancellationToken stoppingToken)
    {
        await using var reporter = new ScheduledRunReporter(
            _notifications,
            _operationTracker,
            ServiceKey,
            OperationType.LogRotation,
            _eventNames,
            $"{StageBase}.complete",
            CurrentRunNotice,
            stoppingToken);

        await reporter.StartAsync($"{StageBase}.starting");

        // Reopening the nginx logs is a single atomic action, so progress is stepped: announce it,
        // run it, then complete from the rotation result.
        await reporter.ReportAsync(50, $"{StageBase}.running");

        // A step that deletes or rewrites a log holds the log lock, and a reopen inside it makes nginx
        // recreate a file the step just deleted, so the reopen waits for the step and keeps the next one
        // out. It changes no file, row or position, so the import and the speed tracker keep running.
        LogRotationResult result;
        try
        {
            await using (await _operationStateService.LockLogFilesAsync(
                reporter.OperationId,
                OperationType.LogRotation,
                LogFileLockKind.Reopen,
                reporter.Token))
            {
                result = await _rotationService.ReopenNginxLogsAsync(reporter.Token);
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Canceled from its card while it waited or while it signaled: disposing the reporter ends the card as canceled.
            return;
        }

        if (result.Success)
        {
            _logger.LogInformation("Log rotation completed successfully (trigger: {Trigger})", trigger);
            if (result.ErrorMessage is { } failedWriters)
            {
                reporter.SetWarning(new RunWarning(
                    "common.notifications.warnings.logReopenPartlyFailed",
                    new Dictionary<string, object?> { ["errors"] = failedWriters }));
            }

            await reporter.CompleteAsync(success: true);
        }
        else
        {
            _logger.LogWarning("Log rotation failed (trigger: {Trigger}): {Error}", trigger, result.ErrorMessage);
            await reporter.CompleteAsync(success: false, error: result.ErrorMessage);
        }
    }
}
