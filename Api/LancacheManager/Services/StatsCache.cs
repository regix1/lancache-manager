using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Models;

namespace LancacheManager.Services;

public class StatsCache
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<StatsCache> _logger;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromSeconds(10);

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
            var cutoff = DateTime.UtcNow.AddMinutes(-5);
            var activeDownloads = await context.Downloads
                .AsNoTracking()
                .Where(d => d.IsActive && d.EndTime > cutoff)
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
        });
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
        });
    }

    public async Task<List<Download>> GetRecentDownloadsAsync(AppDbContext context, int count = 50)
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
        });
    }

    public async Task<List<Download>> GetActiveDownloadsAsync(AppDbContext context)
    {
        return await _cache.GetOrCreateAsync("active_downloads", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(2); // Shorter cache for active downloads
            
            var cutoff = DateTime.UtcNow.AddMinutes(-5);
            return await context.Downloads
                .AsNoTracking()
                .Where(d => d.IsActive && d.EndTime > cutoff)
                .OrderByDescending(d => d.StartTime)
                .Take(100)
                .ToListAsync();
        });
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
        
        // Remove all count-specific recent download caches
        for (int i = 10; i <= 100; i += 10)
        {
            _cache.Remove($"recent_downloads_{i}");
        }
    }
}