using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Scheduled service that walks the cache directory and refreshes the persisted
/// Rust cache-size scan used by the dashboard Cache Files stat and unmapped-cache totals.
/// </summary>
public class CacheSizeScanScheduledService : ScheduledBackgroundService
{
    private readonly CacheManagementService _cacheService;
    private readonly IPathResolver _pathResolver;
    private readonly IOperationQueue _operationQueue;
    private readonly TimeSpan _defaultInterval;

    protected override string ServiceName => "CacheSizeScan";
    protected override TimeSpan StartupDelay => TimeSpan.Zero;
    protected override TimeSpan Interval => _defaultInterval;

    // The tracked scan already emits Started/Progress/Complete lifecycle events; opting in lets the
    // Schedules UI expose the Notifications control and lets a run be gated to silent per the mode.
    protected override bool SupportsNotifications => true;

    public override bool DefaultRunOnStartup => false;
    public override string ServiceKey => "cacheSizeScan";

    public CacheSizeScanScheduledService(
        CacheManagementService cacheService,
        IPathResolver pathResolver,
        IOperationQueue operationQueue,
        IStateService stateService,
        ILogger<CacheSizeScanScheduledService> logger,
        IConfiguration configuration)
        : base(logger, configuration)
    {
        _cacheService = cacheService;
        _pathResolver = pathResolver;
        _operationQueue = operationQueue;
        _defaultInterval = TimeSpan.FromHours(configuration.GetValue("CacheSizeScan:IntervalHours", 24));
        LoadStateOverrides(stateService);
    }

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        await ScanAsync("Startup", stoppingToken);
    }

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        await ScanAsync("Scheduled", stoppingToken);
    }

    private async Task ScanAsync(string trigger, CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();

        var rustBinaryPath = _pathResolver.GetRustCacheSizePath();
        if (!File.Exists(rustBinaryPath))
        {
            _logger.LogWarning(
                "[CacheSizeScan] Rust cache-size binary not found at {Path} - scan skipped",
                rustBinaryPath);
            return;
        }

        try
        {
            // Stamp the run-stable display flag from the effective mode + this run's trigger. The
            // lifecycle events are always emitted (recovery/state stay accurate); the frontend gates
            // whether the card is shown.
            var showNotification = EffectiveNotificationMode.AllowsTrigger(CurrentRunTrigger);

            Task<Guid?> StartScanAsync() => _cacheService.StartCacheSizeScanInBackgroundAsync(showNotification);

            var outcome = await _operationQueue.EnqueueAsync(
                OperationType.CacheSizeScan,
                ConflictScope.Bulk(),
                "Cache File Scan",
                StartScanAsync,
                stoppingToken);

            if (outcome.Queued)
            {
                _logger.LogInformation(
                    "[CacheSizeScan] Cache file scan queued (trigger: {Trigger}, waiting operation: {OperationId})",
                    trigger,
                    outcome.OperationId);
            }
            else if (outcome.AlreadyRunning)
            {
                _logger.LogInformation(
                    "[CacheSizeScan] Cache file scan already requested (trigger: {Trigger}, operation: {OperationId})",
                    trigger,
                    outcome.OperationId);
            }
            else
            {
                _logger.LogInformation(
                    "[CacheSizeScan] Cache file scan started (trigger: {Trigger}, operation: {OperationId})",
                    trigger,
                    outcome.OperationId);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("[CacheSizeScan] Scan request cancelled (trigger: {Trigger})", trigger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CacheSizeScan] Cache file scan failed (trigger: {Trigger})", trigger);
        }
    }
}
