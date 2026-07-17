using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Periodically removes completed and expired cache clearing operation records
/// that are older than 24 hours. This is a housekeeping service - actual cache
/// clearing is triggered manually from the Cache Management page.
/// </summary>
public class OperationHistoryCleanupService : ScheduledBackgroundService
{
    private readonly IStateService _stateService;
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _operationTracker;

    private const string StageBase = "signalr.scheduledRun.operationHistoryCleanup";
    private static readonly ScheduledRunEventNames _eventNames = new(
        SignalREvents.OperationHistoryCleanupStarted,
        SignalREvents.OperationHistoryCleanupProgress,
        SignalREvents.OperationHistoryCleanupComplete);

    protected override string ServiceName => "OperationHistoryCleanupService";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);
    public override bool DefaultRunOnStartup => false;
    public override string ServiceKey => "operationHistoryCleanup";
    protected override bool SupportsNotifications => true;

    // Routine background chore: scheduled runs stay quiet by default; manually triggered runs
    // still notify.
    protected override NotificationMode DefaultNotificationMode => NotificationMode.Manual;

    public OperationHistoryCleanupService(
        ILogger<OperationHistoryCleanupService> logger,
        IConfiguration configuration,
        IStateService stateService,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker operationTracker)
        : base(logger, configuration)
    {
        _stateService = stateService;
        _notifications = notifications;
        _operationTracker = operationTracker;
        LoadStateOverrides(stateService);
    }

    protected override Task OnStartupAsync(CancellationToken stoppingToken)
        => ExecuteWorkAsync(stoppingToken);

    protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
        => CleanupAsync(stoppingToken);

    private async Task CleanupAsync(CancellationToken stoppingToken)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);

        var stateOps = _stateService.GetCacheClearOperations().ToList();
        var toRemove = stateOps
            .Where(op => op.EndTime.HasValue && op.EndTime.Value < cutoff)
            .Select(op => op.Id)
            .ToList();

        // Prerequisite not met (nothing to remove): return before starting so no card surfaces.
        if (toRemove.Count == 0)
        {
            return;
        }

        var show = EffectiveNotificationMode.AllowsTrigger(CurrentRunTrigger);
        await using var reporter = new ScheduledRunReporter(
            _notifications,
            _operationTracker,
            ServiceKey,
            OperationType.OperationHistoryCleanup,
            _eventNames,
            $"{StageBase}.complete",
            show,
            stoppingToken);

        await reporter.StartAsync($"{StageBase}.starting", BuildContext(0, toRemove.Count));

        var reportStep = Math.Max(1, toRemove.Count / 20);
        for (var i = 0; i < toRemove.Count; i++)
        {
            _stateService.RemoveCacheClearOperation(toRemove[i]);

            var processed = i + 1;
            if (processed % reportStep == 0 || processed == toRemove.Count)
            {
                var percent = (double)processed / toRemove.Count * 100;
                await reporter.ReportAsync(percent, $"{StageBase}.running", BuildContext(processed, toRemove.Count));
            }
        }

        _logger.LogDebug("Cleaned up {Count} old cache clear operations from state", toRemove.Count);
        await reporter.CompleteAsync(success: true);
    }

    private static Dictionary<string, object?> BuildContext(int processed, int total) => new()
    {
        ["processed"] = processed,
        ["total"] = total
    };
}
