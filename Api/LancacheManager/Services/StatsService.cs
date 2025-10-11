using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Models;

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
    /// Get top games by download count or bytes
    /// </summary>
    public async Task<List<GameStat>> GetTopGamesAsync(int limit = 10, string period = "7d", string sortBy = "downloads", CancellationToken cancellationToken = default)
    {
        var cutoff = GetCutoffTime(period, DateTime.UtcNow);

        // Load data first, then group in memory to avoid EF Core translation issues
        var downloads = await _context.Downloads
            .AsNoTracking()
            .Where(d => d.StartTimeUtc >= cutoff && !string.IsNullOrEmpty(d.GameName))
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