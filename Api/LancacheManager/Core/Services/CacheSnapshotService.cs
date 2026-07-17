using LancacheManager.Core.Interfaces;
using LancacheManager.Extensions;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using static LancacheManager.Infrastructure.Utilities.FormattingUtils;

namespace LancacheManager.Core.Services;

/// <summary>
/// Background service that periodically records cache size snapshots.
/// This enables showing historical used space data for past time periods.
/// </summary>
public class CacheSnapshotService : ScopedScheduledBackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CacheManagementService _cacheService;
    private readonly StateService _stateService;
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _operationTracker;

    private const string StageBase = "signalr.scheduledRun.cacheSnapshot";
    private static readonly ScheduledRunEventNames _eventNames = new(
        SignalREvents.CacheSnapshotStarted,
        SignalREvents.CacheSnapshotProgress,
        SignalREvents.CacheSnapshotComplete);

    // Default: record snapshot every hour
    private readonly TimeSpan _snapshotInterval;

    // Keep snapshots for 90 days by default
    private readonly int _retentionDays;

    private int _snapshotCount;

    protected override string ServiceName => "CacheSnapshotService";
    protected override TimeSpan StartupDelay => TimeSpan.Zero; // Run immediately
    protected override TimeSpan Interval => _snapshotInterval;
    public override bool DefaultRunOnStartup => false;
    protected override TimeSpan ErrorRetryDelay => TimeSpan.FromMinutes(5);
    protected override bool SupportsNotifications => true;

    // Routine background chore: scheduled runs stay quiet by default; manually triggered runs
    // still notify.
    protected override NotificationMode DefaultNotificationMode => NotificationMode.Manual;

    public override string ServiceKey => "cacheSnapshot";

    public CacheSnapshotService(
        IServiceProvider serviceProvider,
        IServiceScopeFactory scopeFactory,
        CacheManagementService cacheService,
        StateService stateService,
        ILogger<CacheSnapshotService> logger,
        IConfiguration configuration,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker operationTracker)
        : base(serviceProvider, logger, configuration)
    {
        _scopeFactory = scopeFactory;
        _cacheService = cacheService;
        _stateService = stateService;
        _notifications = notifications;
        _operationTracker = operationTracker;

        _snapshotInterval = TimeSpan.FromMinutes(
            configuration.GetValue<int>("CacheSnapshots:IntervalMinutes", 60));
        _retentionDays = configuration.GetValue<int>("CacheSnapshots:RetentionDays", 90);

        _logger.LogInformation("Cache snapshot service initialized. Interval: {Interval}, Retention: {Retention} days",
            _snapshotInterval, _retentionDays);

        LoadStateOverrides(stateService);
    }

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        // Wait for setup to complete so datasources and database are configured
        await _stateService.WaitForSetupCompletedAsync(stoppingToken);

        // Take an initial snapshot at startup
        await RecordSnapshotAsync(stoppingToken);

        // GetCacheInfoAsync above loads any persisted cache-size scan into memory. It deliberately
        // does not start a fresh full-disk walk; automatic scans belong to the dedicated schedule.
    }

    protected override async Task ExecuteWorkAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        await RecordSnapshotAsync(stoppingToken);
        _snapshotCount++;

        // Cleanup old snapshots periodically (every 24 intervals)
        if (_snapshotCount % 24 == 0)
        {
            await CleanupOldSnapshotsAsync();
        }
    }

    private async Task RecordSnapshotAsync(CancellationToken stoppingToken)
    {
        var cacheInfo = await _cacheService.GetCacheInfoAsync();

        // Skip if no valid data (e.g., on Windows development). Return before starting so a run with
        // nothing to record never surfaces a card.
        if (cacheInfo.TotalCacheSize == 0 && cacheInfo.UsedCacheSize == 0)
        {
            _logger.LogDebug("Skipping cache snapshot - no cache info available");
            return;
        }

        var show = EffectiveNotificationMode.AllowsTrigger(CurrentRunTrigger);
        await using var reporter = new ScheduledRunReporter(
            _notifications,
            _operationTracker,
            ServiceKey,
            OperationType.CacheSnapshot,
            _eventNames,
            $"{StageBase}.complete",
            show,
            stoppingToken);

        await reporter.StartAsync($"{StageBase}.starting");

        // Writing one snapshot row is a single atomic action, so progress is stepped.
        await reporter.ReportAsync(50, $"{StageBase}.running");

        using var scopedDb = _scopeFactory.CreateScopedDbContext();

        var snapshot = new CacheSnapshot
        {
            TimestampUtc = DateTime.UtcNow,
            UsedCacheSize = cacheInfo.UsedCacheSize,
            TotalCacheSize = cacheInfo.TotalCacheSize
        };

        scopedDb.DbContext.CacheSnapshots.Add(snapshot);
        await scopedDb.DbContext.SaveChangesAsync(stoppingToken);

        _logger.LogDebug("Recorded cache snapshot: {UsedSize} / {TotalSize}",
            FormatBytes(cacheInfo.UsedCacheSize), FormatBytes(cacheInfo.TotalCacheSize));

        await reporter.CompleteAsync(success: true);
    }

    private async Task CleanupOldSnapshotsAsync()
    {
        try
        {
            using var scopedDb = _scopeFactory.CreateScopedDbContext();

            var cutoffDate = DateTime.UtcNow.AddDays(-_retentionDays);

            var deletedCount = await scopedDb.DbContext.CacheSnapshots
                .Where(s => s.TimestampUtc < cutoffDate)
                .ExecuteDeleteAsync();

            if (deletedCount > 0)
            {
                _logger.LogInformation("Cleaned up {Count} old cache snapshots (older than {Days} days)",
                    deletedCount, _retentionDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup old cache snapshots");
        }
    }

    /// <summary>
    /// Get the estimated cache size at a specific point in time.
    /// Returns the closest snapshot to the requested time.
    /// </summary>
    public async Task<CacheSnapshot?> GetSnapshotAtTimeAsync(DateTime timestampUtc)
    {
        using var scopedDb = _scopeFactory.CreateScopedDbContext();

        // Find the closest snapshot to the requested time
        // First try to find the snapshot just before the requested time
        var snapshot = await scopedDb.DbContext.CacheSnapshots
            .AsNoTracking()
            .Where(s => s.TimestampUtc <= timestampUtc)
            .OrderByDescending(s => s.TimestampUtc)
            .FirstOrDefaultAsync();

        return snapshot;
    }

    /// <summary>
    /// Get the average cache size during a time range.
    /// </summary>
    public async Task<CacheSnapshotSummary?> GetSnapshotSummaryAsync(DateTime startUtc, DateTime endUtc)
    {
        using var scopedDb = _scopeFactory.CreateScopedDbContext();

        var snapshots = await scopedDb.DbContext.CacheSnapshots
            .AsNoTracking()
            .Where(s => s.TimestampUtc >= startUtc && s.TimestampUtc <= endUtc)
            .OrderBy(s => s.TimestampUtc)
            .ToListAsync();

        if (snapshots.Count == 0)
        {
            // Try to get the most recent snapshot before the start time
            var fallbackSnapshot = await scopedDb.DbContext.CacheSnapshots
                .AsNoTracking()
                .Where(s => s.TimestampUtc < startUtc)
                .OrderByDescending(s => s.TimestampUtc)
                .FirstOrDefaultAsync();

            if (fallbackSnapshot != null)
            {
                return new CacheSnapshotSummary
                {
                    StartUsedSize = fallbackSnapshot.UsedCacheSize,
                    EndUsedSize = fallbackSnapshot.UsedCacheSize,
                    AverageUsedSize = fallbackSnapshot.UsedCacheSize,
                    TotalCacheSize = fallbackSnapshot.TotalCacheSize,
                    SnapshotCount = 1,
                    IsEstimate = true
                };
            }

            return null;
        }

        var firstSnapshot = snapshots.First();
        var lastSnapshot = snapshots.Last();
        var avgUsedSize = (long)snapshots.Average(s => s.UsedCacheSize);
        var totalSize = lastSnapshot.TotalCacheSize;

        return new CacheSnapshotSummary
        {
            StartUsedSize = firstSnapshot.UsedCacheSize,
            EndUsedSize = lastSnapshot.UsedCacheSize,
            AverageUsedSize = avgUsedSize,
            TotalCacheSize = totalSize,
            SnapshotCount = snapshots.Count,
            IsEstimate = snapshots.Count < 2
        };
    }

}

/// <summary>
/// Summary of cache snapshots for a time range.
/// </summary>
public class CacheSnapshotSummary
{
    public long StartUsedSize { get; set; }
    public long EndUsedSize { get; set; }
    public long AverageUsedSize { get; set; }
    public long TotalCacheSize { get; set; }
    public int SnapshotCount { get; set; }
    public bool IsEstimate { get; set; }
}
