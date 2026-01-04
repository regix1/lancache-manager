using LancacheManager.Application.DTOs;
using LancacheManager.Data;
using LancacheManager.Infrastructure.Repositories.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Repositories;

/// <summary>
/// Repository for statistics database queries
/// Queries Downloads table directly for consistent data (no caching)
/// </summary>
public class StatsRepository : IStatsRepository
{
    private readonly AppDbContext _context;
    private readonly ILogger<StatsRepository> _logger;

    public StatsRepository(AppDbContext context, ILogger<StatsRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get service statistics from Downloads table
    /// </summary>
    public async Task<List<ServiceStats>> GetServiceStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = await _context.Downloads
            .AsNoTracking()
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
            .ToListAsync(cancellationToken);

        // Fix timezone for proper JSON serialization
        foreach (var stat in stats)
        {
            stat.LastActivityUtc = stat.LastActivityUtc.AsUtc();
        }

        return stats;
    }

    /// <summary>
    /// Get client statistics from Downloads table
    /// </summary>
    public async Task<List<ClientStats>> GetClientStatsAsync(CancellationToken cancellationToken = default)
    {
        // Fetch downloads with client info and end time for duration calculation
        // NOTE: Using EndTime - StartTime instead of querying LogEntries for performance
        var downloads = await _context.Downloads
            .AsNoTracking()
            .Select(d => new
            {
                d.Id,
                d.ClientIp,
                d.CacheHitBytes,
                d.CacheMissBytes,
                d.StartTimeUtc,
                d.EndTimeUtc
            })
            .ToListAsync(cancellationToken);

        // Group in memory to calculate stats
        var stats = downloads
            .GroupBy(d => d.ClientIp)
            .Select(g =>
            {
                // Calculate total duration from EndTime - StartTime for completed downloads
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
                    LastActivityUtc = g.Max(d => d.StartTimeUtc).AsUtc(),
                    LastActivityLocal = g.Max(d => d.StartTimeUtc)
                };
            })
            .OrderByDescending(c => c.TotalCacheHitBytes + c.TotalCacheMissBytes)
            .ToList();

        return stats;
    }

    /// <summary>
    /// Get latest downloads with optional limit
    /// </summary>
    public async Task<List<Download>> GetLatestDownloadsAsync(int limit = int.MaxValue, CancellationToken cancellationToken = default)
    {
        var downloads = await _context.Downloads
            .AsNoTracking()
            .Where(d => !d.GameAppId.HasValue || d.GameAppId.Value != 0)
            .OrderByDescending(d => d.StartTimeUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

        // Fix timezone and calculate duration from EndTime - StartTime for proper JSON serialization
        // NOTE: Using EndTime - StartTime instead of querying LogEntries for performance
        foreach (var download in downloads)
        {
            download.StartTimeUtc = download.StartTimeUtc.AsUtc();
            if (download.EndTimeUtc != default(DateTime))
            {
                download.EndTimeUtc = download.EndTimeUtc.AsUtc();
                // Calculate duration from EndTime - StartTime
                if (download.EndTimeUtc > download.StartTimeUtc)
                {
                    download.DurationSeconds = (download.EndTimeUtc - download.StartTimeUtc).TotalSeconds;
                }
            }
        }

        return downloads;
    }

    /// <summary>
    /// Get active downloads grouped by game
    /// </summary>
    

    /// <summary>
    /// Get top games by download count or bytes
    /// </summary>
    public async Task<List<GameStat>> GetTopGamesAsync(int limit = 10, string period = "7d", string sortBy = "downloads", CancellationToken cancellationToken = default)
    {
        var cutoff = TimeUtils.GetCutoffTime(period, DateTime.UtcNow);

        var downloads = await _context.Downloads
            .AsNoTracking()
            .Where(d => d.StartTimeUtc >= cutoff && !string.IsNullOrEmpty(d.GameName))
            .Select(d => new { d.GameName, d.GameAppId, d.CacheHitBytes, d.CacheMissBytes, d.ClientIp })
            .ToListAsync(cancellationToken);

        var groupedStats = downloads
            // Group by GameAppId only to prevent duplicates from name variations (e.g., "Game®" vs "Game")
            .GroupBy(d => d.GameAppId ?? 0)
            .Select(g => new GameStat
            {
                // Use the first non-empty game name from the group
                GameName = g.Select(d => d.GameName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "",
                GameAppId = (int)g.Key,
                TotalDownloads = g.Count(),
                TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                CacheMissBytes = g.Sum(d => d.CacheMissBytes),
                UniqueClients = g.Select(d => d.ClientIp).Distinct().Count()
            });

        var sortedStats = sortBy.ToLower() switch
        {
            "bytes" => groupedStats.OrderByDescending(g => g.TotalBytes),
            "clients" => groupedStats.OrderByDescending(g => g.UniqueClients),
            _ => groupedStats.OrderByDescending(g => g.TotalDownloads)
        };

        return sortedStats.Take(limit).ToList();
    }
}
