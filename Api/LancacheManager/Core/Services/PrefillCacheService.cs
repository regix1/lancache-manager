using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LancacheManager.Core.Services;

/// <summary>
/// Service to manage the prefill cache - tracking which games/depots have been
/// successfully prefilled to prevent re-downloading unless there's an update.
/// </summary>
public class PrefillCacheService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<PrefillCacheService> _logger;

    public PrefillCacheService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<PrefillCacheService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Records that a depot has been successfully cached.
    /// Called after a successful prefill download.
    /// </summary>
    public async Task RecordCachedDepotAsync(
        uint appId,
        uint depotId,
        ulong manifestId,
        string? appName,
        long totalBytes,
        string? cachedBy)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        // Check if this exact depot+manifest combination already exists
        var existing = await context.PrefillCachedDepots
            .FirstOrDefaultAsync(d => d.DepotId == depotId && d.ManifestId == manifestId);

        if (existing != null)
        {
            // Update the cached timestamp
            existing.CachedAtUtc = DateTime.UtcNow;
            existing.CachedBy = cachedBy;
            _logger.LogDebug("Updated cache timestamp for depot {DepotId} manifest {ManifestId}",
                depotId, manifestId);
        }
        else
        {
            // Remove any old manifests for this depot (game was updated)
            var oldManifests = await context.PrefillCachedDepots
                .Where(d => d.DepotId == depotId && d.ManifestId != manifestId)
                .ToListAsync();

            if (oldManifests.Any())
            {
                context.PrefillCachedDepots.RemoveRange(oldManifests);
                _logger.LogInformation("Removed {Count} outdated manifests for depot {DepotId}",
                    oldManifests.Count, depotId);
            }

            // Add new cache entry
            context.PrefillCachedDepots.Add(new PrefillCachedDepot
            {
                AppId = appId,
                DepotId = depotId,
                ManifestId = manifestId,
                AppName = appName,
                TotalBytes = totalBytes,
                CachedAtUtc = DateTime.UtcNow,
                CachedBy = cachedBy
            });

            _logger.LogInformation("Cached depot {DepotId} for app {AppId} ({AppName}) - manifest {ManifestId}",
                depotId, appId, appName, manifestId);
        }

        await context.SaveChangesAsync();

        // Also update SteamDepotMappings so downloads can resolve game names
        // This captures mappings that PICS doesn't provide (e.g., private branches, unusual depot structures)
        await EnsureDepotMappingExistsAsync(context, appId, depotId, appName);
    }

    /// <summary>
    /// Ensures a depot mapping exists in SteamDepotMappings for download resolution.
    /// This is called when prefill records a cached depot, capturing mappings that PICS may not provide.
    /// </summary>
    private async Task EnsureDepotMappingExistsAsync(AppDbContext context, uint appId, uint depotId, string? appName)
    {
        try
        {
            // Check if mapping already exists
            var existingMapping = await context.SteamDepotMappings
                .FirstOrDefaultAsync(m => m.DepotId == depotId && m.AppId == appId);

            if (existingMapping != null)
            {
                // Update app name if we have a better one (not placeholder)
                if (!string.IsNullOrEmpty(appName) &&
                    !appName.StartsWith("App ") &&
                    !appName.StartsWith("Steam App ") &&
                    (string.IsNullOrEmpty(existingMapping.AppName) ||
                     existingMapping.AppName.StartsWith("App ") ||
                     existingMapping.AppName.StartsWith("Steam App ")))
                {
                    existingMapping.AppName = appName;
                    existingMapping.DiscoveredAt = DateTime.UtcNow;
                    await context.SaveChangesAsync();
                    _logger.LogDebug("Updated depot mapping name for {DepotId} -> {AppName}", depotId, appName);
                }
                return;
            }

            // Create new mapping - mark as owner since prefill knows the correct relationship
            var mapping = new SteamDepotMapping
            {
                DepotId = depotId,
                AppId = appId,
                AppName = appName ?? $"App {appId}",
                IsOwner = true,  // Prefill-discovered mappings are authoritative
                Source = "Prefill",
                DiscoveredAt = DateTime.UtcNow
            };

            context.SteamDepotMappings.Add(mapping);
            await context.SaveChangesAsync();
            _logger.LogInformation("Created depot mapping from prefill: depot {DepotId} -> app {AppId} ({AppName})",
                depotId, appId, appName);
        }
        catch (Exception ex)
        {
            // Don't fail the prefill cache operation if mapping update fails
            _logger.LogWarning(ex, "Failed to update depot mapping for depot {DepotId} app {AppId}", depotId, appId);
        }
    }

    /// <summary>
    /// Records multiple depots as cached (batch operation after app download).
    /// </summary>
    public async Task RecordCachedDepotsAsync(
        uint appId,
        string? appName,
        IEnumerable<(uint DepotId, ulong ManifestId, long TotalBytes)> depots,
        string? cachedBy)
    {
        foreach (var (depotId, manifestId, totalBytes) in depots)
        {
            await RecordCachedDepotAsync(appId, depotId, manifestId, appName, totalBytes, cachedBy);
        }
    }

    /// <summary>
    /// Checks if an app is fully cached (all its depots with current manifests are in the cache).
    /// </summary>
    /// <param name="appId">The app ID to check</param>
    /// <param name="depotManifests">Dictionary of depot ID to manifest ID that need to be cached</param>
    /// <returns>True if all depots are cached with matching manifests</returns>
    public async Task<bool> IsAppCachedAsync(uint appId, Dictionary<uint, ulong> depotManifests)
    {
        if (depotManifests == null || depotManifests.Count == 0)
            return false;

        await using var context = await _contextFactory.CreateDbContextAsync();

        var depotIds = depotManifests.Keys.ToList();

        // Get all cached depots for this app
        var cachedDepots = await context.PrefillCachedDepots
            .Where(d => depotIds.Contains(d.DepotId))
            .ToDictionaryAsync(d => d.DepotId, d => d.ManifestId);

        // Check if ALL depots are cached with matching manifest IDs
        foreach (var (depotId, requiredManifestId) in depotManifests)
        {
            if (!cachedDepots.TryGetValue(depotId, out var cachedManifestId) ||
                cachedManifestId != requiredManifestId)
            {
                _logger.LogDebug("App {AppId} depot {DepotId} not cached or outdated. Required: {Required}, Cached: {Cached}",
                    appId, depotId, requiredManifestId, cachedManifestId);
                return false;
            }
        }

        _logger.LogDebug("App {AppId} is fully cached (all {Count} depots match)", appId, depotManifests.Count);
        return true;
    }

    /// <summary>
    /// Gets the cache status for multiple apps.
    /// </summary>
    /// <param name="appManifests">Dictionary of app ID to (depot ID -> manifest ID) mappings</param>
    /// <returns>Dictionary of app ID to (isCached, cachedAtUtc)</returns>
    public async Task<Dictionary<uint, (bool IsCached, DateTime? CachedAtUtc)>> GetAppsCacheStatusAsync(
        Dictionary<uint, Dictionary<uint, ulong>> appManifests)
    {
        var result = new Dictionary<uint, (bool IsCached, DateTime? CachedAtUtc)>();

        await using var context = await _contextFactory.CreateDbContextAsync();

        // Get all depot IDs we need to check
        var allDepotIds = appManifests.Values.SelectMany(d => d.Keys).Distinct().ToList();

        // Fetch all cached depots in one query
        var cachedDepots = await context.PrefillCachedDepots
            .Where(d => allDepotIds.Contains(d.DepotId))
            .ToListAsync();

        var cachedLookup = cachedDepots.ToDictionary(d => (d.DepotId, d.ManifestId));

        foreach (var (appId, depotManifests) in appManifests)
        {
            var allCached = true;
            DateTime? latestCache = null;

            foreach (var (depotId, requiredManifestId) in depotManifests)
            {
                if (cachedLookup.TryGetValue((depotId, requiredManifestId), out var cached))
                {
                    latestCache = latestCache == null ? cached.CachedAtUtc
                        : (cached.CachedAtUtc > latestCache ? cached.CachedAtUtc : latestCache);
                }
                else
                {
                    allCached = false;
                    break;
                }
            }

            result[appId] = (allCached, allCached ? latestCache : null);
        }

        return result;
    }

    /// <summary>
    /// Gets all cached apps with their cache timestamps.
    /// </summary>
    public async Task<List<CachedAppInfo>> GetCachedAppsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var cachedApps = await context.PrefillCachedDepots
            .GroupBy(d => d.AppId)
            .Select(g => new CachedAppInfo
            {
                AppId = g.Key,
                AppName = g.First().AppName,
                DepotCount = g.Count(),
                TotalBytes = g.Sum(d => d.TotalBytes),
                CachedAtUtc = g.Max(d => d.CachedAtUtc),
                CachedBy = g.First().CachedBy
            })
            .OrderByDescending(a => a.CachedAtUtc)
            .ToListAsync();

        return cachedApps;
    }

    /// <summary>
    /// Clears the cache for a specific app (for force re-download).
    /// </summary>
    public async Task ClearAppCacheAsync(uint appId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var entries = await context.PrefillCachedDepots
            .Where(d => d.AppId == appId)
            .ToListAsync();

        if (entries.Any())
        {
            context.PrefillCachedDepots.RemoveRange(entries);
            await context.SaveChangesAsync();
            _logger.LogInformation("Cleared cache for app {AppId} ({Count} depots)", appId, entries.Count);
        }
    }

    /// <summary>
    /// Clears the entire prefill cache (for admin use).
    /// </summary>
    public async Task ClearAllCacheAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var count = await context.PrefillCachedDepots.CountAsync();
        await context.PrefillCachedDepots.ExecuteDeleteAsync();
        _logger.LogInformation("Cleared entire prefill cache ({Count} entries)", count);
    }

    /// <summary>
    /// Gets all cached depots in the format needed for the prefill daemon.
    /// Returns the complete list of depot/manifest pairs that are currently cached.
    /// </summary>
    public async Task<List<(uint AppId, uint DepotId, ulong ManifestId)>> GetAllCachedDepotsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var cachedDepots = await context.PrefillCachedDepots
            .Select(d => new { d.AppId, d.DepotId, d.ManifestId })
            .ToListAsync();

        _logger.LogDebug("Retrieved {Count} cached depot manifests from database", cachedDepots.Count);

        return cachedDepots.Select(d => (d.AppId, d.DepotId, d.ManifestId)).ToList();
    }

    /// <summary>
    /// Gets cached depots for specific apps in the format needed for the prefill daemon.
    /// </summary>
    public async Task<List<(uint AppId, uint DepotId, ulong ManifestId)>> GetCachedDepotsForAppsAsync(IEnumerable<uint> appIds)
    {
        var appIdList = appIds?.Distinct().ToList() ?? new List<uint>();
        if (appIdList.Count == 0)
        {
            return new List<(uint AppId, uint DepotId, ulong ManifestId)>();
        }

        await using var context = await _contextFactory.CreateDbContextAsync();

        var cachedDepots = await context.PrefillCachedDepots
            .Where(d => appIdList.Contains(d.AppId))
            .Select(d => new { d.AppId, d.DepotId, d.ManifestId })
            .ToListAsync();

        _logger.LogDebug("Retrieved {Count} cached depot manifests for {AppCount} apps", cachedDepots.Count, appIdList.Count);

        return cachedDepots.Select(d => (d.AppId, d.DepotId, d.ManifestId)).ToList();
    }
}

/// <summary>
/// Summary info about a cached app.
/// </summary>
public class CachedAppInfo
{
    public uint AppId { get; set; }
    public string? AppName { get; set; }
    public int DepotCount { get; set; }
    public long TotalBytes { get; set; }
    public DateTime CachedAtUtc { get; set; }
    public string? CachedBy { get; set; }
}
