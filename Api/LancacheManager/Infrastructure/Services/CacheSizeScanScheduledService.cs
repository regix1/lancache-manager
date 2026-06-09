using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Scheduled service that walks the cache directory and refreshes the persisted
/// Rust cache-size scan used by the dashboard Cache Files stat and unmapped-cache totals.
/// </summary>
public class CacheSizeScanScheduledService : ScheduledBackgroundService
{
    private readonly CacheManagementService _cacheService;
    private readonly IPathResolver _pathResolver;
    private readonly ISignalRNotificationService _notifications;
    private readonly TimeSpan _defaultInterval;

    protected override string ServiceName => "CacheSizeScan";
    protected override TimeSpan StartupDelay => TimeSpan.Zero;
    protected override TimeSpan Interval => _defaultInterval;

    public override bool DefaultRunOnStartup => false;
    public override string ServiceKey => "cacheSizeScan";

    public CacheSizeScanScheduledService(
        CacheManagementService cacheService,
        IPathResolver pathResolver,
        ISignalRNotificationService notifications,
        IStateService stateService,
        ILogger<CacheSizeScanScheduledService> logger,
        IConfiguration configuration)
        : base(logger, configuration)
    {
        _cacheService = cacheService;
        _pathResolver = pathResolver;
        _notifications = notifications;
        _defaultInterval = TimeSpan.FromHours(configuration.GetValue("CacheSizeScan:IntervalHours", 24));
        LoadStateOverrides(stateService);
    }

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        await RunScanAsync("Startup", stoppingToken);
    }

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        await RunScanAsync("Scheduled", stoppingToken);
    }

    private async Task RunScanAsync(string trigger, CancellationToken stoppingToken)
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
            _logger.LogInformation("[CacheSizeScan] Starting cache file scan (trigger: {Trigger})", trigger);
            var result = await _cacheService.GetCachedCacheSizeAsync(force: true, datasource: null, cancellationToken: stoppingToken);
            if (result == null)
            {
                _logger.LogWarning("[CacheSizeScan] Cache file scan returned no result (trigger: {Trigger})", trigger);
                return;
            }

            _logger.LogInformation(
                "[CacheSizeScan] Completed: {FileCount} files, {TotalGb:F2} GB (trigger: {Trigger})",
                result.TotalFiles,
                result.TotalBytes / 1_073_741_824.0,
                trigger);
            await _notifications.NotifyAllAsync(SignalREvents.CacheScanComplete, new { success = true });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("[CacheSizeScan] Scan cancelled (trigger: {Trigger})", trigger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CacheSizeScan] Cache file scan failed (trigger: {Trigger})", trigger);
        }
    }
}
