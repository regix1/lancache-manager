using LancacheManager.Infrastructure.Data;
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

    // Default: record snapshot every hour
    private readonly TimeSpan _snapshotInterval;

    // Keep snapshots for 90 days by default
    private readonly int _retentionDays;

    private int _snapshotCount;

    protected override string ServiceName => "CacheSnapshotService";
    protected override TimeSpan StartupDelay => TimeSpan.Zero; // Run immediately
    protected override TimeSpan Interval => _snapshotInterval;
    protected override bool RunOnStartup => true;
    protected override TimeSpan ErrorRetryDelay => TimeSpan.FromMinutes(5);

    public CacheSnapshotService(
        IServiceProvider serviceProvider,
        IServiceScopeFactory scopeFactory,
        CacheManagementService cacheService,
        ILogger<CacheSnapshotService> logger,
        IConfiguration configuration)
        : base(serviceProvider, logger, configuration)
    {
        _scopeFactory = scopeFactory;
        _cacheService = cacheService;

        _snapshotInterval = TimeSpan.FromMinutes(
            configuration.GetValue<int>("CacheSnapshots:IntervalMinutes", 60));
        _retentionDays = configuration.GetValue<int>("CacheSnapshots:RetentionDays", 90);

        Logger.LogInformation("Cache snapshot service initialized. Interval: {Interval}, Retention: {Retention} days",
            _snapshotInterval, _retentionDays);
    }

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        // Take an initial snapshot at startup
        await RecordSnapshotAsync();
    }

    protected override async Task ExecuteScopedWorkAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        await RecordSnapshotAsync();
        _snapshotCount++;

        // Cleanup old snapshots periodically (every 24 intervals)
        if (_snapshotCount % 24 == 0)
        {
            await CleanupOldSnapshotsAsync();
        }
    }

    private async Task RecordSnapshotAsync()
    {
        try
        {
            var cacheInfo = _cacheService.GetCacheInfo();

            // Skip if no valid data (e.g., on Windows development)
            if (cacheInfo.TotalCacheSize == 0 && cacheInfo.UsedCacheSize == 0)
            {
                Logger.LogDebug("Skipping cache snapshot - no cache info available");
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var snapshot = new CacheSnapshot
            {
                TimestampUtc = DateTime.UtcNow,
                UsedCacheSize = cacheInfo.UsedCacheSize,
                TotalCacheSize = cacheInfo.TotalCacheSize
            };

            dbContext.CacheSnapshots.Add(snapshot);
            await dbContext.SaveChangesAsync();

            Logger.LogDebug("Recorded cache snapshot: {UsedSize} / {TotalSize}",
                FormatBytes(cacheInfo.UsedCacheSize), FormatBytes(cacheInfo.TotalCacheSize));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to record cache snapshot");
        }
    }

    private async Task CleanupOldSnapshotsAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var cutoffDate = DateTime.UtcNow.AddDays(-_retentionDays);

            var deletedCount = await dbContext.CacheSnapshots
                .Where(s => s.TimestampUtc < cutoffDate)
                .ExecuteDeleteAsync();

            if (deletedCount > 0)
            {
                Logger.LogInformation("Cleaned up {Count} old cache snapshots (older than {Days} days)",
                    deletedCount, _retentionDays);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to cleanup old cache snapshots");
        }
    }

    /// <summary>
    /// Get the estimated cache size at a specific point in time.
    /// Returns the closest snapshot to the requested time.
    /// </summary>
    public async Task<CacheSnapshot?> GetSnapshotAtTimeAsync(DateTime timestampUtc)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Find the closest snapshot to the requested time
        // First try to find the snapshot just before the requested time
        var snapshot = await dbContext.CacheSnapshots
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
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var snapshots = await dbContext.CacheSnapshots
            .Where(s => s.TimestampUtc >= startUtc && s.TimestampUtc <= endUtc)
            .OrderBy(s => s.TimestampUtc)
            .ToListAsync();

        if (snapshots.Count == 0)
        {
            // Try to get the most recent snapshot before the start time
            var fallbackSnapshot = await dbContext.CacheSnapshots
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
