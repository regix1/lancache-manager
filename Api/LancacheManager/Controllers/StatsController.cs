using LancacheManager.Application.DTOs;
using LancacheManager.Data;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
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
    [OutputCache(PolicyName = "stats-short")]
    public async Task<IActionResult> GetClients([FromQuery] long? startTime = null, [FromQuery] long? endTime = null)
    {
        try
        {
            // Build base query with time filtering
            var query = _context.Downloads.AsNoTracking();

            if (startTime.HasValue)
            {
                var startDate = DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc >= startDate);
            }
            if (endTime.HasValue)
            {
                var endDate = DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc <= endDate);
            }

            // Get filtered downloads with EndTimeUtc for duration calculation
            // NOTE: Using EndTime - StartTime instead of querying LogEntries for performance
            // The LogEntries query was causing severe slowdowns with large datasets
            var downloads = await query
                .Select(d => new { d.Id, d.ClientIp, d.CacheHitBytes, d.CacheMissBytes, d.StartTimeUtc, d.EndTimeUtc })
                .ToListAsync();

            // Group by client and calculate totals including duration from EndTime - StartTime
            var stats = downloads
                .GroupBy(d => d.ClientIp)
                .Select(g =>
                {
                    // Calculate duration from EndTime - StartTime for completed downloads
                    var totalDuration = g.Sum(d =>
                    {
                        if (d.EndTimeUtc > d.StartTimeUtc)
                        {
                            return (d.EndTimeUtc - d.StartTimeUtc).TotalSeconds;
                        }
                        return 0;
                    });

                    return new ClientStats
                    {
                        ClientIp = g.Key,
                        TotalCacheHitBytes = g.Sum(d => d.CacheHitBytes),
                        TotalCacheMissBytes = g.Sum(d => d.CacheMissBytes),
                        TotalDownloads = g.Count(),
                        TotalDurationSeconds = totalDuration,
                        LastActivityUtc = DateTime.SpecifyKind(g.Max(d => d.StartTimeUtc), DateTimeKind.Utc),
                        LastActivityLocal = g.Max(d => d.StartTimeUtc)
                    };
                })
                .OrderByDescending(c => c.TotalCacheHitBytes + c.TotalCacheMissBytes)
                .Take(100)
                .ToList();

            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting client stats");
            return Ok(new List<ClientStats>());
        }
    }

    [HttpGet("services")]
    [OutputCache(PolicyName = "stats-short")]
    public async Task<IActionResult> GetServices([FromQuery] string? since = null, [FromQuery] long? startTime = null, [FromQuery] long? endTime = null)
    {
        try
        {
            // ALWAYS query Downloads table directly to ensure consistency with dashboard stats
            // Previously used cached ServiceStats table which caused fluctuating values
            var query = _context.Downloads.AsNoTracking();

            // Apply time filtering if provided
            if (startTime.HasValue)
            {
                var startDate = DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc >= startDate);
            }
            if (endTime.HasValue)
            {
                var endDate = DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc <= endDate);
            }
            else if (!string.IsNullOrEmpty(since) && since != "all")
            {
                // Parse time period string for backwards compatibility
                var cutoffTime = ParseTimePeriod(since);
                if (cutoffTime.HasValue)
                {
                    query = query.Where(d => d.StartTimeUtc >= cutoffTime.Value);
                }
            }
            // No filter = all data (consistent with dashboard)

            // Aggregate by service from Downloads table
            var serviceStats = await query
                .GroupBy(d => d.Service)
                .Select(g => new ServiceStats
                {
                    Service = g.Key,
                    TotalCacheHitBytes = g.Sum(d => d.CacheHitBytes),
                    TotalCacheMissBytes = g.Sum(d => d.CacheMissBytes),
                    TotalDownloads = g.Count(),
                    LastActivityUtc = g.Max(d => d.StartTimeUtc),
                    LastActivityLocal = g.Max(d => d.StartTimeLocal)
                })
                .OrderByDescending(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes)
                .ToListAsync();

            // Fix timezone for proper JSON serialization
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
    [OutputCache(PolicyName = "stats-short")]
    public async Task<IActionResult> GetDashboardStats(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null)
    {
        // Use Unix timestamps if provided, otherwise return ALL data (no time filter)
        // This ensures consistency: frontend always provides timestamps for time-filtered queries
        DateTime? cutoffTime = null;
        DateTime? endDateTime = null;

        if (startTime.HasValue)
        {
            cutoffTime = DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime;
        }
        if (endTime.HasValue)
        {
            endDateTime = DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime;
        }
        // If no timestamps provided, cutoffTime and endDateTime remain null = query ALL data

        // IMPORTANT: Calculate ALL metrics from Downloads table directly (no cache)
        // This ensures consistency - mixing cached ServiceStats with live Downloads caused fluctuating values

        // Build the base query for period-specific metrics
        var downloadsQuery = _context.Downloads.AsNoTracking();
        if (cutoffTime.HasValue)
        {
            downloadsQuery = downloadsQuery.Where(d => d.StartTimeUtc >= cutoffTime.Value);
        }
        if (endDateTime.HasValue)
        {
            downloadsQuery = downloadsQuery.Where(d => d.StartTimeUtc <= endDateTime.Value);
        }

        // Calculate ALL-TIME totals from Downloads table directly (no cache)
        var allTimeQuery = _context.Downloads.AsNoTracking();
        var totalHitBytesTask = allTimeQuery.SumAsync(d => (long?)d.CacheHitBytes);
        var totalMissBytesTask = allTimeQuery.SumAsync(d => (long?)d.CacheMissBytes);

        // Calculate PERIOD-specific metrics
        var periodHitBytesTask = downloadsQuery.SumAsync(d => (long?)d.CacheHitBytes);
        var periodMissBytesTask = downloadsQuery.SumAsync(d => (long?)d.CacheMissBytes);
        var periodDownloadCountTask = downloadsQuery.CountAsync();

        // Get top service from Downloads table (not cached ServiceStats)
        var topServiceTask = _context.Downloads
            .AsNoTracking()
            .GroupBy(d => d.Service)
            .Select(g => new { Service = g.Key, TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes) })
            .OrderByDescending(s => s.TotalBytes)
            .FirstOrDefaultAsync();

        // Active downloads and unique clients
        var activeDownloadsTask = _context.Downloads
            .AsNoTracking()
            .Where(d => d.IsActive && d.EndTimeUtc > DateTime.UtcNow.AddMinutes(-5))
            .CountAsync();

        var uniqueClientsQuery = cutoffTime.HasValue || endDateTime.HasValue
            ? downloadsQuery.Select(d => d.ClientIp).Distinct().CountAsync()
            : _context.Downloads.AsNoTracking().Select(d => d.ClientIp).Distinct().CountAsync();

        // Await all tasks in parallel
        await Task.WhenAll(
            totalHitBytesTask, totalMissBytesTask,
            periodHitBytesTask, periodMissBytesTask, periodDownloadCountTask,
            topServiceTask, activeDownloadsTask, uniqueClientsQuery);

        // All-time metrics (from Downloads table directly)
        var totalBandwidthSaved = totalHitBytesTask.Result ?? 0L;
        var totalAddedToCache = totalMissBytesTask.Result ?? 0L;
        var totalServed = totalBandwidthSaved + totalAddedToCache;
        var cacheHitRatio = totalServed > 0
            ? (double)totalBandwidthSaved / totalServed
            : 0;
        var topService = topServiceTask.Result?.Service ?? "none";

        // Period-specific metrics
        var periodHitBytes = periodHitBytesTask.Result ?? 0L;
        var periodMissBytes = periodMissBytesTask.Result ?? 0L;
        var periodDownloadCount = periodDownloadCountTask.Result;
        var activeDownloads = activeDownloadsTask.Result;
        var uniqueClientsCount = uniqueClientsQuery.Result;

        var periodTotal = periodHitBytes + periodMissBytes;
        var periodHitRatio = periodTotal > 0
            ? (double)periodHitBytes / periodTotal
            : 0;

        // Determine period label for response
        string periodLabel = "all";
        if (cutoffTime.HasValue && endDateTime.HasValue)
        {
            var duration = endDateTime.Value - cutoffTime.Value;
            periodLabel = duration.TotalHours <= 24 ? $"{(int)duration.TotalHours}h" : $"{(int)duration.TotalDays}d";
        }
        else if (cutoffTime.HasValue)
        {
            periodLabel = "since " + cutoffTime.Value.ToString("yyyy-MM-dd");
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
                Duration = periodLabel,
                Since = cutoffTime,
                BandwidthSaved = periodHitBytes,
                AddedToCache = periodMissBytes,
                TotalServed = periodTotal,
                HitRatio = periodHitRatio,
                Downloads = periodDownloadCount
            },

            // Service breakdown (query from Downloads table for consistency)
            ServiceBreakdown = await _context.Downloads
                .AsNoTracking()
                .GroupBy(d => d.Service)
                .Select(g => new ServiceBreakdownItem
                {
                    Service = g.Key,
                    Bytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                    Percentage = totalServed > 0
                        ? (g.Sum(d => d.CacheHitBytes + d.CacheMissBytes) * 100.0) / totalServed
                        : 0
                })
                .OrderByDescending(s => s.Bytes)
                .ToListAsync(),

            LastUpdated = DateTime.UtcNow
        });
    }


    /// <summary>
    /// Get hourly activity data for peak usage hours widget
    /// Groups downloads by hour of day to show activity patterns
    /// </summary>
    [HttpGet("hourly-activity")]
    [OutputCache(PolicyName = "stats-long")]
    public async Task<IActionResult> GetHourlyActivity(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null)
    {
        try
        {
            // Build query with optional time filtering
            var query = _context.Downloads.AsNoTracking();

            DateTime? cutoffTime = null;
            DateTime? endDateTime = null;

            if (startTime.HasValue)
            {
                cutoffTime = DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc >= cutoffTime);
            }
            if (endTime.HasValue)
            {
                endDateTime = DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc <= endDateTime);
            }

            // Calculate number of distinct days in the period
            int daysInPeriod = 1;
            long? periodStartTimestamp = null;
            long? periodEndTimestamp = null;

            if (startTime.HasValue && endTime.HasValue)
            {
                // Use the provided time range
                daysInPeriod = Math.Max(1, (int)Math.Ceiling((endDateTime!.Value - cutoffTime!.Value).TotalDays));
                periodStartTimestamp = startTime.Value;
                periodEndTimestamp = endTime.Value;
            }
            else
            {
                // For "all" data, count distinct days from the actual data
                var dateRange = await query
                    .Select(d => d.StartTimeLocal.Date)
                    .Distinct()
                    .ToListAsync();

                daysInPeriod = Math.Max(1, dateRange.Count);

                if (dateRange.Count > 0)
                {
                    var minDate = dateRange.Min();
                    var maxDate = dateRange.Max();
                    periodStartTimestamp = new DateTimeOffset(minDate, TimeSpan.Zero).ToUnixTimeSeconds();
                    periodEndTimestamp = new DateTimeOffset(maxDate.AddDays(1).AddSeconds(-1), TimeSpan.Zero).ToUnixTimeSeconds();
                }
            }

            // Query downloads and group by local time hour (StartTimeLocal is already in configured timezone)
            var hourlyData = await query
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

            // Fill in missing hours with zeros and calculate averages
            var allHours = Enumerable.Range(0, 24)
                .Select(h => {
                    var existing = hourlyData.FirstOrDefault(hd => hd.Hour == h);
                    if (existing != null)
                    {
                        existing.AvgDownloads = Math.Round((double)existing.Downloads / daysInPeriod, 1);
                        existing.AvgBytesServed = existing.BytesServed / daysInPeriod;
                        return existing;
                    }
                    return new HourlyActivityItem { Hour = h };
                })
                .OrderBy(h => h.Hour)
                .ToList();

            // Find peak hour (based on total downloads, not average)
            var peakHour = allHours.OrderByDescending(h => h.Downloads).FirstOrDefault()?.Hour ?? 0;

            return Ok(new HourlyActivityResponse
            {
                Hours = allHours,
                PeakHour = peakHour,
                TotalDownloads = allHours.Sum(h => h.Downloads),
                TotalBytesServed = allHours.Sum(h => h.BytesServed),
                DaysInPeriod = daysInPeriod,
                PeriodStart = periodStartTimestamp,
                PeriodEnd = periodEndTimestamp,
                Period = startTime.HasValue ? "filtered" : "all"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting hourly activity data");
            return Ok(new HourlyActivityResponse { Period = "error" });
        }
    }

    /// <summary>
    /// Get cache growth data over time
    /// Shows how much new data has been added to the cache
    /// </summary>
    [HttpGet("cache-growth")]
    [OutputCache(PolicyName = "stats-long")]
    public async Task<IActionResult> GetCacheGrowth(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null,
        [FromQuery] string interval = "daily")
    {
        try
        {
            DateTime? cutoffTime = startTime.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime
                : (DateTime?)null;
            DateTime? endDateTime = endTime.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime
                : (DateTime?)null;
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

            // Build base query with time filtering
            var baseQuery = _context.Downloads.AsNoTracking();
            if (cutoffTime.HasValue)
            {
                baseQuery = baseQuery.Where(d => d.StartTimeUtc >= cutoffTime.Value);
            }
            if (endDateTime.HasValue)
            {
                baseQuery = baseQuery.Where(d => d.StartTimeUtc <= endDateTime.Value);
            }

            // Get daily cache growth data points
            List<CacheGrowthDataPoint> dataPoints;

            if (intervalMinutes >= 1440) // Daily or larger
            {
                // Group by date
                dataPoints = await baseQuery
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
                var hourlyData = await baseQuery
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
                Period = startTime.HasValue ? "filtered" : "all"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache growth data");
            return Ok(new CacheGrowthResponse { Period = "error" });
        }
    }

    /// <summary>
    /// Get sparkline data for dashboard stat cards
    /// Returns daily aggregated data for bandwidth saved, cache hit ratio, total served, and added to cache
    /// </summary>
    [HttpGet("sparklines")]
    [OutputCache(PolicyName = "stats-long")]
    public async Task<IActionResult> GetSparklineData(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null)
    {
        try
        {
            // Build query with optional time filtering
            var query = _context.Downloads.AsNoTracking();

            if (startTime.HasValue)
            {
                var cutoffTime = DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc >= cutoffTime);
            }
            if (endTime.HasValue)
            {
                var endDateTime = DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc <= endDateTime);
            }

            // Query downloads grouped by date
            var dailyData = await query
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
                Period = startTime.HasValue ? "filtered" : "all"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting sparkline data");
            return Ok(new SparklineDataResponse { Period = "error" });
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