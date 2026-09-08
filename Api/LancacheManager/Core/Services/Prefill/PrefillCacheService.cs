using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

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
    /// <returns>
    /// True when this depot was newly recorded, false when an entry already covering the same
    /// manifest only had its timestamp refreshed, and false as well when a concurrent writer won
    /// the insert. All three mean the row is there, so false is not a failure to report upwards.
    /// </returns>
    public async Task<bool> RecordCachedDepotAsync(
        long appId,
        long depotId,
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

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            // Depot+manifest is unique, so two containers prefilling the same game both read no row
            // and both insert; one of them loses. The row still exists and the rest of the batch
            // still has to be written, so this is not a failure to report upwards.
            _logger.LogDebug(ex, "Depot {DepotId} manifest {ManifestId} was recorded by another writer",
                depotId, manifestId);
            return false;
        }

        // Also update SteamDepotMappings so downloads can resolve game names
        // This captures mappings that PICS doesn't provide (e.g., private branches, unusual depot structures)
        await EnsureDepotMappingExistsAsync(context, appId, depotId, appName);

        // Re-recording a depot+manifest already in the table only moves its timestamp, so every app
        // stays on the side of the cached/not-cached line it was already on. Only the insert branch
        // moves one across, which is what a listener needs to hear about.
        return existing == null;
    }

    /// <summary>
    /// Ensures a depot mapping exists in SteamDepotMappings for download resolution.
    /// This is called when prefill records a cached depot, capturing mappings that PICS may not provide.
    /// </summary>
    private async Task EnsureDepotMappingExistsAsync(AppDbContext context, long appId, long depotId, string? appName)
    {
        try
        {
            var depotMappings = await context.SteamDepotMappings
                .Where(m => m.DepotId == depotId)
                .ToListAsync();
            var existingMapping = depotMappings.FirstOrDefault(m => m.AppId == appId);

            if (existingMapping != null)
            {
                var changed = false;

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
                    changed = true;
                }

                // A PICS owner describes the depot itself. A prefill mapping only records which
                // selected app downloaded it, so it must not become a second owner.
                if (existingMapping.IsOwner &&
                    existingMapping.Source == "Prefill" &&
                    depotMappings.Any(m => m.Id != existingMapping.Id && m.IsOwner))
                {
                    existingMapping.IsOwner = false;
                    changed = true;
                }

                if (changed)
                {
                    await context.SaveChangesAsync();
                    _logger.LogDebug("Updated depot mapping for {DepotId} -> {AppName}", depotId, appName);
                }
                return;
            }

            // Keep the existing PICS owner when the selected app downloaded a DLC or shared depot.
            var mapping = new SteamDepotMapping
            {
                DepotId = depotId,
                AppId = appId,
                AppName = appName ?? $"App {appId}",
                IsOwner = !depotMappings.Any(m => m.IsOwner),
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
    /// <returns>True when at least one depot in the batch was newly recorded.</returns>
    public async Task<bool> RecordCachedDepotsAsync(
        long appId,
        string? appName,
        IEnumerable<(long DepotId, ulong ManifestId, long TotalBytes)> depots,
        string? cachedBy)
    {
        var recorded = false;

        foreach (var (depotId, manifestId, totalBytes) in depots)
        {
            // |= rather than a short-circuiting || so every depot is still recorded once one has been.
            recorded |= await RecordCachedDepotAsync(appId, depotId, manifestId, appName, totalBytes, cachedBy);
        }

        return recorded;
    }

    /// <summary>
    /// Gets the cache status for multiple apps.
    /// </summary>
    /// <param name="appManifests">Dictionary of app ID to (depot ID -> manifest ID) mappings</param>
    /// <returns>Dictionary of app ID to (isCached, cachedAtUtc)</returns>
    public async Task<Dictionary<long, (bool IsCached, DateTime? CachedAtUtc)>> GetAppsCacheStatusAsync(
        Dictionary<long, Dictionary<long, ulong>> appManifests)
    {
        var result = new Dictionary<long, (bool IsCached, DateTime? CachedAtUtc)>();

        await using var context = await _contextFactory.CreateDbContextAsync();

        // Get all depot IDs we need to check
        var allDepotIds = appManifests.Values.SelectMany(d => d.Keys).Distinct().ToList();

        // Fetch all cached depots in one query
        var cachedDepots = await context.PrefillCachedDepots
            .AsNoTracking()
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
    public async Task<bool> RecordCachedAppAsync(
        PrefillPlatform platform, string appId, string? appName, long totalBytes, string? cachedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        await using var context = await _contextFactory.CreateDbContextAsync();
        var existing = await context.PrefillCachedApps
            .FirstOrDefaultAsync(a => a.Platform == platform && a.AppId == appId);
        var app = existing ?? new PrefillCachedApp { Platform = platform, AppId = appId };
        app.CachedAtUtc = DateTime.UtcNow;
        app.TotalBytes = totalBytes;
        if (!string.IsNullOrWhiteSpace(appName)) app.AppName = appName;
        if (!string.IsNullOrWhiteSpace(cachedBy)) app.CachedBy = cachedBy;
        if (existing == null) context.PrefillCachedApps.Add(app);
        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogDebug(ex, "App {AppId} for {Platform} was recorded by another writer", appId, platform);
            return false;
        }
        return existing == null;
    }

    public async Task<List<CachedAppInfo>> GetCachedAppsAsync(PrefillPlatform platform, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var evicted = MatchingCachedApps(context, context.Downloads.Where(d => d.IsEvicted));
        var cachedApps = await context.PrefillCachedApps
            .AsNoTracking()
            .Where(a => a.Platform == platform && !evicted.Any(e => e.Id == a.Id))
            .Select(a => new CachedAppInfo
            {
                AppId = a.AppId,
                AppName = a.AppName,
                DepotCount = platform == PrefillPlatform.Steam
                    ? context.PrefillCachedDepots.Count(d => d.AppId.ToString() == a.AppId) : 0,
                TotalBytes = a.TotalBytes,
                CachedAtUtc = a.CachedAtUtc,
                CachedBy = a.CachedBy
            })
            .OrderByDescending(a => a.CachedAtUtc)
            .ToListAsync(cancellationToken);

        return cachedApps;
    }

    internal static IQueryable<PrefillCachedApp> MatchingCachedApps(AppDbContext context, IQueryable<Download> downloads)
    {
        var steam = PrefillPlatform.Steam.ToService();
        var epic = PrefillPlatform.Epic.ToService();
        var xbox = PrefillPlatform.Xbox.ToService();
        var battleNet = PrefillPlatform.BattleNet.ToService();
        var riot = PrefillPlatform.Riot.ToService();
        return context.PrefillCachedApps.Where(a => downloads.Any(d =>
            (a.Platform == PrefillPlatform.Steam && d.Service == steam && d.GameAppId.ToString() == a.AppId)
            || (a.Platform == PrefillPlatform.Epic && d.Service == epic
                && ((!string.IsNullOrWhiteSpace(d.EpicAppId) && d.EpicAppId == a.AppId)
                    || (string.IsNullOrWhiteSpace(d.EpicAppId) && !string.IsNullOrWhiteSpace(a.AppName) && a.AppName == d.GameName)))
            || (a.Platform == PrefillPlatform.Xbox && d.Service == xbox
                && ((!string.IsNullOrWhiteSpace(d.XboxProductId) && d.XboxProductId == a.AppId)
                    || (string.IsNullOrWhiteSpace(d.XboxProductId) && !string.IsNullOrWhiteSpace(a.AppName) && a.AppName == d.GameName)))
            || (((a.Platform == PrefillPlatform.BattleNet && d.Service == battleNet)
                    || (a.Platform == PrefillPlatform.Riot && d.Service == riot))
                && !string.IsNullOrWhiteSpace(a.AppName) && a.AppName == d.GameName)));
    }

    /// <summary>
    /// Clears the cache for a specific app (for force re-download).
    /// </summary>
    /// <returns>
    /// The number of depot rows removed. Cached status is read per depot+manifest with no app term,
    /// so an app whose depots all sit under another app's rows reads as cached while owning none,
    /// and clearing it removes nothing.
    /// </returns>
    public async Task<(int RemovedApps, int RemovedDepots)> ClearAppCacheAsync(PrefillPlatform platform, string appId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var apps = await context.PrefillCachedApps.Where(a => a.Platform == platform && a.AppId == appId).ToListAsync();
        var entries = platform == PrefillPlatform.Steam && long.TryParse(appId, out var numericAppId)
            ? await context.PrefillCachedDepots.Where(d => d.AppId == numericAppId).ToListAsync()
            : [];
        context.PrefillCachedApps.RemoveRange(apps);
        context.PrefillCachedDepots.RemoveRange(entries);
        await context.SaveChangesAsync();
        return (apps.Count, entries.Count);
    }

    /// <summary>
    /// Clears the entire prefill cache (for admin use).
    /// </summary>
    public async Task<(int RemovedApps, int RemovedDepots)> ClearAllCacheAsync(PrefillPlatform platform)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var apps = await context.PrefillCachedApps.Where(a => a.Platform == platform).ToListAsync();
        var depots = platform == PrefillPlatform.Steam ? await context.PrefillCachedDepots.ToListAsync() : [];
        context.PrefillCachedApps.RemoveRange(apps);
        context.PrefillCachedDepots.RemoveRange(depots);
        await context.SaveChangesAsync();
        return (apps.Count, depots.Count);
    }

    /// <summary>
    /// Gets all cached depots in the format needed for the prefill daemon.
    /// Returns the complete list of depot/manifest pairs that are currently cached.
    /// </summary>
    public async Task<List<(long AppId, long DepotId, ulong ManifestId)>> GetAllCachedDepotsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var cachedDepots = await context.PrefillCachedDepots
            .AsNoTracking()
            .Select(d => new { d.AppId, d.DepotId, d.ManifestId })
            .ToListAsync();

        _logger.LogDebug("Retrieved {Count} cached depot manifests from database", cachedDepots.Count);

        return cachedDepots.Select(d => ((long)d.AppId, (long)d.DepotId, d.ManifestId)).ToList();
    }

    /// <summary>
    /// Gets cached depots for specific apps in the format needed for the prefill daemon.
    /// </summary>
    public async Task<List<(long AppId, long DepotId, ulong ManifestId)>> GetCachedDepotsForAppsAsync(IEnumerable<long> appIds)
    {
        var appIdList = appIds?.Distinct().ToList() ?? new List<long>();
        if (appIdList.Count == 0)
        {
            return new List<(long AppId, long DepotId, ulong ManifestId)>();
        }

        await using var context = await _contextFactory.CreateDbContextAsync();

        var cachedDepots = await context.PrefillCachedDepots
            .AsNoTracking()
            .Where(d => appIdList.Contains(d.AppId))
            .Select(d => new { d.AppId, d.DepotId, d.ManifestId })
            .ToListAsync();

        _logger.LogDebug("Retrieved {Count} cached depot manifests for {AppCount} apps", cachedDepots.Count, appIdList.Count);

        return cachedDepots.Select(d => ((long)d.AppId, (long)d.DepotId, d.ManifestId)).ToList();
    }
}

/// <summary>
/// Summary info about a cached app.
/// </summary>
public class CachedAppInfo
{
    public string AppId { get; set; } = string.Empty;
    public string? AppName { get; set; }
    public int DepotCount { get; set; }
    public long TotalBytes { get; set; }
    public DateTime CachedAtUtc { get; set; }
    public string? CachedBy { get; set; }
}
