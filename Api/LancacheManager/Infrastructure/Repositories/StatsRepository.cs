using LancacheManager.Application.DTOs;
using LancacheManager.Data;
using LancacheManager.Infrastructure.Repositories.Interfaces;
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
            stat.LastActivityUtc = DateTime.SpecifyKind(stat.LastActivityUtc, DateTimeKind.Utc);
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
                    LastActivityUtc = DateTime.SpecifyKind(g.Max(d => d.StartTimeUtc), DateTimeKind.Utc),
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
            download.StartTimeUtc = DateTime.SpecifyKind(download.StartTimeUtc, DateTimeKind.Utc);
            if (download.EndTimeUtc != default(DateTime))
            {
                download.EndTimeUtc = DateTime.SpecifyKind(download.EndTimeUtc, DateTimeKind.Utc);
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
    public async Task<List<Download>> GetActiveDownloadsAsync(CancellationToken cancellationToken = default)
    {
        // Get all active downloads
        var activeDownloads = await _context.Downloads
            .AsNoTracking()
            .Where(d => d.IsActive && (d.CacheHitBytes + d.CacheMissBytes) > 0 && (!d.GameAppId.HasValue || d.GameAppId.Value != 0))
            .OrderByDescending(d => d.StartTimeUtc)
            .ToListAsync(cancellationToken);

        // Group chunks by game to show as single download
        var grouped = activeDownloads
            .GroupBy(d => new
            {
                GameKey = d.GameAppId.HasValue ? d.GameAppId.Value.ToString() : (d.DepotId?.ToString() ?? "unknown"),
                ClientIp = d.ClientIp,
                Service = d.Service
            })
            .Select(group =>
            {
                var first = group.OrderByDescending(d => !string.IsNullOrEmpty(d.GameName) && d.GameName != "Unknown Steam Game").First();

                return new Download
                {
                    Id = first.Id,
                    Service = first.Service,
                    ClientIp = first.ClientIp,
                    StartTimeUtc = DateTime.SpecifyKind(group.Min(d => d.StartTimeUtc), DateTimeKind.Utc),
                    EndTimeUtc = default(DateTime),
                    StartTimeLocal = group.Min(d => d.StartTimeLocal),
                    EndTimeLocal = default(DateTime),
                    CacheHitBytes = group.Sum(d => d.CacheHitBytes),
                    CacheMissBytes = group.Sum(d => d.CacheMissBytes),
                    IsActive = true,
                    GameName = first.GameName,
                    GameAppId = first.GameAppId,
                    GameImageUrl = first.GameImageUrl,
                    DepotId = first.DepotId
                };
            })
            .OrderByDescending(d => d.StartTimeUtc)
            .ToList();

        return grouped;
    }

    /// <summary>
    /// Get top games by download count or bytes
    /// </summary>
    public async Task<List<GameStat>> GetTopGamesAsync(int limit = 10, string period = "7d", string sortBy = "downloads", CancellationToken cancellationToken = default)
    {
        var cutoff = GetCutoffTime(period, DateTime.UtcNow);

        var downloads = await _context.Downloads
            .AsNoTracking()
            .Where(d => d.StartTimeUtc >= cutoff && !string.IsNullOrEmpty(d.GameName))
            .Select(d => new { d.GameName, d.GameAppId, d.CacheHitBytes, d.CacheMissBytes, d.ClientIp })
            .ToListAsync(cancellationToken);

        var groupedStats = downloads
            .GroupBy(d => new { d.GameName, d.GameAppId })
            .Select(g => new GameStat
            {
                GameName = g.Key.GameName ?? "",
                GameAppId = (int)(g.Key.GameAppId ?? 0),
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
            _ => now.AddHours(-24)
        };
    }
}
