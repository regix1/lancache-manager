using LancacheManager.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace LancacheManager.Infrastructure.Utilities;

public class StatsCache
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<StatsCache> _logger;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromSeconds(10); // Longer cache for graph stability

    public StatsCache(IMemoryCache cache, ILogger<StatsCache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task RefreshFromDatabase(AppDbContext context)
    {
        try
        {
            // Pre-load client stats into cache
            var clientStats = await context.ClientStats
                .AsNoTracking()
                .OrderByDescending(c => c.TotalCacheHitBytes + c.TotalCacheMissBytes)
                .ToListAsync();

            var clientStatsOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(_cacheExpiration);
            _cache.Set("client_stats", clientStats, clientStatsOptions);
            _logger.LogInformation($"Cached {clientStats.Count} client stats");

            // Pre-load service stats into cache
            var serviceStats = await context.ServiceStats
                .AsNoTracking()
                .OrderByDescending(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes)
                .ToListAsync();

            var serviceStatsOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(_cacheExpiration);
            _cache.Set("service_stats", serviceStats, serviceStatsOptions);
            _logger.LogInformation($"Cached {serviceStats.Count} service stats");

            // Pre-load active downloads into cache (exclude App 0 which indicates unmapped/invalid apps)
            var activeDownloadsRaw = await context.Downloads
                .AsNoTracking()
                .Where(d => d.IsActive && (d.CacheHitBytes + d.CacheMissBytes) > 0 && (!d.GameAppId.HasValue || d.GameAppId.Value != 0))
                .OrderByDescending(d => d.StartTimeUtc)
                .ToListAsync();

            // Group chunks by game - prefer mapped downloads
            var activeDownloads = activeDownloadsRaw
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

            var activeDownloadsOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromSeconds(2));
            _cache.Set("active_downloads", activeDownloads, activeDownloadsOptions);
            _logger.LogInformation($"Cached {activeDownloads.Count} active downloads (from {activeDownloadsRaw.Count} raw chunks)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing cache from database");
        }
    }

    public async Task<List<ClientStats>> GetClientStatsAsync(AppDbContext context)
    {
        return await _cache.GetOrCreateAsync("client_stats", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _cacheExpiration;

            var stats = await context.ClientStats
                .AsNoTracking()
                .OrderByDescending(c => c.TotalCacheHitBytes + c.TotalCacheMissBytes)
                .ToListAsync();

            // Fix timezone: Ensure UTC DateTime values are marked as UTC for proper JSON serialization
            foreach (var stat in stats)
            {
                stat.LastActivityUtc = DateTime.SpecifyKind(stat.LastActivityUtc, DateTimeKind.Utc);
            }

            return stats;
        }) ?? new List<ClientStats>();
    }

    public async Task<List<ServiceStats>> GetServiceStatsAsync(AppDbContext context)
    {
        return await _cache.GetOrCreateAsync("service_stats", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _cacheExpiration;

            var stats = await context.ServiceStats
                .AsNoTracking()
                .OrderByDescending(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes)
                .ToListAsync();

            // Fix timezone: Ensure UTC DateTime values are marked as UTC for proper JSON serialization
            foreach (var stat in stats)
            {
                stat.LastActivityUtc = DateTime.SpecifyKind(stat.LastActivityUtc, DateTimeKind.Utc);
            }

            return stats;
        }) ?? new List<ServiceStats>();
    }

    public async Task<List<Download>> GetRecentDownloadsAsync(AppDbContext context, int count = int.MaxValue)
    {
        // No cache - always query DB for fresh data with requested count
        var downloads = await context.Downloads
            .AsNoTracking()
            .Where(d => !d.GameAppId.HasValue || d.GameAppId.Value != 0)
            .OrderByDescending(d => d.StartTimeUtc)
            .Take(count)
            .ToListAsync();

        // Fix timezone: Ensure UTC DateTime values are marked as UTC for proper JSON serialization
        foreach (var download in downloads)
        {
            download.StartTimeUtc = DateTime.SpecifyKind(download.StartTimeUtc, DateTimeKind.Utc);
            if (download.EndTimeUtc != default(DateTime))
            {
                download.EndTimeUtc = DateTime.SpecifyKind(download.EndTimeUtc, DateTimeKind.Utc);
            }
        }

        return downloads;
    }

    public async Task<List<Download>> GetActiveDownloadsAsync(AppDbContext context)
    {
        return await _cache.GetOrCreateAsync("active_downloads", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(2); // Fast refresh for live data

            // Get all active downloads
            var activeDownloads = await context.Downloads
                .AsNoTracking()
                .Where(d => d.IsActive && (d.CacheHitBytes + d.CacheMissBytes) > 0 && (!d.GameAppId.HasValue || d.GameAppId.Value != 0))
                .OrderByDescending(d => d.StartTimeUtc)
                .ToListAsync();

            // Group chunks by game to show as single download
            // For Steam: use GameAppId if available, otherwise group by ClientIp+Service
            // For others: group by GameName+ClientIp+Service
            var grouped = activeDownloads
                .GroupBy(d => new
                {
                    // Use GameAppId for Steam (when mapped), otherwise use DepotId to keep chunks separate until mapped
                    GameKey = d.GameAppId.HasValue ? d.GameAppId.Value.ToString() : (d.DepotId?.ToString() ?? "unknown"),
                    ClientIp = d.ClientIp,
                    Service = d.Service
                })
                .Select(group =>
                {
                    // Prefer mapped downloads (with GameName) over unmapped
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
        }) ?? new List<Download>();
    }

    public void InvalidateDownloads()
    {
        // Only invalidate active downloads cache (recent downloads not cached)
        _cache.Remove("active_downloads");
    }
}