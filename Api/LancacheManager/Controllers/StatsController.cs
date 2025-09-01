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
    private readonly ILogger<StatsController> _logger;

    public StatsController(AppDbContext context, ILogger<StatsController> logger)
    {
        _context = context;
        _logger = logger;
    }

    [HttpGet("clients")]
    [ResponseCache(Duration = 10)] // Cache for 10 seconds
    public async Task<IActionResult> GetClients()
    {
        try
        {
            var stats = await _context.ClientStats
                .AsNoTracking()
                .OrderByDescending(c => c.TotalCacheHitBytes + c.TotalCacheMissBytes)
                .Take(100) // Limit results
                .ToListAsync();
                
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
    public async Task<IActionResult> GetServices([FromQuery] string? since = null)
    {
        try
        {
            var query = _context.ServiceStats.AsNoTracking();
            
            // Add time filtering if requested
            if (!string.IsNullOrEmpty(since))
            {
                var cutoffTime = ParseTimePeriod(since);
                if (cutoffTime.HasValue)
                {
                    query = query.Where(s => s.LastActivity >= cutoffTime.Value);
                }
            }
            
            var stats = await query
                .OrderByDescending(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes)
                .ToListAsync();
                
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting service stats");
            return Ok(new List<object>());
        }
    }

    [HttpGet("dashboard")]
    [ResponseCache(Duration = 5)] // Cache for 5 seconds
    public async Task<IActionResult> GetDashboardStats([FromQuery] string period = "24h")
    {
        try
        {
            var cutoffTime = ParseTimePeriod(period) ?? DateTime.UtcNow.AddHours(-24);
            
            // Get all stats in one query for efficiency
            var serviceStats = await _context.ServiceStats
                .AsNoTracking()
                .ToListAsync();
                
            var clientStats = await _context.ClientStats
                .AsNoTracking()
                .ToListAsync();
                
            var recentDownloads = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.StartTime >= cutoffTime)
                .ToListAsync();
                
            var activeDownloads = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.IsActive && d.EndTime > DateTime.UtcNow.AddMinutes(-5))
                .CountAsync();
                
            // Calculate aggregated metrics
            var totalBandwidthSaved = serviceStats.Sum(s => s.TotalCacheHitBytes);
            var totalAddedToCache = serviceStats.Sum(s => s.TotalCacheMissBytes);
            var totalServed = totalBandwidthSaved + totalAddedToCache;
            
            var cacheHitRatio = totalServed > 0 
                ? (double)totalBandwidthSaved / totalServed 
                : 0;
                
            var uniqueClientsCount = clientStats
                .Where(c => c.LastSeen >= cutoffTime)
                .Count();
                
            var topService = serviceStats
                .OrderByDescending(s => s.TotalBytes)
                .FirstOrDefault()?.Service ?? "none";
                
            // Calculate period-specific metrics
            var periodHitBytes = recentDownloads.Sum(d => d.CacheHitBytes);
            var periodMissBytes = recentDownloads.Sum(d => d.CacheMissBytes);
            var periodTotal = periodHitBytes + periodMissBytes;
            var periodHitRatio = periodTotal > 0 
                ? (double)periodHitBytes / periodTotal 
                : 0;
            
            return Ok(new
            {
                // All-time metrics
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
                    downloads = recentDownloads.Count
                },
                
                // Service breakdown for the period
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
            var cutoffTime = ParseTimePeriod(period) ?? DateTime.UtcNow.AddHours(-24);
            
            // Get downloads within the period
            var downloads = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.StartTime >= cutoffTime)
                .ToListAsync();
                
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
                    totalBytes = totalBytes,
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
            var cutoffTime = ParseTimePeriod(period) ?? DateTime.UtcNow.AddHours(-24);
            var intervalMinutes = ParseInterval(interval);
            
            // Get all downloads in the period
            var downloads = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.StartTime >= cutoffTime)
                .OrderBy(d => d.StartTime)
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
                        interval = interval
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
            var currentTime = cutoffTime;
            var endTime = DateTime.UtcNow;
            
            while (currentTime < endTime)
            {
                var intervalEnd = currentTime.AddMinutes(intervalMinutes);
                
                var intervalDownloads = downloads
                    .Where(d => d.StartTime >= currentTime && d.StartTime < intervalEnd)
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
                    totalBytes = totalBytes,
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
                    interval = interval,
                    intervalMinutes = intervalMinutes
                },
                dataPoints = dataPoints,
                summary = new
                {
                    totalHitBytes = totalHitBytes,
                    totalMissBytes = totalMissBytes,
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
                    query = query.Where(d => d.StartTime >= cutoffTime.Value);
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
                period = period,
                totalBandwidthSaved = totalSaved,
                totalBandwidthServed = totalServed,
                savingsRatio = savingsRatio,
                savingsPercentage = savingsRatio * 100,
                byService = byService,
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
            var cutoffTime = ParseTimePeriod(period) ?? DateTime.UtcNow.AddDays(-7);
            
            var topGames = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.StartTime >= cutoffTime && 
                           d.Service == "steam" && 
                           !string.IsNullOrEmpty(d.GameName) &&
                           d.GameName != "Unknown Steam Game")
                .GroupBy(d => new { d.GameAppId, d.GameName })
                .Select(g => new
                {
                    appId = g.Key.GameAppId,
                    gameName = g.Key.GameName,
                    totalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                    cacheHitBytes = g.Sum(d => d.CacheHitBytes),
                    cacheMissBytes = g.Sum(d => d.CacheMissBytes),
                    downloadCount = g.Count(),
                    uniqueClients = g.Select(d => d.ClientIp).Distinct().Count()
                })
                .OrderByDescending(g => g.totalBytes)
                .Take(limit)
                .ToListAsync();
                
            return Ok(new
            {
                period = new
                {
                    duration = period,
                    since = cutoffTime,
                    until = DateTime.UtcNow
                },
                games = topGames,
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
        if (string.IsNullOrEmpty(period))
            return null;
            
        var now = DateTime.UtcNow;
        
        return period.ToLower() switch
        {
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