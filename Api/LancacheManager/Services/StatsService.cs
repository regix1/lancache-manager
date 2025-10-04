using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Models;
using LancacheManager.Constants;

namespace LancacheManager.Services;

/// <summary>
/// Shared service for common statistics database queries
/// Used by both StatsController and MetricsController to avoid duplication
/// Uses StatsCache for performance on frequently accessed data
/// </summary>
public class StatsService
{
    private readonly AppDbContext _context;
    private readonly StatsCache _cache;
    private readonly ILogger<StatsService> _logger;

    public StatsService(AppDbContext context, StatsCache cache, ILogger<StatsService> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Get service statistics (cached for performance)
    /// </summary>
    public async Task<List<ServiceStats>> GetServiceStatsAsync(CancellationToken cancellationToken = default)
    {
        return await _cache.GetServiceStatsAsync(_context);
    }

    /// <summary>
    /// Get client statistics (cached for performance)
    /// </summary>
    public async Task<List<ClientStats>> GetClientStatsAsync(CancellationToken cancellationToken = default)
    {
        return await _cache.GetClientStatsAsync(_context);
    }

    /// <summary>
    /// Get latest downloads with optional limit (cached for performance)
    /// </summary>
    public async Task<List<Download>> GetLatestDownloadsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        return await _cache.GetRecentDownloadsAsync(_context, limit);
    }

    /// <summary>
    /// Get active downloads (cached with 2-second expiration)
    /// </summary>
    public async Task<List<Download>> GetActiveDownloadsAsync(CancellationToken cancellationToken = default)
    {
        return await _cache.GetActiveDownloadsAsync(_context);
    }

