using System.Data;
using LancacheManager.Core.Services.SteamPrefill;
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
    internal const string SteamCacheReceipt = "steam-depots-v1";
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

    public async Task<PrefillCacheSnapshot> GetCacheSnapshotAsync(
        IReadOnlyCollection<uint> appIds,
        CancellationToken cancellationToken = default)
    {
        var requested = appIds.Distinct().Order().ToArray();
        var requestedText = requested.Select(appId => appId.ToString()).ToArray();
        await using var retryContext = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var execution = retryContext.Database.CreateExecutionStrategy();
        return await execution.ExecuteAsync(async attemptToken =>
        {
            await using var context = await _contextFactory.CreateDbContextAsync(attemptToken);
            await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                attemptToken);
            var receipts = await context.PrefillCachedApps
                .AsNoTracking()
                .Where(app => app.Platform == PrefillPlatform.Steam && requestedText.Contains(app.AppId))
                .Select(app => new { app.AppId, app.CacheRevision })
                .ToDictionaryAsync(
                    app => app.AppId,
                    app => app.CacheRevision,
                    StringComparer.Ordinal,
                    attemptToken);
            var depots = await context.PrefillCachedDepots
                .AsNoTracking()
                .OrderBy(depot => depot.DepotId)
                .ThenBy(depot => depot.ManifestId)
                .Select(depot => new CachedDepotInput
                {
                    AppId = depot.AppId,
                    DepotId = depot.DepotId,
                    ManifestId = depot.ManifestId
                })
                .ToListAsync(attemptToken);
            await transaction.CommitAsync(attemptToken);

            return new PrefillCacheSnapshot
            {
                Depots = depots,
                Scope = requested.Select(appId =>
                {
                    var text = appId.ToString();
                    var authority = !receipts.TryGetValue(text, out var receipt)
                        ? CacheAuthority.Empty
                        : StringComparer.Ordinal.Equals(receipt, SteamCacheReceipt)
                            ? CacheAuthority.Snapshot
                            : CacheAuthority.Absent;
                    return new CacheAppScope { AppId = appId, Authority = authority };
                }).ToList()
            };
        }, cancellationToken);
    }

    public async Task<bool> RecordSteamCacheAsync(
        long appId,
        string? appName,
        long totalBytes,
        string? cachedBy,
        IReadOnlyList<DepotManifestProgressInfo>? depots,
        CancellationToken cancellationToken = default)
    {
        if (appId is <= 0 or > uint.MaxValue || totalBytes < 0 || depots is not { Count: > 0 })
            return false;

        var accepted = new List<DepotManifestProgressInfo>();
        foreach (var group in depots.GroupBy(depot => depot.DepotId).OrderBy(group => group.Key))
        {
            if (group.Key is <= 0 or > uint.MaxValue
                || group.Any(depot => depot.ManifestId == 0 || depot.TotalBytes < 0)
                || group.Select(depot => depot.ManifestId).Distinct().Skip(1).Any())
            {
                return false;
            }

            var first = group.First();
            accepted.Add(new DepotManifestProgressInfo
            {
                DepotId = first.DepotId,
                ManifestId = first.ManifestId,
                TotalBytes = group.Max(depot => depot.TotalBytes)
            });
        }

        var appIdText = appId.ToString();
        var now = DateTime.UtcNow;
        await using var retryContext = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var execution = retryContext.Database.CreateExecutionStrategy();
        var changed = await execution.ExecuteAsync(async attemptToken =>
        {
            await using var context = await _contextFactory.CreateDbContextAsync(attemptToken);
            var relational = context.Database.IsRelational();
            await using var transaction = relational
                ? await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, attemptToken)
                : null;
            if (relational)
            {
                var cacheLock = unchecked((long)0x4C434D0000000000UL | (uint)appId);
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock({cacheLock})",
                    attemptToken);
            }

            var existingApp = await context.PrefillCachedApps
                .SingleOrDefaultAsync(app => app.Platform == PrefillPlatform.Steam && app.AppId == appIdText,
                    attemptToken);
            var depotIds = accepted.Select(depot => depot.DepotId).ToArray();
            var existingDepots = await context.PrefillCachedDepots
                .Where(depot => depotIds.Contains(depot.DepotId))
                .ToListAsync(attemptToken);
            var cacheChanged = existingApp?.CacheRevision != SteamCacheReceipt
                || accepted.Any(depot => !existingDepots.Any(existing =>
                    existing.DepotId == depot.DepotId && existing.ManifestId == depot.ManifestId))
                || existingDepots.Any(existing => accepted.Any(depot =>
                    depot.DepotId == existing.DepotId && depot.ManifestId != existing.ManifestId));

            if (!relational)
            {
                var app = existingApp ?? new PrefillCachedApp
                {
                    Platform = PrefillPlatform.Steam,
                    AppId = appIdText
                };
                app.AppName = appName ?? app.AppName;
                app.TotalBytes = totalBytes;
                app.CachedAtUtc = now;
                app.CachedBy = cachedBy ?? app.CachedBy;
                app.CacheRevision = SteamCacheReceipt;
                if (existingApp is null) context.PrefillCachedApps.Add(app);
                foreach (var depot in accepted)
                {
                    context.PrefillCachedDepots.RemoveRange(existingDepots.Where(existing =>
                        existing.DepotId == depot.DepotId && existing.ManifestId != depot.ManifestId));
                    var existing = existingDepots.SingleOrDefault(row =>
                        row.DepotId == depot.DepotId && row.ManifestId == depot.ManifestId);
                    if (existing is null)
                    {
                        context.PrefillCachedDepots.Add(new PrefillCachedDepot
                        {
                            AppId = appId,
                            DepotId = depot.DepotId,
                            ManifestId = depot.ManifestId,
                            AppName = appName,
                            TotalBytes = depot.TotalBytes,
                            CachedAtUtc = now,
                            CachedBy = cachedBy
                        });
                    }
                    else
                    {
                        existing.CachedAtUtc = now;
                        existing.CachedBy = cachedBy;
                    }
                }
                await context.SaveChangesAsync(attemptToken);
                return cacheChanged;
            }

            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "PrefillCachedApps"
                    ("Platform", "AppId", "AppName", "CachedAtUtc", "CachedBy", "TotalBytes", "CacheRevision")
                VALUES ({PrefillPlatform.Steam.ToString()}, {appIdText}, {appName}, {now}, {cachedBy}, {totalBytes}, {SteamCacheReceipt})
                ON CONFLICT ("Platform", "AppId") DO UPDATE SET
                    "AppName" = COALESCE(EXCLUDED."AppName", "PrefillCachedApps"."AppName"),
                    "CachedAtUtc" = EXCLUDED."CachedAtUtc",
                    "CachedBy" = COALESCE(EXCLUDED."CachedBy", "PrefillCachedApps"."CachedBy"),
                    "TotalBytes" = EXCLUDED."TotalBytes",
                    "CacheRevision" = EXCLUDED."CacheRevision"
                """, attemptToken);
            foreach (var depot in accepted)
            {
                var manifestId = (decimal)depot.ManifestId;
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM "PrefillCachedDepots"
                    WHERE "DepotId" = {depot.DepotId} AND "ManifestId" <> {manifestId}
                    """, attemptToken);
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "PrefillCachedDepots"
                        ("AppId", "DepotId", "ManifestId", "AppName", "CachedAtUtc", "CachedBy", "TotalBytes")
                    VALUES ({appId}, {depot.DepotId}, {manifestId}, {appName}, {now}, {cachedBy}, {depot.TotalBytes})
                    ON CONFLICT ("DepotId", "ManifestId") DO UPDATE SET
                        "CachedAtUtc" = EXCLUDED."CachedAtUtc",
                        "CachedBy" = EXCLUDED."CachedBy"
                    """, attemptToken);
            }
            await transaction!.CommitAsync(attemptToken);
            return cacheChanged;
        }, cancellationToken);

        try
        {
            await using var mappingContext = await _contextFactory.CreateDbContextAsync(CancellationToken.None);
            foreach (var depot in accepted)
            {
                await EnsureDepotMappingExistsAsync(mappingContext, appId, depot.DepotId, appName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update depot mappings for app {AppId}", appId);
        }
        return changed;
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
        PrefillPlatform platform, string appId, string? appName, long totalBytes, string? cachedBy,
        string? cacheRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        await using var context = await _contextFactory.CreateDbContextAsync();
        var existing = await context.PrefillCachedApps
            .FirstOrDefaultAsync(a => a.Platform == platform && a.AppId == appId);
        var app = existing ?? new PrefillCachedApp { Platform = platform, AppId = appId };
        var changed = existing == null || !StringComparer.Ordinal.Equals(existing.CacheRevision, cacheRevision);
        app.CachedAtUtc = DateTime.UtcNow;
        app.TotalBytes = totalBytes;
        app.CacheRevision = cacheRevision;
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
        return changed;
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
                CachedBy = a.CachedBy,
                CacheRevision = a.CacheRevision
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

}
