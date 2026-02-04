using LancacheManager.Models;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;


namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service for statistics database queries
/// Queries Downloads table directly for consistent data (no caching)
/// </summary>
public class StatsDataService : IStatsDataService
{
    private const string PrefillToken = "prefill";

    private readonly AppDbContext _context;
    private readonly ILogger<StatsDataService> _logger;

    public StatsDataService(AppDbContext context, ILogger<StatsDataService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get service statistics from Downloads table
    /// </summary>
    public async Task<List<ServiceStats>> GetServiceStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = await ApplyPrefillFilter(_context.Downloads)
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

        return stats.WithUtcMarking();
    }

    /// <summary>
    /// Get client statistics from Downloads table
    /// </summary>
    public async Task<List<ClientStats>> GetClientStatsAsync(CancellationToken cancellationToken = default)
    {
        // Fetch downloads with client info and end time for duration calculation
        // NOTE: Using EndTime - StartTime instead of querying LogEntries for performance
        var downloads = await ApplyPrefillFilter(_context.Downloads)
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
                    LastActivityUtc = g.Max(d => d.StartTimeUtc),
                    LastActivityLocal = g.Max(d => d.StartTimeUtc)
                };
            })
            .OrderByDescending(c => c.TotalCacheHitBytes + c.TotalCacheMissBytes)
            .ToList();

        return stats.WithUtcMarking();
    }

    /// <summary>
    /// Get latest downloads with optional limit.
    /// Uses LEFT JOIN to resolve game names from SteamDepotMappings for downloads
    /// where the game name wasn't available at download time.
    /// </summary>
    /// <param name="limit">Maximum number of downloads to return</param>
    /// <param name="activeOnly">If true, only return active (in-progress) downloads</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<List<Download>> GetLatestDownloadsAsync(int limit = int.MaxValue, bool activeOnly = false, CancellationToken cancellationToken = default)
    {
        // Start with base query applying prefill filter
        var baseQuery = ApplyPrefillFilter(_context.Downloads.AsNoTracking())
            .Where(d => !d.GameAppId.HasValue || d.GameAppId.Value != 0);

        // Apply active-only filter if requested
        if (activeOnly)
        {
            baseQuery = baseQuery.Where(d => d.IsActive);
        }

        // LEFT JOIN with SteamDepotMappings to resolve missing game names at query time
        var query = from d in baseQuery
                    join m in _context.SteamDepotMappings.Where(mapping => mapping.IsOwner)
                        on d.DepotId equals m.DepotId into mappings
                    from mapping in mappings.DefaultIfEmpty()
                    orderby d.StartTimeUtc descending
                    select new
                    {
                        Download = d,
                        MappedAppName = mapping != null ? mapping.AppName : null,
                        MappedAppId = mapping != null ? (uint?)mapping.AppId : null
                    };

        var results = await query.Take(limit).ToListAsync(cancellationToken);

        var downloads = results.Select(r =>
        {
            var download = r.Download;

            // Fill in missing game info from mapping if available
            if (string.IsNullOrEmpty(download.GameName) && !string.IsNullOrEmpty(r.MappedAppName))
            {
                download.GameName = r.MappedAppName;
                download.GameAppId = r.MappedAppId;
            }

            // Calculate duration from EndTime - StartTime for proper JSON serialization
            if (download.EndTimeUtc != default(DateTime) && download.EndTimeUtc > download.StartTimeUtc)
            {
                download.DurationSeconds = (download.EndTimeUtc - download.StartTimeUtc).TotalSeconds;
            }

            return download;
        }).ToList();

        return downloads.WithUtcMarking();
    }

    /// <summary>
    /// Get top games by download count or bytes
    /// </summary>
    public async Task<List<GameStat>> GetTopGamesAsync(int limit = 10, string period = "7d", string sortBy = "downloads", CancellationToken cancellationToken = default)
    {
        var cutoff = TimeUtils.GetCutoffTime(period, DateTime.UtcNow);

        var downloads = await ApplyPrefillFilter(_context.Downloads)
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

    private static IQueryable<Download> ApplyPrefillFilter(IQueryable<Download> query)
    {
        return query
            .Where(d => d.ClientIp == null || d.ClientIp.ToLower() != PrefillToken)
            .Where(d => d.Datasource == null || d.Datasource.ToLower() != PrefillToken);
    }
}
