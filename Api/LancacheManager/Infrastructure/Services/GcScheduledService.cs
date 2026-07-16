using System.Diagnostics;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Scheduled service for garbage collection management. Runs on a user-configurable
/// interval (managed through the unified Schedules page via <see cref="ServiceScheduleRegistry"/>)
/// and calls <see cref="IMemoryManager.CollectGarbage"/> when the process
/// working set exceeds <see cref="GcSettings.MemoryThresholdMB"/>. Surfaces on the Schedules
/// page as the <c>performanceOptimization</c> card, but only when
/// <see cref="IsScheduleVisible"/> returns <c>true</c> - which tracks the user-controlled
/// <see cref="GcSettings.Enabled"/> toggle on the Performance Optimizations page.
/// </summary>
public class GcScheduledService : ConfigurableScheduledService, IConditionallyVisibleSchedule
{
    private static readonly TimeSpan _defaultInterval = TimeSpan.FromHours(1);

    private readonly SettingsService _settingsService;
    private readonly IMemoryManager _memoryManager;
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _operationTracker;

    private const string StageBase = "signalr.scheduledRun.performanceOptimization";
    private static readonly ScheduledRunEventNames _eventNames = new(
        SignalREvents.PerformanceOptimizationStarted,
        SignalREvents.PerformanceOptimizationProgress,
        SignalREvents.PerformanceOptimizationComplete);

    /// <summary>
    /// Stable service key used by <see cref="ServiceScheduleRegistry"/> (read via reflection).
    /// </summary>
    public string ScheduleServiceKey => "performanceOptimization";

    /// <summary>
    /// Log-friendly name surfaced by the base class in log lines.
    /// </summary>
    protected override string ServiceName => "Performance Optimizations";

    protected override bool SupportsNotifications => true;

    public GcScheduledService(
        ILogger<GcScheduledService> logger,
        SettingsService settingsService,
        IMemoryManager memoryManager,
        IStateService stateService,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker operationTracker)
        : base(logger, _defaultInterval)
    {
        _settingsService = settingsService;
        _memoryManager = memoryManager;
        _notifications = notifications;
        _operationTracker = operationTracker;

        // Apply any user-saved interval / run-on-startup overrides from state.json before
        // the scheduling loop starts. Matches SteamKit2Service / EpicMappingService pattern.
        LoadStateOverrides(stateService, ScheduleServiceKey);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Visibility is driven purely by the user-controlled <see cref="GcSettings.Enabled"/>
    /// toggle. The master kill-switch (<c>Optimizations:EnableGarbageCollectionManagement</c>)
    /// still guards the controller endpoints via <c>RequiresGcManagementFilter</c>; when the
    /// switch is off the user cannot flip <see cref="GcSettings.Enabled"/> in the first place.
    /// </remarks>
    public bool IsScheduleVisible()
    {
        return _settingsService.GetSettings().Enabled;
    }

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        var settings = _settingsService.GetSettings();

        // Prerequisite not met (disabled): return before starting so no card surfaces.
        if (!settings.Enabled)
        {
            _logger.LogDebug("{ServiceName} skip: Enabled=false", ServiceName);
            return;
        }

        var process = Process.GetCurrentProcess();
        var workingSetBytes = process.WorkingSet64;
        var thresholdBytes = settings.MemoryThresholdMB * 1024L * 1024L;
        var workingSetMB = workingSetBytes / (1024.0 * 1024.0);

        // Below threshold: no GC is needed this run, so return before starting - a run that does no
        // work must not surface a card.
        if (workingSetBytes <= thresholdBytes)
        {
            _logger.LogDebug(
                "{ServiceName} skip: working set {WorkingSetMB:F0}MB below threshold {ThresholdMB}MB",
                ServiceName,
                workingSetMB,
                settings.MemoryThresholdMB);
            return;
        }

        var show = EffectiveNotificationMode.AllowsTrigger(CurrentRunTrigger);
        await using var reporter = new ScheduledRunReporter(
            _notifications,
            _operationTracker,
            ScheduleServiceKey,
            OperationType.PerformanceOptimization,
            _eventNames,
            $"{StageBase}.complete",
            show,
            stoppingToken);

        await reporter.StartAsync($"{StageBase}.starting");

        _logger.LogInformation(
            "{ServiceName} GC triggered: working set {WorkingSetMB:F0}MB above threshold {ThresholdMB}MB",
            ServiceName,
            workingSetMB,
            settings.MemoryThresholdMB);

        // Collecting garbage is a single synchronous action, so progress is stepped: announce it,
        // run it, then complete at 100.
        await reporter.ReportAsync(50, $"{StageBase}.running");

        // Platform-specific GC (Windows: GC.Collect + pool clearing; Linux: + malloc_trim).
        _memoryManager.CollectGarbage(_logger);

        process.Refresh();
        var afterGcMB = process.WorkingSet64 / (1024.0 * 1024.0);
        _logger.LogInformation(
            "{ServiceName} GC complete: before {BeforeMB:F0}MB, after {AfterMB:F0}MB (freed {FreedMB:F0}MB)",
            ServiceName,
            workingSetMB,
            afterGcMB,
            workingSetMB - afterGcMB);

        await reporter.CompleteAsync(success: true);
    }
}
