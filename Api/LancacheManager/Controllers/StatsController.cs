using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Services;
using LancacheManager.Data;
using LancacheManager.Models;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly StatsService _statsService;
    private readonly ILogger<StatsController> _logger;

    public StatsController(AppDbContext context, StatsService statsService, ILogger<StatsController> logger)
    {
        _context = context;
        _statsService = statsService;
        _logger = logger;
    }

    [HttpGet("clients")]
    [ResponseCache(Duration = 10)] // Cache for 10 seconds
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
            return Ok(new List<object>());
        }
    }

    [HttpGet("services")]
    [ResponseCache(Duration = 10)] // Cache for 10 seconds
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
            return Ok(new List<object>());
        }
    }

    [HttpGet("dashboard")]
    [ResponseCache(Duration = 0, NoStore = true)] // No cache - prevent memory buildup
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

            // Calculate aggregates in the database (no ToListAsync - just get the numbers)
            var periodHitBytes = await downloadsQuery.SumAsync(d => (long?)d.CacheHitBytes) ?? 0L;
            var periodMissBytes = await downloadsQuery.SumAsync(d => (long?)d.CacheMissBytes) ?? 0L;
            var periodDownloadCount = await downloadsQuery.CountAsync();
            var periodTotal = periodHitBytes + periodMissBytes;
            var periodHitRatio = periodTotal > 0
                ? (double)periodHitBytes / periodTotal
                : 0;

            // Active downloads count (database-side)
            var activeDownloads = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.IsActive && d.EndTimeUtc > DateTime.UtcNow.AddMinutes(-5))
                .CountAsync();

            // Count unique clients for the period (database-side)
            var uniqueClientsCount = cutoffTime.HasValue
                ? await _context.ClientStats
                    .AsNoTracking()
                    .Where(c => c.LastActivityUtc >= cutoffTime.Value)
                    .CountAsync()
                : await _context.ClientStats.AsNoTracking().CountAsync();

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

    [HttpGet("cache-effectiveness")]
    [ResponseCache(Duration = 10)] // Cache for 10 seconds
    public async Task<IActionResult> GetCacheEffectiveness([FromQuery] string period = "24h")
    {
        try
        {
            // Handle "all" period
            DateTime? cutoffTime = null;
            if (period != "all")
            {
                cutoffTime = ParseTimePeriod(period) ?? DateTime.UtcNow.AddHours(-24);
            }
            
            // Get downloads based on period
            IQueryable<Download> downloadsQuery = _context.Downloads.AsNoTracking();
            if (cutoffTime.HasValue)
            {
                downloadsQuery = downloadsQuery.Where(d => d.StartTimeUtc >= cutoffTime.Value);
            }
            var downloads = await downloadsQuery.ToListAsync();
                
            // Calculate overall effectiveness
            var totalHitBytes = downloads.Sum(d => d.CacheHitBytes);
            var totalMissBytes = downloads.Sum(d => d.CacheMissBytes);
            var totalBytes = totalHitBytes + totalMissBytes;
            
            var overallHitRatio = totalBytes > 0 
                ? (double)totalHitBytes / totalBytes 
                : 0;
                
            // Per-service effectiveness
            var serviceEffectiveness = downloads
                .GroupBy(d => d.Service)
                .Select(g => new
                {
                    service = g.Key,
                    hitBytes = g.Sum(d => d.CacheHitBytes),
                    missBytes = g.Sum(d => d.CacheMissBytes),
                    totalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                    hitRatio = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes) > 0
                        ? (double)g.Sum(d => d.CacheHitBytes) / g.Sum(d => d.CacheHitBytes + d.CacheMissBytes)
                        : 0,
                    downloads = g.Count()
                })
                .OrderByDescending(s => s.totalBytes)
                .ToList();
                
            // Per-client effectiveness
            var clientEffectiveness = downloads
                .GroupBy(d => d.ClientIp)
                .Select(g => new
                {
                    clientIp = g.Key,
                    hitBytes = g.Sum(d => d.CacheHitBytes),
                    missBytes = g.Sum(d => d.CacheMissBytes),
                    totalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                    hitRatio = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes) > 0
                        ? (double)g.Sum(d => d.CacheHitBytes) / g.Sum(d => d.CacheHitBytes + d.CacheMissBytes)
                        : 0,
                    downloads = g.Count()
                })
                .OrderByDescending(c => c.totalBytes)
                .Take(20) // Top 20 clients
                .ToList();
                
            return Ok(new
            {
                period = new
                {
                    duration = period,
                    since = cutoffTime,
                    until = DateTime.UtcNow
                },
                overall = new
                {
                    hitBytes = totalHitBytes,
                    missBytes = totalMissBytes,
                    totalBytes,
                    hitRatio = overallHitRatio,
                    hitPercentage = overallHitRatio * 100,
                    bandwidthSaved = totalHitBytes,
                    downloadsAnalyzed = downloads.Count
                },
                byService = serviceEffectiveness,
                byClient = clientEffectiveness,
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache effectiveness");
            return StatusCode(500, new { error = "Failed to get cache effectiveness data" });
        }
    }

    [HttpGet("timeline")]
    [ResponseCache(Duration = 30)] // Cache for 30 seconds
    public async Task<IActionResult> GetTimelineStats(
        [FromQuery] string period = "24h",
        [FromQuery] string interval = "hourly")
    {
        try
        {
            // Handle "all" period
            DateTime? cutoffTime = null;
            if (period != "all")
            {
                cutoffTime = ParseTimePeriod(period) ?? DateTime.UtcNow.AddHours(-24);
            }
            var intervalMinutes = ParseInterval(interval);
            
            // Get downloads based on period
            IQueryable<Download> downloadsQuery = _context.Downloads.AsNoTracking();
            if (cutoffTime.HasValue)
            {
                downloadsQuery = downloadsQuery.Where(d => d.StartTimeUtc >= cutoffTime.Value);
            }
            var downloads = await downloadsQuery
                .OrderBy(d => d.StartTimeUtc)
                .ToListAsync();
                
            if (downloads.Count == 0)
            {
                return Ok(new
                {
                    period = new
                    {
                        duration = period,
                        since = cutoffTime,
                        until = DateTime.UtcNow,
                        interval
                    },
                    dataPoints = new List<object>(),
                    summary = new
                    {
                        totalHitBytes = 0L,
                        totalMissBytes = 0L,
                        totalBytes = 0L,
                        averageHitRatio = 0.0
                    }
                });
            }
            
            // Group downloads by time interval
            var dataPoints = new List<object>();
            var startTime = cutoffTime ?? downloads.Min(d => d.StartTimeUtc);
            var currentTime = startTime;
            var endTime = DateTime.UtcNow;
            
            while (currentTime < endTime)
            {
                var intervalEnd = currentTime.AddMinutes(intervalMinutes);
                
                var intervalDownloads = downloads
                    .Where(d => d.StartTimeUtc >= currentTime && d.StartTimeUtc < intervalEnd)
                    .ToList();
                    
                var hitBytes = intervalDownloads.Sum(d => d.CacheHitBytes);
                var missBytes = intervalDownloads.Sum(d => d.CacheMissBytes);
                var totalBytes = hitBytes + missBytes;
                
                dataPoints.Add(new
                {
                    timestamp = currentTime,
                    timestampEnd = intervalEnd,
                    cacheHits = hitBytes,
                    cacheMisses = missBytes,
                    totalBytes,
                    hitRatio = totalBytes > 0 ? (double)hitBytes / totalBytes : 0,
                    downloads = intervalDownloads.Count
                });
                
                currentTime = intervalEnd;
            }
            
            // Calculate summary statistics
            var totalHitBytes = downloads.Sum(d => d.CacheHitBytes);
            var totalMissBytes = downloads.Sum(d => d.CacheMissBytes);
            var totalBytesSum = totalHitBytes + totalMissBytes;
            
            return Ok(new
            {
                period = new
                {
                    duration = period,
                    since = cutoffTime,
                    until = DateTime.UtcNow,
                    interval,
                    intervalMinutes
                },
                dataPoints = dataPoints,
                summary = new
                {
                    totalHitBytes,
                    totalMissBytes,
                    totalBytes = totalBytesSum,
                    averageHitRatio = totalBytesSum > 0 
                        ? (double)totalHitBytes / totalBytesSum 
                        : 0
                },
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting timeline stats");
            return StatusCode(500, new { error = "Failed to get timeline statistics" });
        }
    }

    [HttpGet("bandwidth-saved")]
    [ResponseCache(Duration = 10)]
    public async Task<IActionResult> GetBandwidthSaved([FromQuery] string period = "all")
    {
        try
        {
            var query = _context.Downloads.AsNoTracking();

            if (period != "all")
            {
                var cutoffTime = ParseTimePeriod(period);
                if (cutoffTime.HasValue)
                {
                    query = query.Where(d => d.StartTimeUtc >= cutoffTime.Value);
                }
            }

            var downloads = await query.ToListAsync();
            
            var totalSaved = downloads.Sum(d => d.CacheHitBytes);
            var totalServed = downloads.Sum(d => d.CacheHitBytes + d.CacheMissBytes);
            var savingsRatio = totalServed > 0 ? (double)totalSaved / totalServed : 0;
            
            // Calculate by service
            var byService = downloads
                .GroupBy(d => d.Service)
                .Select(g => new
                {
                    service = g.Key,
                    saved = g.Sum(d => d.CacheHitBytes),
                    total = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                    ratio = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes) > 0
                        ? (double)g.Sum(d => d.CacheHitBytes) / g.Sum(d => d.CacheHitBytes + d.CacheMissBytes)
                        : 0
                })
                .OrderByDescending(s => s.saved)
                .ToList();
            
            return Ok(new
            {
                period,
                totalBandwidthSaved = totalSaved,
                totalBandwidthServed = totalServed,
                savingsRatio,
                savingsPercentage = savingsRatio * 100,
                byService,
                calculatedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating bandwidth saved");
            return StatusCode(500, new { error = "Failed to calculate bandwidth saved" });
        }
    }

    [HttpGet("top-games")]
    [ResponseCache(Duration = 30)]
    public async Task<IActionResult> GetTopGames([FromQuery] int limit = 10, [FromQuery] string period = "7d")
    {
        try
        {
            var topGames = await _statsService.GetTopGamesAsync(limit, period, "bytes");

            var cutoffTime = period != "all"
                ? ParseTimePeriod(period) ?? DateTime.UtcNow.AddDays(-7)
                : (DateTime?)null;

            return Ok(new
            {
                period = new
                {
                    duration = period,
                    since = cutoffTime,
                    until = DateTime.UtcNow
                },
                games = topGames.Select(g => new
                {
                    appId = g.GameAppId,
                    gameName = g.GameName,
                    totalBytes = g.TotalBytes,
                    cacheHitBytes = g.CacheHitBytes,
                    cacheMissBytes = g.CacheMissBytes,
                    downloadCount = g.TotalDownloads,
                    uniqueClients = g.UniqueClients
                }),
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting top games");
            return StatusCode(500, new { error = "Failed to get top games" });
        }
    }

    // Helper method to parse time period strings
    private DateTime? ParseTimePeriod(string period)
    {
        if (string.IsNullOrEmpty(period) || period == "all")
            return null;
            
        var now = DateTime.UtcNow;
        
        return period.ToLower() switch
        {
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