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

            // Pre-load recent downloads into cache
            var recentDownloads = await context.Downloads
                .AsNoTracking()
                .OrderByDescending(d => d.StartTime)
                .Take(100)
                .ToListAsync();
            
            _cache.Set("recent_downloads", recentDownloads, _cacheExpiration);
            _logger.LogInformation($"Cached {recentDownloads.Count} recent downloads");

            // Pre-load active downloads into cache
            // Only check IsActive flag - cleanup service handles marking old downloads as complete
            var activeDownloads = await context.Downloads
                .AsNoTracking()
                .Where(d => d.IsActive)
                .OrderByDescending(d => d.StartTime)
                .Take(100)
                .ToListAsync();

            _cache.Set("active_downloads", activeDownloads, _cacheExpiration);
            _logger.LogInformation($"Cached {activeDownloads.Count} active downloads");
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
                .OrderByDescending(d => d.StartTime)
                .Take(count)
                .ToListAsync();
        }) ?? new List<Download>();
    }

    public async Task<List<Download>> GetActiveDownloadsAsync(AppDbContext context)
    {
        return await _cache.GetOrCreateAsync("active_downloads", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10); // Match general cache for consistency

            // Only check IsActive flag - cleanup service handles marking old downloads as complete
            return await context.Downloads
                .AsNoTracking()
                .Where(d => d.IsActive)
                .OrderByDescending(d => d.StartTime)
                .Take(100)
                .ToListAsync();
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