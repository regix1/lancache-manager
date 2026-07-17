using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Warms the IMemoryCache entry behind <c>/api/dashboard/batch</c> on startup and at a periodic
/// interval thereafter. Without this, the FIRST user request after a server restart would run
/// nine parallel DB queries against a cold connection pool - often 10s+ or a gateway timeout on
/// resource-constrained hosts. Running the compute in the background decouples user latency
/// from the pool warm-up.
/// </summary>
public class DashboardCacheWarmerService : ScheduledBackgroundService
{
    private readonly IDashboardBatchService _batchService;
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _operationTracker;

    private const string StageBase = "signalr.scheduledRun.dashboardCacheWarmer";
    private static readonly ScheduledRunEventNames _eventNames = new(
        SignalREvents.DashboardCacheWarmerStarted,
        SignalREvents.DashboardCacheWarmerProgress,
        SignalREvents.DashboardCacheWarmerComplete);

    public override string ServiceKey => "dashboardCacheWarmer";
    public override bool DefaultRunOnStartup => true;
    protected override bool SupportsNotifications => true;

    // Routine background chore: scheduled runs stay quiet by default; manually triggered runs
    // still notify.
    protected override NotificationMode DefaultNotificationMode => NotificationMode.Manual;

    protected override string ServiceName => "DashboardCacheWarmer";
    protected override TimeSpan StartupDelay => TimeSpan.FromSeconds(5);
    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    public DashboardCacheWarmerService(
        IDashboardBatchService batchService,
        IStateService stateService,
        IConfiguration configuration,
        ILogger<DashboardCacheWarmerService> logger,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker operationTracker)
        : base(logger, configuration)
    {
        _batchService = batchService;
        _notifications = notifications;
        _operationTracker = operationTracker;
        LoadStateOverrides(stateService);
    }

    protected override Task OnStartupAsync(CancellationToken stoppingToken)
        => WarmAsync(stoppingToken);

    protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
        => WarmAsync(stoppingToken);

    private async Task WarmAsync(CancellationToken stoppingToken)
    {
        var show = EffectiveNotificationMode.AllowsTrigger(CurrentRunTrigger);
        await using var reporter = new ScheduledRunReporter(
            _notifications,
            _operationTracker,
            ServiceKey,
            OperationType.DashboardCacheWarmer,
            _eventNames,
            $"{StageBase}.complete",
            show,
            stoppingToken);

        await reporter.StartAsync($"{StageBase}.starting");

        // Warming the dashboard batch is a single opaque compute, so progress is stepped rather than
        // per-key: announce the warm, run it, then complete at 100.
        await reporter.ReportAsync(25, $"{StageBase}.running");

        var started = DateTime.UtcNow;
        _ = await _batchService.GetBatchAsync(null, null, null, reporter.Token);
        var elapsed = DateTime.UtcNow - started;
        _logger.LogInformation(
            "Dashboard batch cache warmed (live view, no event filter) in {ElapsedMs} ms",
            (long)elapsed.TotalMilliseconds);

        await reporter.CompleteAsync(success: true);
    }
}
