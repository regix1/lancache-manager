using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Models;

namespace LancacheManager.Services;

public class StatsCache
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<StatsCache> _logger;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromSeconds(1); // Short cache to respect user's polling rate choice

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
            var recentDownloads = await context.Downloads
                .AsNoTracking()
                .Where(d => !d.GameAppId.HasValue || d.GameAppId.Value != 0)
                .OrderByDescending(d => d.StartTimeUtc)
                .Take(100)
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
        var cacheKey = $"recent_downloads_{count}";

        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _cacheExpiration;
            entry.SetSize(1);

            return await context.Downloads
                .AsNoTracking()
                .Where(d => !d.GameAppId.HasValue || d.GameAppId.Value != 0)
                .OrderByDescending(d => d.StartTimeUtc)
                .Take(count)
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

    /// <summary>
    /// Get downloads for a specific time period with caching
    /// </summary>
    public async Task<List<Download>> GetDownloadsByPeriodAsync(AppDbContext context, string period = "24h")
    {
        var cacheKey = $"downloads_period_{period}";

        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _cacheExpiration;
            entry.SetSize(1);

            DateTime? cutoffTime = null;
            if (period != "all")
            {
                cutoffTime = ParseTimePeriod(period) ?? DateTime.UtcNow.AddHours(-24);
            }

            IQueryable<Download> query = context.Downloads.AsNoTracking();
            if (cutoffTime.HasValue)
            {
                query = query.Where(d => d.StartTimeUtc >= cutoffTime.Value);
            }

            return await query
                .OrderByDescending(d => d.StartTimeUtc)
                .Take(10000) // Reasonable limit to prevent loading millions of records
                .ToListAsync();
        }) ?? new List<Download>();
    }

    /// <summary>
    /// Get all client stats with caching (used by dashboard)
    /// </summary>
    public async Task<List<ClientStats>> GetAllClientStatsAsync(AppDbContext context)
    {
        return await _cache.GetOrCreateAsync("all_client_stats", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _cacheExpiration;
            entry.SetSize(1);

            return await context.ClientStats
                .AsNoTracking()
                .ToListAsync();
        }) ?? new List<ClientStats>();
    }

    /// <summary>
    /// Get all service stats with caching (used by dashboard)
    /// </summary>
    public async Task<List<ServiceStats>> GetAllServiceStatsAsync(AppDbContext context)
    {
        return await _cache.GetOrCreateAsync("all_service_stats", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _cacheExpiration;
            entry.SetSize(1);

            return await context.ServiceStats
                .AsNoTracking()
                .ToListAsync();
        }) ?? new List<ServiceStats>();
    }

    private DateTime? ParseTimePeriod(string period)
    {
        if (string.IsNullOrEmpty(period) || period == "all")
            return null;

        var now = DateTime.UtcNow;

        return period.ToLower() switch
        {
            "15m" => now.AddMinutes(-15),
            "30m" => now.AddMinutes(-30),
            "1h" => now.AddHours(-1),
            "6h" => now.AddHours(-6),
            "12h" => now.AddHours(-12),
            "24h" or "1d" => now.AddDays(-1),
            "48h" or "2d" => now.AddDays(-2),
            "7d" or "1w" => now.AddDays(-7),
            "14d" or "2w" => now.AddDays(-14),
            "30d" or "1m" => now.AddDays(-30),
            "90d" or "3m" => now.AddDays(-90),
            "365d" or "1y" => now.AddDays(-365),
            _ => null
        };
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

        // Remove period-specific caches
        var commonPeriods = new[] { "15m", "30m", "1h", "6h", "12h", "24h", "7d", "30d", "all" };
        foreach (var period in commonPeriods)
        {
            _cache.Remove($"downloads_period_{period}");
        }

        _logger.LogDebug("Downloads cache invalidated");
    }

    public void InvalidateAll()
    {
        InvalidateDownloads();
        _cache.Remove("client_stats");
        _cache.Remove("service_stats");
        _cache.Remove("all_client_stats");
        _cache.Remove("all_service_stats");
        _logger.LogDebug("All cache invalidated");
    }
}