using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Models;
using LancacheManager.Constants;

namespace LancacheManager.Services;

/// <summary>
/// Shared service for common statistics database queries
/// Used by both StatsController and MetricsController to avoid duplication
/// </summary>
public class StatsService
{
    private readonly AppDbContext _context;
    private readonly ILogger<StatsService> _logger;

    public StatsService(AppDbContext context, ILogger<StatsService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get service statistics
    /// </summary>
    public async Task<List<ServiceStats>> GetServiceStatsAsync(CancellationToken cancellationToken = default)
    {
        return await _context.ServiceStats
            .AsNoTracking()
            .OrderByDescending(s => s.TotalBytes)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Get client statistics
    /// </summary>
    public async Task<List<ClientStats>> GetClientStatsAsync(CancellationToken cancellationToken = default)
    {
        return await _context.ClientStats
            .AsNoTracking()
            .OrderByDescending(c => c.TotalBytes)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Get latest downloads with optional limit
    /// </summary>
    public async Task<List<Download>> GetLatestDownloadsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var query = _context.Downloads
            .AsNoTracking()
            .OrderByDescending(d => d.StartTime);

        if (limit > 0)
        {
            query = (IOrderedQueryable<Download>)query.Take(limit);
        }

        return await query.ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Get active downloads
    /// </summary>
    public async Task<List<Download>> GetActiveDownloadsAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Downloads
            .AsNoTracking()
            .Where(d => d.IsActive)
            .OrderByDescending(d => d.StartTime)
            .ToListAsync(cancellationToken);
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

        var query = _context.Downloads
            .AsNoTracking()
            .Where(d => d.StartTime >= cutoff && !string.IsNullOrEmpty(d.GameName))
            .GroupBy(d => new { d.GameName, d.GameAppId })
            .Select(g => new GameStat
            {
                GameName = g.Key.GameName,
                GameAppId = (int)(g.Key.GameAppId ?? 0),
                TotalDownloads = g.Count(),
                TotalBytes = g.Sum(d => (long?)d.TotalBytes ?? 0),
                CacheHitBytes = g.Sum(d => (long?)d.CacheHitBytes ?? 0),
                CacheMissBytes = g.Sum(d => (long?)d.CacheMissBytes ?? 0),
                UniqueClients = g.Select(d => d.ClientIp).Distinct().Count()
            });

        // Sort based on preference
        query = sortBy.ToLower() switch
        {
            "bytes" => query.OrderByDescending(g => g.TotalBytes),
            "clients" => query.OrderByDescending(g => g.UniqueClients),
            _ => query.OrderByDescending(g => g.TotalDownloads)
        };

        return await query.Take(limit).ToListAsync(cancellationToken);
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