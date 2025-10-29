using System.Diagnostics.Metrics;
using LancacheManager.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Application.Services;

/// <summary>
/// Service for collecting and exposing LanCache-specific metrics to Prometheus/Grafana
/// Uses OpenTelemetry Metrics API for instrumentation
/// </summary>
public class LancacheMetricsService : BackgroundService
{
    private readonly Meter _meter;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LancacheMetricsService> _logger;

    // Observables for gauges (values that can go up and down)
    private int _activeDownloads;
    private int _activeClients;
    private long _totalDownloads;
    private long _totalBytesServed;

    // New metrics for cache effectiveness
    private long _cacheCapacityBytes;
    private long _cacheUsedBytes;
    private long _cacheFreeBytes;
    private long _cacheUsageRatioBits; // Store as long bits for thread-safety
    private long _cacheHitBytesTotal;
    private long _cacheMissBytesTotal;
    private long _cacheHitRatioBits; // Store as long bits for thread-safety
    private long _averageDownloadSizeBytes;

    public LancacheMetricsService(IServiceScopeFactory scopeFactory, ILogger<LancacheMetricsService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        _logger.LogInformation("Initializing LancacheMetricsService");

        // Create a meter for LanCache metrics
        _meter = new Meter("LancacheManager", "1.0.0");

        // Observable Gauges - current state values
        _meter.CreateObservableGauge(
            "lancache_cache_size_bytes",
            () => _cacheUsedBytes,
            unit: "bytes",
            description: "Current cache used size in bytes"
        );

        _meter.CreateObservableGauge(
            "lancache_active_downloads",
            () => _activeDownloads,
            unit: "downloads",
            description: "Number of currently active downloads"
        );

        _meter.CreateObservableGauge(
            "lancache_active_clients",
            () => _activeClients,
            unit: "clients",
            description: "Number of unique active client IPs"
        );

        _meter.CreateObservableGauge(
            "lancache_total_downloads",
            () => _totalDownloads,
            unit: "downloads",
            description: "Total downloads in database"
        );

        _meter.CreateObservableGauge(
            "lancache_total_bytes_served",
            () => _totalBytesServed,
            unit: "bytes",
            description: "Total bytes served (all time)"
        );

        // New observable gauges for cache effectiveness
        _meter.CreateObservableGauge(
            "lancache_cache_capacity_bytes",
            () => _cacheCapacityBytes,
            unit: "bytes",
            description: "Total cache storage capacity"
        );

        _meter.CreateObservableGauge(
            "lancache_cache_usage_ratio",
            () => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _cacheUsageRatioBits)),
            unit: "ratio",
            description: "Cache usage as ratio (0-1)"
        );

        _meter.CreateObservableGauge(
            "lancache_cache_hit_bytes_total",
            () => _cacheHitBytesTotal,
            unit: "bytes",
            description: "Total cache hit bytes (bandwidth saved)"
        );

        _meter.CreateObservableGauge(
            "lancache_cache_miss_bytes_total",
            () => _cacheMissBytesTotal,
            unit: "bytes",
            description: "Total cache miss bytes (added to cache)"
        );

        _meter.CreateObservableGauge(
            "lancache_cache_hit_ratio",
            () => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _cacheHitRatioBits)),
            unit: "ratio",
            description: "Cache hit ratio (0-1)"
        );

        _meter.CreateObservableGauge(
            "lancache_cache_free_bytes",
            () => _cacheFreeBytes,
            unit: "bytes",
            description: "Cache free space in bytes"
        );

        _meter.CreateObservableGauge(
            "lancache_average_download_size_bytes",
            () => _averageDownloadSizeBytes,
            unit: "bytes",
            description: "Average download size in bytes"
        );

        _logger.LogInformation("LancacheMetricsService initialization complete");
    }

    /// <summary>
    /// Background task implementation to periodically update gauge values from database
    /// Runs every 30 seconds to keep metrics fresh with proper cancellation support
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LancacheMetricsService background task started");

        // Wait for app to initialize
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        // Update metrics immediately on first run, then every 30 seconds
        bool isFirstRun = true;
        int updateCount = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!isFirstRun)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
                isFirstRun = false;
                updateCount++;

                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cacheService = scope.ServiceProvider.GetRequiredService<CacheManagementService>();

                // Get total downloads count
                var totalDownloads = await context.Downloads.CountAsync();
                Interlocked.Exchange(ref _totalDownloads, totalDownloads);

                // Get total bytes served
                var totalBytes = await context.Downloads.SumAsync(d => (long?)(d.CacheHitBytes + d.CacheMissBytes)) ?? 0;
                Interlocked.Exchange(ref _totalBytesServed, totalBytes);

                // Get active downloads (in last 5 minutes)
                var fiveMinutesAgo = DateTime.UtcNow.AddMinutes(-5);
                var activeCount = await context.Downloads
                    .Where(d => d.EndTimeUtc >= fiveMinutesAgo)
                    .CountAsync();
                Interlocked.Exchange(ref _activeDownloads, activeCount);

                // Get unique client IPs (in last hour)
                var oneHourAgo = DateTime.UtcNow.AddHours(-1);
                var activeClientsCount = await context.Downloads
                    .Where(d => d.EndTimeUtc >= oneHourAgo)
                    .Select(d => d.ClientIp)
                    .Distinct()
                    .CountAsync();
                Interlocked.Exchange(ref _activeClients, activeClientsCount);

                // Get cache info for capacity and usage (wrap in try-catch to prevent metrics loop from failing)
                CacheInfo cacheInfo = new CacheInfo();
                try
                {
                    cacheInfo = cacheService.GetCacheInfo();
                    Interlocked.Exchange(ref _cacheCapacityBytes, cacheInfo.TotalCacheSize);
                    Interlocked.Exchange(ref _cacheUsedBytes, cacheInfo.UsedCacheSize);
                    Interlocked.Exchange(ref _cacheFreeBytes, cacheInfo.FreeCacheSize);

                    // Calculate usage ratio (0-1, following Prometheus conventions)
                    var usageRatio = cacheInfo.TotalCacheSize > 0
                        ? (double)cacheInfo.UsedCacheSize / cacheInfo.TotalCacheSize
                        : 0;
                    Interlocked.Exchange(ref _cacheUsageRatioBits, BitConverter.DoubleToInt64Bits(usageRatio));
                }
                catch (Exception cacheEx)
                {
                    // Log but don't fail the entire metrics update if cache info fails
                    // This can happen in Docker/containerized environments with unusual mount setups
                    if (updateCount % 20 == 0) // Only log every 10 minutes to avoid spam
                    {
                        _logger.LogWarning(cacheEx, "Failed to get cache info for metrics");
                    }
                }

                // Get cache effectiveness metrics
                var totalHitBytes = await context.Downloads.SumAsync(d => (long?)d.CacheHitBytes) ?? 0;
                var totalMissBytes = await context.Downloads.SumAsync(d => (long?)d.CacheMissBytes) ?? 0;
                Interlocked.Exchange(ref _cacheHitBytesTotal, totalHitBytes);
                Interlocked.Exchange(ref _cacheMissBytesTotal, totalMissBytes);

                // Calculate hit ratio (0-1, following Prometheus conventions)
                var totalBytesForRatio = totalHitBytes + totalMissBytes;
                var hitRatio = totalBytesForRatio > 0
                    ? (double)totalHitBytes / totalBytesForRatio
                    : 0;
                Interlocked.Exchange(ref _cacheHitRatioBits, BitConverter.DoubleToInt64Bits(hitRatio));

                // Calculate average download size
                var avgDownloadSize = totalDownloads > 0
                    ? totalBytes / totalDownloads
                    : 0;
                Interlocked.Exchange(ref _averageDownloadSizeBytes, avgDownloadSize);

                // Only log every 20 updates (every 10 minutes) to reduce log noise
                if (updateCount % 20 == 0)
                {
                    _logger.LogInformation("Metrics updated - Downloads: {Downloads}, TotalBytes: {Bytes}, ActiveDownloads: {Active}, CacheSize: {CacheSize}",
                        totalDownloads, totalBytes, activeCount, cacheInfo.UsedCacheSize);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected during shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update gauge metrics - this will cause /metrics to return empty");
            }
        }

        _logger.LogInformation("LancacheMetricsService background task stopped");
    }
}
