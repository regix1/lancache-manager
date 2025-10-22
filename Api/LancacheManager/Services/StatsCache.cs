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
                .Take(100) // Limit to top 100 clients
                .ToListAsync();

            var clientStatsOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(_cacheExpiration)
                .SetSize(1);
            _cache.Set("client_stats", clientStats, clientStatsOptions);
            _logger.LogInformation($"Cached {clientStats.Count} client stats");

            // Pre-load service stats into cache
            var serviceStats = await context.ServiceStats
                .AsNoTracking()
                .OrderByDescending(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes)
                .ToListAsync();

            var serviceStatsOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(_cacheExpiration)
                .SetSize(1);
            _cache.Set("service_stats", serviceStats, serviceStatsOptions);
            _logger.LogInformation($"Cached {serviceStats.Count} service stats");

            // Pre-load recent downloads into cache (exclude App 0 which indicates unmapped/invalid apps)
            // MEMORY LEAK FIX: Limit to 500 downloads max
            var recentDownloads = await context.Downloads
                .AsNoTracking()
                .Where(d => !d.GameAppId.HasValue || d.GameAppId.Value != 0)
                .OrderByDescending(d => d.StartTimeUtc)
                .Take(500)
                .ToListAsync();

            var recentDownloadsOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(_cacheExpiration)
                .SetSize(1);
            _cache.Set("recent_downloads", recentDownloads, recentDownloadsOptions);
            _logger.LogInformation($"Cached {recentDownloads.Count} recent downloads");

            // Pre-load active downloads into cache (exclude App 0 which indicates unmapped/invalid apps)
            var activeDownloadsRaw = await context.Downloads
                .AsNoTracking()
                .Where(d => d.IsActive && (d.CacheHitBytes + d.CacheMissBytes) > 0 && (!d.GameAppId.HasValue || d.GameAppId.Value != 0))
                .OrderByDescending(d => d.StartTimeUtc)
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
                        StartTimeUtc = group.Min(d => d.StartTimeUtc),
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
                .SetAbsoluteExpiration(TimeSpan.FromSeconds(2))
                .SetSize(1);
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
            entry.SetSize(1);

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
            entry.SetSize(1);

            return await context.ServiceStats
                .AsNoTracking()
                .OrderByDescending(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes)
                .ToListAsync();
        }) ?? new List<ServiceStats>();
    }

    public async Task<List<Download>> GetRecentDownloadsAsync(AppDbContext context, int count = 9999)
    {
        // MEMORY LEAK FIX: Cap maximum count to prevent loading thousands of records
        // Frontend requests "unlimited" (9999) but we limit to 500 for memory safety
        const int maxAllowedCount = 500;
        var effectiveCount = Math.Min(count, maxAllowedCount);

        // MEMORY LEAK FIX: Use consistent cache key to prevent multiple entries
        // Don't use count in key - always use the same key for recent downloads
        const string cacheKey = "recent_downloads";

        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _cacheExpiration;
            entry.SetSize(1); // Set size for cache eviction policies

            return await context.Downloads
                .AsNoTracking()
                .Where(d => !d.GameAppId.HasValue || d.GameAppId.Value != 0)
                .OrderByDescending(d => d.StartTimeUtc)
                .Take(effectiveCount)
                .ToListAsync();
        }) ?? new List<Download>();
    }

    public async Task<List<Download>> GetActiveDownloadsAsync(AppDbContext context)
    {
        return await _cache.GetOrCreateAsync("active_downloads", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(2); // Fast refresh for live data
            entry.SetSize(1);

            // Get all active downloads
            var activeDownloads = await context.Downloads
                .AsNoTracking()
                .Where(d => d.IsActive && (d.CacheHitBytes + d.CacheMissBytes) > 0 && (!d.GameAppId.HasValue || d.GameAppId.Value != 0))
                .OrderByDescending(d => d.StartTimeUtc)
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
                        StartTimeUtc = group.Min(d => d.StartTimeUtc),
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
        _cache.Remove("recent_downloads");
        _cache.Remove("active_downloads");

        _logger.LogDebug("Downloads cache invalidated");
    }
}