using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Models;

namespace LancacheManager.Services;

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

            _cache.Set("client_stats", clientStats, _cacheExpiration);
            _logger.LogInformation($"Cached {clientStats.Count} client stats");

            // Pre-load service stats into cache
            var serviceStats = await context.ServiceStats
                .AsNoTracking()
                .OrderByDescending(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes)
                .ToListAsync();
            
            _cache.Set("service_stats", serviceStats, _cacheExpiration);
            _logger.LogInformation($"Cached {serviceStats.Count} service stats");

            // Pre-load recent downloads into cache (exclude App 0 which indicates unmapped/invalid apps)
            var recentDownloads = await context.Downloads
                .AsNoTracking()
                .Where(d => !d.GameAppId.HasValue || d.GameAppId.Value != 0)
                .OrderByDescending(d => d.StartTime)
                .Take(100)
                .ToListAsync();

            _cache.Set("recent_downloads", recentDownloads, _cacheExpiration);
            _logger.LogInformation($"Cached {recentDownloads.Count} recent downloads");

            // Pre-load active downloads into cache (exclude App 0 which indicates unmapped/invalid apps)
            var activeDownloadsRaw = await context.Downloads
                .AsNoTracking()
                .Where(d => d.IsActive && (d.CacheHitBytes + d.CacheMissBytes) > 0 && (!d.GameAppId.HasValue || d.GameAppId.Value != 0))
                .OrderByDescending(d => d.StartTime)
                .Take(100)
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
                        StartTime = group.Min(d => d.StartTime),
                        EndTime = default(DateTime),
                        CacheHitBytes = group.Sum(d => d.CacheHitBytes),
                        CacheMissBytes = group.Sum(d => d.CacheMissBytes),
                        IsActive = true,
                        GameName = first.GameName,
                        GameAppId = first.GameAppId,
                        GameImageUrl = first.GameImageUrl,
                        DepotId = first.DepotId
                    };
                })
                .OrderByDescending(d => d.StartTime)
                .ToList();

            _cache.Set("active_downloads", activeDownloads, TimeSpan.FromSeconds(2));
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

            return await context.ClientStats
                .AsNoTracking()
                .OrderByDescending(c => c.TotalCacheHitBytes + c.TotalCacheMissBytes)
                .Take(100)
                .ToListAsync();
        }) ?? new List<ClientStats>();
    }

    public async Task<List<ServiceStats>> GetServiceStatsAsync(AppDbContext context)
    {
        return await _cache.GetOrCreateAsync("service_stats", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _cacheExpiration;
            
            return await context.ServiceStats
                .AsNoTracking()
                .OrderByDescending(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes)
                .ToListAsync();
        }) ?? new List<ServiceStats>();
    }

    public async Task<List<Download>> GetRecentDownloadsAsync(AppDbContext context, int count = 9999)
    {
        var cacheKey = $"recent_downloads_{count}";

        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _cacheExpiration;

            return await context.Downloads
                .AsNoTracking()
                .Where(d => !d.GameAppId.HasValue || d.GameAppId.Value != 0)
                .OrderByDescending(d => d.StartTime)
                .Take(count)
                .ToListAsync();
        }) ?? new List<Download>();
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
                .OrderByDescending(d => d.StartTime)
                .Take(100)
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
                        StartTime = group.Min(d => d.StartTime),
                        EndTime = default(DateTime),
                        CacheHitBytes = group.Sum(d => d.CacheHitBytes),
                        CacheMissBytes = group.Sum(d => d.CacheMissBytes),
                        IsActive = true,
                        GameName = first.GameName,
                        GameAppId = first.GameAppId,
                        GameImageUrl = first.GameImageUrl,
                        DepotId = first.DepotId
                    };
                })
                .OrderByDescending(d => d.StartTime)
                .ToList();

            return grouped;
        }) ?? new List<Download>();
    }

    public void InvalidateCache()
    {
        _cache.Remove("client_stats");
        _cache.Remove("service_stats");
        _cache.Remove("recent_downloads");
        _cache.Remove("active_downloads");
        _logger.LogInformation("Cache invalidated");
    }

    public void InvalidateDownloads()
    {
        _cache.Remove("recent_downloads");
        _cache.Remove("active_downloads");

        // Remove all count-specific recent download caches (including common values)
        var commonCounts = new[] { 10, 20, 25, 30, 40, 50, 60, 70, 75, 80, 90, 100, 200, 500, 1000, 9999 };
        foreach (var count in commonCounts)
        {
            _cache.Remove($"recent_downloads_{count}");
        }

        _logger.LogDebug("Downloads cache invalidated");
    }
}