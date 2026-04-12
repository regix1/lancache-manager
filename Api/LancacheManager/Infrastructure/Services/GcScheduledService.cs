using System.Diagnostics;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Scheduled service for garbage collection management. Runs on a user-configurable
/// interval (managed through the unified Schedules page via <see cref="ServiceScheduleRegistry"/>)
/// and calls <see cref="IMemoryManager.PerformAggressiveGarbageCollection"/> when the process
/// working set exceeds <see cref="GcSettings.MemoryThresholdMB"/>. Surfaces on the Schedules
/// page as the <c>performanceOptimization</c> card, but only when
/// <see cref="IsScheduleVisible"/> returns <c>true</c> — which tracks the user-controlled
/// <see cref="GcSettings.Enabled"/> toggle on the Performance Optimizations page.
/// </summary>
public class GcScheduledService : ConfigurableScheduledService, IConditionallyVisibleSchedule
{
    private static readonly TimeSpan _defaultInterval = TimeSpan.FromHours(1);

    private readonly SettingsService _settingsService;
    private readonly IMemoryManager _memoryManager;

    /// <summary>
    /// Stable service key used by <see cref="ServiceScheduleRegistry"/> (read via reflection).
    /// </summary>
    public string ScheduleServiceKey => "performanceOptimization";

    /// <summary>
    /// Log-friendly name surfaced by the base class in log lines.
    /// </summary>
    protected override string ServiceName => "Performance Optimizations";

    public GcScheduledService(
        ILogger<GcScheduledService> logger,
        SettingsService settingsService,
        IMemoryManager memoryManager,
        IStateService stateService)
        : base(logger, _defaultInterval)
    {
        _settingsService = settingsService;
        _memoryManager = memoryManager;

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

    protected override Task ExecuteScheduledWorkAsync(CancellationToken stoppingToken)
    {
        var settings = _settingsService.GetSettings();

        if (!settings.Enabled)
        {
            _logger.LogDebug("{ServiceName} skip: Enabled=false", ServiceName);
            return Task.CompletedTask;
        }

        var process = Process.GetCurrentProcess();
        var workingSetBytes = process.WorkingSet64;
        var thresholdBytes = settings.MemoryThresholdMB * 1024L * 1024L;
        var workingSetMB = workingSetBytes / (1024.0 * 1024.0);

        if (workingSetBytes <= thresholdBytes)
        {
            _logger.LogDebug(
                "{ServiceName} skip: working set {WorkingSetMB:F0}MB below threshold {ThresholdMB}MB",
                ServiceName,
                workingSetMB,
                settings.MemoryThresholdMB);
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "{ServiceName} GC triggered: working set {WorkingSetMB:F0}MB above threshold {ThresholdMB}MB",
            ServiceName,
            workingSetMB,
            settings.MemoryThresholdMB);

        // Platform-specific GC (Windows: GC.Collect + pool clearing; Linux: + malloc_trim).
        _memoryManager.PerformAggressiveGarbageCollection(_logger);

        process.Refresh();
        var afterGcMB = process.WorkingSet64 / (1024.0 * 1024.0);
        _logger.LogInformation(
            "{ServiceName} GC complete: before {BeforeMB:F0}MB, after {AfterMB:F0}MB (freed {FreedMB:F0}MB)",
            ServiceName,
            workingSetMB,
            afterGcMB,
            workingSetMB - afterGcMB);

        return Task.CompletedTask;
    }
}