    /// <summary>
    /// Get count of active downloads
    /// </summary>
    public async Task<int> GetActiveDownloadCountAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Downloads
            .AsNoTracking()
            .Where(d => d.IsActive)
            .CountAsync(cancellationToken);
    }

    /// <summary>
    /// Get unique client count
    /// </summary>
    public async Task<int> GetUniqueClientCountAsync(CancellationToken cancellationToken = default)
    {
        return await _context.ClientStats
            .AsNoTracking()
            .CountAsync(cancellationToken);
    }

    /// <summary>
    /// Get total bandwidth saved (sum of all cache hits)
    /// </summary>
    public async Task<long> GetTotalBandwidthSavedAsync(CancellationToken cancellationToken = default)
    {
        return await _context.ServiceStats
            .AsNoTracking()
            .SumAsync(s => s.TotalCacheHitBytes, cancellationToken);
    }

    /// <summary>
    /// Get dashboard aggregated stats for a specific time period
    /// </summary>
    public async Task<DashboardStats> GetDashboardStatsAsync(string period = "24h", CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var cutoff = GetCutoffTime(period, now);

        // Get downloads within the time period
        var downloadsQuery = _context.Downloads
            .AsNoTracking()
            .Where(d => d.StartTime >= cutoff);

        var totalDownloads = await downloadsQuery.CountAsync(cancellationToken);
        var activeDownloads = await downloadsQuery.Where(d => d.IsActive).CountAsync(cancellationToken);

        var totalBytes = await downloadsQuery.SumAsync(d => (long?)d.TotalBytes ?? 0, cancellationToken);
        var cacheHitBytes = await downloadsQuery.SumAsync(d => (long?)d.CacheHitBytes ?? 0, cancellationToken);
        var cacheMissBytes = await downloadsQuery.SumAsync(d => (long?)d.CacheMissBytes ?? 0, cancellationToken);

        // Get unique services and clients in period
        var uniqueServices = await downloadsQuery
            .Select(d => d.Service)
            .Distinct()
            .CountAsync(cancellationToken);

        var uniqueClients = await downloadsQuery
            .Select(d => d.ClientIp)
            .Distinct()
            .CountAsync(cancellationToken);

        // Calculate hit ratio
        var hitRatio = totalBytes > 0 ? (double)cacheHitBytes / totalBytes * 100 : 0;

        return new DashboardStats
        {
            Period = period,
            TotalDownloads = totalDownloads,
            ActiveDownloads = activeDownloads,
            TotalBytes = totalBytes,
            CacheHitBytes = cacheHitBytes,
            CacheMissBytes = cacheMissBytes,
            HitRatio = hitRatio,
            UniqueServices = uniqueServices,
            UniqueClients = uniqueClients,
            BandwidthSaved = cacheHitBytes,
            LastUpdated = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Get top games by download count or bytes
    /// </summary>
    public async Task<List<GameStat>> GetTopGamesAsync(int limit = 10, string period = "7d", string sortBy = "downloads", CancellationToken cancellationToken = default)
    {
        var cutoff = GetCutoffTime(period, DateTime.UtcNow);

        // Load data first, then group in memory to avoid EF Core translation issues
        // Exclude localhost (127.0.0.1) from statistics to filter out test/development traffic
        var downloads = await _context.Downloads
            .AsNoTracking()
            .Where(d => d.StartTime >= cutoff && !string.IsNullOrEmpty(d.GameName) && d.ClientIp != "127.0.0.1")
            .Select(d => new { d.GameName, d.GameAppId, d.TotalBytes, d.CacheHitBytes, d.CacheMissBytes, d.ClientIp })
            .ToListAsync(cancellationToken);

        var groupedStats = downloads
            .GroupBy(d => new { d.GameName, d.GameAppId })
            .Select(g => new GameStat
            {
                GameName = g.Key.GameName ?? "",
                GameAppId = (int)(g.Key.GameAppId ?? 0),
                TotalDownloads = g.Count(),
                TotalBytes = g.Sum(d => d.TotalBytes),
                CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                CacheMissBytes = g.Sum(d => d.CacheMissBytes),
                UniqueClients = g.Select(d => d.ClientIp).Distinct().Count()
            });

        // Sort based on preference
        var sortedStats = sortBy.ToLower() switch
        {
            "bytes" => groupedStats.OrderByDescending(g => g.TotalBytes),
            "clients" => groupedStats.OrderByDescending(g => g.UniqueClients),
            _ => groupedStats.OrderByDescending(g => g.TotalDownloads)
        };

        return sortedStats.Take(limit).ToList();
    }

    /// <summary>
    /// Get service-specific stats for a time period
    /// </summary>
    public async Task<List<ServicePeriodStat>> GetServiceStatsForPeriodAsync(string period = "24h", CancellationToken cancellationToken = default)
    {
        var cutoff = GetCutoffTime(period, DateTime.UtcNow);

        return await _context.Downloads
            .AsNoTracking()
            .Where(d => d.StartTime >= cutoff)
            .GroupBy(d => d.Service)
            .Select(g => new ServicePeriodStat
            {
                Service = g.Key,
                TotalDownloads = g.Count(),
                TotalBytes = g.Sum(d => (long?)d.TotalBytes ?? 0),
                CacheHitBytes = g.Sum(d => (long?)d.CacheHitBytes ?? 0),
                CacheMissBytes = g.Sum(d => (long?)d.CacheMissBytes ?? 0),
                UniqueClients = g.Select(d => d.ClientIp).Distinct().Count(),
                Period = period
            })
            .OrderByDescending(s => s.TotalBytes)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Helper method to calculate cutoff time based on period string
    /// </summary>
    private DateTime GetCutoffTime(string period, DateTime now)
    {
        return period.ToLower() switch
        {
            "1h" => now.AddHours(-1),
            "6h" => now.AddHours(-6),
            "12h" => now.AddHours(-12),
            "24h" => now.AddHours(-24),
            "7d" => now.AddDays(-7),
            "30d" => now.AddDays(-30),
            "all" => DateTime.MinValue,
            _ => now.AddHours(-24) // Default to 24h
        };
    }
}

// DTOs for stats results
public class DashboardStats
{
    public string Period { get; set; } = "24h";
    public int TotalDownloads { get; set; }
    public int ActiveDownloads { get; set; }
    public long TotalBytes { get; set; }
    public long CacheHitBytes { get; set; }
    public long CacheMissBytes { get; set; }
    public double HitRatio { get; set; }
    public int UniqueServices { get; set; }
    public int UniqueClients { get; set; }
    public long BandwidthSaved { get; set; }
    public DateTime LastUpdated { get; set; }
}

public class GameStat
{
    public string GameName { get; set; } = "";
    public int GameAppId { get; set; }
    public int TotalDownloads { get; set; }
    public long TotalBytes { get; set; }
    public long CacheHitBytes { get; set; }
    public long CacheMissBytes { get; set; }
    public int UniqueClients { get; set; }
}

public class ServicePeriodStat
{
    public string Service { get; set; } = "";
    public string Period { get; set; } = "";
    public int TotalDownloads { get; set; }
    public long TotalBytes { get; set; }
    public long CacheHitBytes { get; set; }
    public long CacheMissBytes { get; set; }
    public int UniqueClients { get; set; }
}