using LancacheManager.Data;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
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
        try
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

            return Ok(new
            {
                // All-time metrics (always from ServiceStats totals)
                totalBandwidthSaved,
                totalAddedToCache,
                totalServed,
                cacheHitRatio,

                // Current status
                activeDownloads,
                uniqueClients = uniqueClientsCount,
                topService,

                // Period-specific metrics
                period = new
                {
                    duration = period,
                    since = cutoffTime,
                    bandwidthSaved = periodHitBytes,
                    addedToCache = periodMissBytes,
                    totalServed = periodTotal,
                    hitRatio = periodHitRatio,
                    downloads = periodDownloadCount
                },

                // Service breakdown (always all-time for consistency)
                serviceBreakdown = serviceStats
                    .Select(s => new
                    {
                        service = s.Service,
                        bytes = s.TotalBytes,
                        percentage = totalServed > 0
                            ? (s.TotalBytes * 100.0) / totalServed
                            : 0
                    })
                    .OrderByDescending(s => s.bytes)
                    .ToList(),

                lastUpdated = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dashboard stats");
            return StatusCode(500, new { error = "Failed to get dashboard statistics" });
        }
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