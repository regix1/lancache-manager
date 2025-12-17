using LancacheManager.Application.DTOs;
using LancacheManager.Data;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for statistics and analytics
/// Handles client stats, service stats, and dashboard metrics
/// </summary>
[ApiController]
[Route("api/stats")]
public class StatsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly StatsRepository _statsService;
    private readonly ILogger<StatsController> _logger;

    public StatsController(AppDbContext context, StatsRepository statsService, ILogger<StatsController> logger)
    {
        _context = context;
        _statsService = statsService;
        _logger = logger;
    }

    [HttpGet("clients")]
    public async Task<IActionResult> GetClients([FromQuery] long? startTime = null, [FromQuery] long? endTime = null)
    {
        try
        {
            List<ClientStats> stats;

            // If no filtering, use cached service method
            if (!startTime.HasValue && !endTime.HasValue)
            {
                stats = await _statsService.GetClientStatsAsync();
                // Take top 100 after caching
                stats = stats.Take(100).ToList();
            }
            else
            {
                // With filtering, query database directly
                // Database stores dates in UTC, so filter with UTC
                var startDate = startTime.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime
                    : DateTime.MinValue;
                var endDate = endTime.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime
                    : DateTime.UtcNow;

                stats = await _context.ClientStats
                    .AsNoTracking()
                    .Where(c => c.LastActivityUtc >= startDate && c.LastActivityUtc <= endDate)
                    .OrderByDescending(c => c.TotalCacheHitBytes + c.TotalCacheMissBytes)
                    .Take(100)
                    .ToListAsync();

                // Fix timezone: Ensure UTC DateTime values are marked as UTC for proper JSON serialization
                foreach (var stat in stats)
                {
                    stat.LastActivityUtc = DateTime.SpecifyKind(stat.LastActivityUtc, DateTimeKind.Utc);
                }
            }

            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting client stats");
            return Ok(new List<ClientStats>());
        }
    }

    [HttpGet("services")]
    public async Task<IActionResult> GetServices([FromQuery] string? since = null, [FromQuery] long? startTime = null, [FromQuery] long? endTime = null)
    {
        try
        {
            // If no filtering, use cached service method
            if (!startTime.HasValue && !endTime.HasValue && (string.IsNullOrEmpty(since) || since == "all"))
            {
                var cachedStats = await _statsService.GetServiceStatsAsync();
                return Ok(cachedStats);
            }

            // With filtering, calculate from Downloads table for accurate time-sliced stats
            DateTime startDate;
            DateTime endDate;

            if (startTime.HasValue || endTime.HasValue)
            {
                // Use provided Unix timestamps
                startDate = startTime.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime
                    : DateTime.MinValue;
                endDate = endTime.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime
                    : DateTime.UtcNow;
            }
            else if (!string.IsNullOrEmpty(since) && since != "all")
            {
                // Parse time period string
                var cutoffTime = ParseTimePeriod(since);
                startDate = cutoffTime ?? DateTime.UtcNow.AddHours(-24);
                endDate = DateTime.UtcNow;
            }
            else
            {
                // Fallback
                startDate = DateTime.MinValue;
                endDate = DateTime.UtcNow;
            }

            // Query downloads within the time range and aggregate by service
            var serviceStats = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.StartTimeUtc >= startDate && d.StartTimeUtc <= endDate)
                .GroupBy(d => d.Service)
                .Select(g => new ServiceStats
                {
                    Service = g.Key,
                    TotalCacheHitBytes = g.Sum(d => d.CacheHitBytes),
                    TotalCacheMissBytes = g.Sum(d => d.CacheMissBytes),
                    // TotalBytes and CacheHitPercent are computed properties
                    TotalDownloads = g.Count(),
                    LastActivityUtc = g.Max(d => d.StartTimeUtc),
                    LastActivityLocal = g.Max(d => d.StartTimeLocal)
                })
                .OrderByDescending(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes)
                .ToListAsync();

            // Fix timezone: Ensure UTC DateTime values are marked as UTC for proper JSON serialization
            foreach (var stat in serviceStats)
            {
                stat.LastActivityUtc = DateTime.SpecifyKind(stat.LastActivityUtc, DateTimeKind.Utc);
            }

            return Ok(serviceStats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting service stats");
            return Ok(new List<ServiceStats>());
        }
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboardStats([FromQuery] string period = "24h")
    {
        // Parse the time period - handle "all" specially
        DateTime? cutoffTime = null;
        if (period != "all")
        {
            cutoffTime = ParseTimePeriod(period) ?? DateTime.UtcNow.AddHours(-24);
        }

        // Get all service stats (these are all-time totals) - USE CACHE
        var serviceStats = await _statsService.GetServiceStatsAsync();

        // Calculate all-time metrics from ServiceStats (these are cumulative totals)
        var totalBandwidthSaved = serviceStats.Sum(s => s.TotalCacheHitBytes);
        var totalAddedToCache = serviceStats.Sum(s => s.TotalCacheMissBytes);
        var totalServed = totalBandwidthSaved + totalAddedToCache;
        var cacheHitRatio = totalServed > 0
            ? (double)totalBandwidthSaved / totalServed
            : 0;

        // Get top service
        var topService = serviceStats
            .OrderByDescending(s => s.TotalBytes)
            .FirstOrDefault()?.Service ?? "none";

        // DATABASE-SIDE AGGREGATION: Calculate period metrics without loading all records
        var downloadsQuery = _context.Downloads.AsNoTracking();
        if (cutoffTime.HasValue)
        {
            downloadsQuery = downloadsQuery.Where(d => d.StartTimeUtc >= cutoffTime.Value);
        }

        // Run aggregates in parallel for better performance
        var periodHitBytesTask = downloadsQuery.SumAsync(d => (long?)d.CacheHitBytes);
        var periodMissBytesTask = downloadsQuery.SumAsync(d => (long?)d.CacheMissBytes);
        var periodDownloadCountTask = downloadsQuery.CountAsync();
        var activeDownloadsTask = _context.Downloads
            .AsNoTracking()
            .Where(d => d.IsActive && d.EndTimeUtc > DateTime.UtcNow.AddMinutes(-5))
            .CountAsync();
        var uniqueClientsCountTask = cutoffTime.HasValue
            ? _context.ClientStats
                .AsNoTracking()
                .Where(c => c.LastActivityUtc >= cutoffTime.Value)
                .CountAsync()
            : _context.ClientStats.AsNoTracking().CountAsync();

        // Await all tasks in parallel
        await Task.WhenAll(periodHitBytesTask, periodMissBytesTask, periodDownloadCountTask, activeDownloadsTask, uniqueClientsCountTask);

        var periodHitBytes = periodHitBytesTask.Result ?? 0L;
        var periodMissBytes = periodMissBytesTask.Result ?? 0L;
        var periodDownloadCount = periodDownloadCountTask.Result;
        var activeDownloads = activeDownloadsTask.Result;
        var uniqueClientsCount = uniqueClientsCountTask.Result;

        var periodTotal = periodHitBytes + periodMissBytes;
        var periodHitRatio = periodTotal > 0
            ? (double)periodHitBytes / periodTotal
            : 0;

        // For "all" period, period metrics should equal all-time metrics
        if (period == "all")
        {
            periodHitBytes = totalBandwidthSaved;
            periodMissBytes = totalAddedToCache;
            periodTotal = totalServed;
            periodHitRatio = cacheHitRatio;
        }

        return Ok(new DashboardStatsResponse
        {
            // All-time metrics (always from ServiceStats totals)
            TotalBandwidthSaved = totalBandwidthSaved,
            TotalAddedToCache = totalAddedToCache,
            TotalServed = totalServed,
            CacheHitRatio = cacheHitRatio,

            // Current status
            ActiveDownloads = activeDownloads,
            UniqueClients = uniqueClientsCount,
            TopService = topService,

            // Period-specific metrics
            Period = new DashboardPeriodStats
            {
                Duration = period,
                Since = cutoffTime,
                BandwidthSaved = periodHitBytes,
                AddedToCache = periodMissBytes,
                TotalServed = periodTotal,
                HitRatio = periodHitRatio,
                Downloads = periodDownloadCount
            },

            // Service breakdown (always all-time for consistency)
            ServiceBreakdown = serviceStats
                .Select(s => new ServiceBreakdownItem
                {
                    Service = s.Service,
                    Bytes = s.TotalBytes,
                    Percentage = totalServed > 0
                        ? (s.TotalBytes * 100.0) / totalServed
                        : 0
                })
                .OrderByDescending(s => s.Bytes)
                .ToList(),

            LastUpdated = DateTime.UtcNow
        });
    }


    /// <summary>
    /// Get hourly activity data for peak usage hours widget
    /// Groups downloads by hour of day to show activity patterns
    /// </summary>
    [HttpGet("hourly-activity")]
    public async Task<IActionResult> GetHourlyActivity([FromQuery] string period = "7d")
    {
        try
        {
            var cutoffTime = ParseTimePeriod(period) ?? DateTime.UtcNow.AddDays(-7);

            // Query downloads and group by local time hour (StartTimeLocal is already in configured timezone)
            var hourlyData = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.StartTimeUtc >= cutoffTime)
                .GroupBy(d => d.StartTimeLocal.Hour)
                .Select(g => new HourlyActivityItem
                {
                    Hour = g.Key,
                    Downloads = g.Count(),
                    BytesServed = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                    CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                    CacheMissBytes = g.Sum(d => d.CacheMissBytes)
                })
                .ToListAsync();

            // Fill in missing hours with zeros
            var allHours = Enumerable.Range(0, 24)
                .Select(h => hourlyData.FirstOrDefault(hd => hd.Hour == h) ?? new HourlyActivityItem { Hour = h })
                .OrderBy(h => h.Hour)
                .ToList();

            // Find peak hour
            var peakHour = allHours.OrderByDescending(h => h.Downloads).FirstOrDefault()?.Hour ?? 0;

            return Ok(new HourlyActivityResponse
            {
                Hours = allHours,
                PeakHour = peakHour,
                TotalDownloads = allHours.Sum(h => h.Downloads),
                TotalBytesServed = allHours.Sum(h => h.BytesServed),
                Period = period
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting hourly activity data");
            return Ok(new HourlyActivityResponse { Period = period });
        }
    }

    /// <summary>
    /// Get cache growth data over time
    /// Shows how much new data has been added to the cache
    /// </summary>
    [HttpGet("cache-growth")]
    public async Task<IActionResult> GetCacheGrowth([FromQuery] string period = "7d", [FromQuery] string interval = "daily")
    {
        try
        {
            var cutoffTime = ParseTimePeriod(period) ?? DateTime.UtcNow.AddDays(-7);
            var intervalMinutes = ParseInterval(interval);

            // Get cache info for current size/capacity
            long currentCacheSize = 0;
            long totalCapacity = 0;

            // Try to get cache info from the system controller's cache service
            // For now, we'll calculate from downloads data
            var totalCacheMiss = await _context.Downloads
                .AsNoTracking()
                .SumAsync(d => (long?)d.CacheMissBytes) ?? 0;

            currentCacheSize = totalCacheMiss; // Approximation: total cache misses = data added to cache

            // Get daily cache growth data points
            List<CacheGrowthDataPoint> dataPoints;

            if (intervalMinutes >= 1440) // Daily or larger
            {
                // Group by date
                dataPoints = await _context.Downloads
                    .AsNoTracking()
                    .Where(d => d.StartTimeUtc >= cutoffTime)
                    .GroupBy(d => d.StartTimeUtc.Date)
                    .OrderBy(g => g.Key)
                    .Select(g => new CacheGrowthDataPoint
                    {
                        Timestamp = g.Key,
                        CumulativeCacheMissBytes = 0, // Will calculate cumulative below
                        GrowthFromPrevious = g.Sum(d => d.CacheMissBytes)
                    })
                    .ToListAsync();
            }
            else
            {
                // Group by hour for smaller intervals
                var hourlyData = await _context.Downloads
                    .AsNoTracking()
                    .Where(d => d.StartTimeUtc >= cutoffTime)
                    .GroupBy(d => new { d.StartTimeUtc.Date, d.StartTimeUtc.Hour })
                    .OrderBy(g => g.Key.Date).ThenBy(g => g.Key.Hour)
                    .Select(g => new CacheGrowthDataPoint
                    {
                        Timestamp = g.Key.Date.AddHours(g.Key.Hour),
                        CumulativeCacheMissBytes = 0,
                        GrowthFromPrevious = g.Sum(d => d.CacheMissBytes)
                    })
                    .ToListAsync();

                dataPoints = hourlyData;
            }

            // Calculate cumulative values
            long cumulative = 0;
            foreach (var dp in dataPoints)
            {
                cumulative += dp.GrowthFromPrevious;
                dp.CumulativeCacheMissBytes = cumulative;
                dp.Timestamp = DateTime.SpecifyKind(dp.Timestamp, DateTimeKind.Utc);
            }

            // Calculate trend and statistics using period-over-period comparison
            // Compare recent half growth to older half growth for meaningful trends
            var trend = "stable";
            double percentChange = 0;
            long avgDailyGrowth = 0;

            if (dataPoints.Count >= 2)
            {
                var firstValue = dataPoints.First().CumulativeCacheMissBytes;
                var lastValue = dataPoints.Last().CumulativeCacheMissBytes;

                var daysCovered = (dataPoints.Last().Timestamp - dataPoints.First().Timestamp).TotalDays;
                if (daysCovered > 0)
                {
                    avgDailyGrowth = (long)((lastValue - firstValue) / daysCovered);
                }

                // Period-over-period comparison: compare recent half growth rate to older half
                var growthValues = dataPoints.Select(d => (double)d.GrowthFromPrevious).ToList();
                var midpoint = growthValues.Count / 2;
                var olderHalf = growthValues.Take(midpoint).ToList();
                var recentHalf = growthValues.Skip(midpoint).ToList();

                var olderAvg = olderHalf.Count > 0 ? olderHalf.Average() : 0;
                var recentAvg = recentHalf.Count > 0 ? recentHalf.Average() : 0;

                if (olderAvg == 0 && recentAvg == 0)
                {
                    percentChange = 0;
                }
                else if (olderAvg == 0)
                {
                    percentChange = recentAvg > 0 ? 100 : 0; // New growth, cap at 100%
                }
                else
                {
                    percentChange = ((recentAvg - olderAvg) / olderAvg) * 100;
                }

                // Cap percentage at reasonable bounds (±999%)
                percentChange = Math.Max(-999, Math.Min(999, percentChange));
                percentChange = Math.Round(percentChange, 1);

                if (percentChange > 5) trend = "up";
                else if (percentChange < -5) trend = "down";
            }

            // Estimate days until full (if we had capacity info)
            int? daysUntilFull = null;
            if (avgDailyGrowth > 0 && totalCapacity > 0)
            {
                var remainingSpace = totalCapacity - currentCacheSize;
                if (remainingSpace > 0)
                {
                    daysUntilFull = (int)Math.Ceiling((double)remainingSpace / avgDailyGrowth);
                }
            }

            return Ok(new CacheGrowthResponse
            {
                DataPoints = dataPoints,
                CurrentCacheSize = currentCacheSize,
                TotalCapacity = totalCapacity,
                AverageDailyGrowth = avgDailyGrowth,
                Trend = trend,
                PercentChange = percentChange,
                EstimatedDaysUntilFull = daysUntilFull,
                Period = period
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache growth data");
            return Ok(new CacheGrowthResponse { Period = period });
        }
    }

    /// <summary>
    /// Get sparkline data for dashboard stat cards
    /// Returns daily aggregated data for bandwidth saved, cache hit ratio, total served, and added to cache
    /// </summary>
    [HttpGet("sparklines")]
    public async Task<IActionResult> GetSparklineData([FromQuery] string period = "7d")
    {
        try
        {
            var cutoffTime = ParseTimePeriod(period) ?? DateTime.UtcNow.AddDays(-7);

            // Query downloads grouped by date
            var dailyData = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.StartTimeUtc >= cutoffTime)
                .GroupBy(d => d.StartTimeUtc.Date)
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    Date = g.Key,
                    CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                    CacheMissBytes = g.Sum(d => d.CacheMissBytes)
                })
                .ToListAsync();

            // Build sparkline data for each metric
            var bandwidthSavedData = dailyData.Select(d => (double)d.CacheHitBytes).ToList();
            var addedToCacheData = dailyData.Select(d => (double)d.CacheMissBytes).ToList();
            var totalServedData = dailyData.Select(d => (double)(d.CacheHitBytes + d.CacheMissBytes)).ToList();
            var cacheHitRatioData = dailyData.Select(d =>
            {
                var total = d.CacheHitBytes + d.CacheMissBytes;
                return total > 0 ? (d.CacheHitBytes * 100.0) / total : 0.0;
            }).ToList();

            return Ok(new SparklineDataResponse
            {
                BandwidthSaved = BuildSparklineMetric(bandwidthSavedData),
                CacheHitRatio = BuildSparklineMetricForRatio(cacheHitRatioData), // Use absolute change for ratios
                TotalServed = BuildSparklineMetric(totalServedData),
                AddedToCache = BuildSparklineMetric(addedToCacheData),
                Period = period
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting sparkline data");
            return Ok(new SparklineDataResponse { Period = period });
        }
    }

    // Helper method to build sparkline metric with trend calculation
    // Uses period-over-period comparison (recent half avg vs older half avg) for meaningful trends
    // Smooths out volatility by using averages instead of single data points
    // Caps percentage at ±999% to avoid absurd display values
    private static SparklineMetric BuildSparklineMetric(List<double> data)
    {
        if (data.Count < 2)
        {
            return new SparklineMetric { Data = data, Trend = "stable", PercentChange = 0 };
        }

        // Remove trailing zeros (incomplete current period) for trend calculation
        var trimmedData = data.ToList();
        while (trimmedData.Count > 1 && trimmedData.Last() == 0)
        {
            trimmedData.RemoveAt(trimmedData.Count - 1);
        }

        if (trimmedData.Count < 2)
        {
            return new SparklineMetric { Data = data, Trend = "stable", PercentChange = 0 };
        }

        // Period-over-period comparison: split data into two halves and compare averages
        // This approach is recommended by analytics tools (Amazon QuickSight, Tableau, etc.)
        // and smooths out volatility from single-point comparisons
        var midpoint = trimmedData.Count / 2;
        var olderHalf = trimmedData.Take(midpoint).ToList();
        var recentHalf = trimmedData.Skip(midpoint).ToList();

        var olderAvg = olderHalf.Count > 0 ? olderHalf.Average() : 0;
        var recentAvg = recentHalf.Count > 0 ? recentHalf.Average() : 0;

        // If no baseline data in both periods, stable
        if (olderAvg == 0 && recentAvg == 0)
        {
            return new SparklineMetric { Data = data, Trend = "stable", PercentChange = 0 };
        }

        // Calculate percent change between period averages
        double percentChange;
        if (olderAvg == 0)
        {
            // New activity appeared - cap at 100% to indicate growth without extreme values
            percentChange = recentAvg > 0 ? 100 : 0;
        }
        else
        {
            percentChange = ((recentAvg - olderAvg) / olderAvg) * 100;
        }

        // Cap percentage at reasonable bounds (±999%) to avoid absurd display values
        // Per KPI best practices: bounds should be reasonable and contextual
        percentChange = Math.Max(-999, Math.Min(999, percentChange));

        // Use a 5% threshold for trend determination (more stable than 1%)
        string trend = "stable";
        if (percentChange > 5) trend = "up";
        else if (percentChange < -5) trend = "down";

        return new SparklineMetric
        {
            Data = data,
            Trend = trend,
            PercentChange = Math.Round(percentChange, 1)
        };
    }

    // Helper method for ratio metrics (like cache hit ratio) - uses absolute change, not percent change
    // For ratios that are already percentages, showing "percent of percent" is confusing
    // Uses period-over-period comparison with averages for stability
    private static SparklineMetric BuildSparklineMetricForRatio(List<double> data)
    {
        if (data.Count < 2)
        {
            return new SparklineMetric { Data = data, Trend = "stable", PercentChange = 0 };
        }

        // Remove trailing zeros (incomplete current period) for trend calculation
        var trimmedData = data.ToList();
        while (trimmedData.Count > 1 && trimmedData.Last() == 0)
        {
            trimmedData.RemoveAt(trimmedData.Count - 1);
        }

        if (trimmedData.Count < 2)
        {
            return new SparklineMetric { Data = data, Trend = "stable", PercentChange = 0 };
        }

        // Period-over-period comparison: split data into two halves and compare averages
        var midpoint = trimmedData.Count / 2;
        var olderHalf = trimmedData.Take(midpoint).ToList();
        var recentHalf = trimmedData.Skip(midpoint).ToList();

        var olderAvg = olderHalf.Count > 0 ? olderHalf.Average() : 0;
        var recentAvg = recentHalf.Count > 0 ? recentHalf.Average() : 0;

        // For ratios, use absolute point change between period averages
        // e.g., older avg 20% -> recent avg 80% = +60 points
        var absoluteChange = recentAvg - olderAvg;

        // Cap at reasonable bounds for ratio changes (ratios are 0-100, so ±100 is max meaningful)
        absoluteChange = Math.Max(-100, Math.Min(100, absoluteChange));

        string trend = "stable";
        if (absoluteChange > 2) trend = "up";
        else if (absoluteChange < -2) trend = "down";

        return new SparklineMetric
        {
            Data = data,
            Trend = trend,
            PercentChange = Math.Round(absoluteChange, 1) // This is absolute points, not percent
        };
    }

    // Helper method to parse time period strings
    private DateTime? ParseTimePeriod(string period)
    {
        var now = DateTime.UtcNow;

        return period?.ToLower() switch
        {
            null or "" or "all" => null,
            "15m" => now.AddMinutes(-15),
            "30m" => now.AddMinutes(-30),
            "1h" => now.AddHours(-1),
            "6h" => now.AddHours(-6),
            "12h" => now.AddHours(-12),
            "24h" or "1d" => now.AddDays(-1),
            "48h" or "2d" => now.AddDays(-2),
            "7d" or "1w" => now.AddDays(-7),
            "14d" or "2w" => now.AddDays(-14),
            "30d" or "1m" => now.AddDays(-30),
            "90d" or "3m" => now.AddDays(-90),
            "365d" or "1y" => now.AddDays(-365),
            _ => null
        };
    }

    // Helper method to parse interval strings
    private int ParseInterval(string interval)
    {
        return interval.ToLower() switch
        {
            "5min" => 5,
            "10min" => 10,
            "15min" => 15,
            "30min" => 30,
            "hourly" or "1h" => 60,
            "2h" => 120,
            "4h" => 240,
            "6h" => 360,
            "12h" => 720,
            "daily" or "1d" => 1440,
            _ => 60 // Default to hourly
        };
    }
}