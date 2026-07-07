using System.Text.Json;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using DetectionOperationResponse = LancacheManager.Core.Services.GameCacheDetectionService.DetectionOperationResponse;

namespace LancacheManager.Core.Services;

public sealed partial class GameCacheDetectionDataService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<GameCacheDetectionDataService> _logger;

    public GameCacheDetectionDataService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<GameCacheDetectionDataService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    internal async Task<CachedGameUnevictTargets> GetGamesToUnevictAsync(
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        var evictedSteamGameIds = await context.CachedGameDetections
            .Where(g => g.IsEvicted && g.EpicAppId == null)
            .Select(g => g.GameAppId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var steamGameIdsToUnevict = evictedSteamGameIds.Count == 0
            ? new List<long>()
            : await context.Downloads
                .Where(d => d.GameAppId != null
                         && evictedSteamGameIds.Contains(d.GameAppId.Value)
                         && !d.IsEvicted)
                .Select(d => d.GameAppId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);

        var evictedEpicAppIds = await context.CachedGameDetections
            .Where(g => g.IsEvicted && g.EpicAppId != null)
            .Select(g => g.EpicAppId!)
            .Distinct()
            .ToListAsync(cancellationToken);

        var epicAppIdsToUnevict = evictedEpicAppIds.Count == 0
            ? new List<string>()
            : await context.Downloads
                .Where(d => d.EpicAppId != null
                         && evictedEpicAppIds.Contains(d.EpicAppId)
                         && !d.IsEvicted)
                .Select(d => d.EpicAppId!)
                .Distinct()
                .ToListAsync(cancellationToken);

        // Named (Blizzard/Riot) arm: evicted named detections keyed by (Service, GameName), matched
        // to non-evicted named Downloads (GameAppId/EpicAppId both null, GameName set). Without this
        // a re-cached Blizzard/Riot game would never self-heal (the Steam arm above only matches
        // Downloads with a non-null GameAppId, which named games never have).
        var evictedNamedKeys = await context.CachedGameDetections
            .Where(g => g.IsEvicted && g.EpicAppId == null && g.GameAppId == 0 && g.Service != null
                     && g.GameName != null && g.GameName != "")
            .Select(g => new { Service = g.Service!.ToLower(), g.GameName })
            .Distinct()
            .ToListAsync(cancellationToken);

        var namedKeysToUnevict = new List<NamedGameKey>();
        if (evictedNamedKeys.Count > 0)
        {
            var evictedNamedServices = evictedNamedKeys.Select(k => k.Service).Distinct().ToList();
            var liveNamedDownloads = await context.Downloads
                .Where(d => d.GameAppId == null
                         && d.EpicAppId == null
                         && d.Service != null
                         && d.GameName != null
                         && d.GameName != ""
                         && !d.IsEvicted
                         && evictedNamedServices.Contains(d.Service!.ToLower()))
                .Select(d => new { Service = d.Service!.ToLower(), GameName = d.GameName! })
                .Distinct()
                .ToListAsync(cancellationToken);

            var liveNamedSet = liveNamedDownloads
                .Select(d => (d.Service, d.GameName))
                .ToHashSet();

            namedKeysToUnevict = evictedNamedKeys
                .Where(k => liveNamedSet.Contains((k.Service, k.GameName)))
                .Select(k => new NamedGameKey(k.Service, k.GameName))
                .Distinct()
                .ToList();
        }

        return new CachedGameUnevictTargets(steamGameIdsToUnevict, epicAppIdsToUnevict, namedKeysToUnevict);
    }

    /// <summary>
    /// Downloads-keyed service self-heal companion to <see cref="GetGamesToUnevictAsync"/>.
    /// Returns the lowercased names of services whose <see cref="CachedServiceDetection.IsEvicted"/>
    /// is currently true but which now have at least one non-evicted service-scoped Download
    /// (Service==name case-insensitive, GameAppId==null &amp;&amp; EpicAppId==null) - i.e. the cache
    /// files reappeared on disk. Keying off Downloads.IsEvicted (the disk-probe signal) instead of
    /// the stale CachedServiceDetection.CacheFilesFound snapshot lets a re-cached service self-heal
    /// within the eviction scan, matching the games path.
    /// </summary>
    internal async Task<List<string>> GetServicesToUnevictAsync(
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        var evictedServiceNames = await context.CachedServiceDetections
            .Where(s => s.IsEvicted)
            .Select(s => s.ServiceName.ToLower())
            .Distinct()
            .ToListAsync(cancellationToken);

        if (evictedServiceNames.Count == 0)
        {
            return new List<string>();
        }

        // Self-heal mirror of the alive test in SaveServicesAsync: a falsely-evicted service whose
        // named-game (GameName-bearing) cache is back on disk must un-evict. Count ALL non-evicted
        // service-scoped Downloads including named-game ones - do NOT filter GameName==null here.
        return await context.Downloads
            .Where(d => d.GameAppId == null
                     && d.EpicAppId == null
                     && d.Service != null
                     && !d.IsEvicted
                     && evictedServiceNames.Contains(d.Service!.ToLower()))
            .Select(d => d.Service!.ToLower())
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<DetectionOperationResponse?> LoadDetectionAsync(
        CancellationToken cancellationToken = default,
        bool includeCacheFilePaths = true)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var cachedGames = await LoadGameEntitiesAsync(dbContext, includeCacheFilePaths, cancellationToken);
        var cachedServices = await LoadServiceEntitiesAsync(dbContext, includeCacheFilePaths, cancellationToken);

        var games = cachedGames.Select(ToGameCacheInfo).ToList();
        var services = cachedServices.Select(ToServiceCacheInfo).ToList();

        var steamEvictedMap = await dbContext.Downloads
            .Where(d => d.IsEvicted && d.GameAppId != null && d.EpicAppId == null)
            .GroupBy(d => d.GameAppId!.Value)
            .Select(g => new
            {
                Key = g.Key,
                Count = g.Count(),
                Bytes = (ulong)g.Sum(x => x.CacheHitBytes + x.CacheMissBytes)
            })
            .ToDictionaryAsync(x => x.Key, x => (x.Count, x.Bytes), cancellationToken);

        var epicEvictedMap = await dbContext.Downloads
            .Where(d => d.IsEvicted && d.EpicAppId != null)
            .GroupBy(d => d.EpicAppId!)
            .Select(g => new
            {
                Key = g.Key,
                Count = g.Count(),
                Bytes = (ulong)g.Sum(x => x.CacheHitBytes + x.CacheMissBytes)
            })
            .ToDictionaryAsync(x => x.Key, x => (x.Count, x.Bytes), cancellationToken);

        // Service evicted accounting: only NULL-GameName service-residual Downloads (shared/agnostic
        // paths). Named (Blizzard/Riot) games carry GameName, so they are excluded here and counted
        // separately via namedEvictedMap below - otherwise they'd be double-claimed by both the
        // service and the game eviction accounting.
        var serviceEvictedMap = await dbContext.Downloads
            .Where(d => d.IsEvicted && d.GameAppId == null && d.EpicAppId == null && d.Service != null && d.GameName == null)
            .GroupBy(d => d.Service!.ToLower())
            .Select(g => new
            {
                Key = g.Key,
                Count = g.Count(),
                Bytes = (ulong)g.Sum(x => x.CacheHitBytes + x.CacheMissBytes)
            })
            .ToDictionaryAsync(x => x.Key, x => (x.Count, x.Bytes), cancellationToken);

        // Named (Blizzard/Riot) evicted accounting keyed by (Service, GameName).
        var namedEvictedFlat = await dbContext.Downloads
            .Where(d => d.IsEvicted && d.GameAppId == null && d.EpicAppId == null && d.Service != null && d.GameName != null)
            .GroupBy(d => new { Service = d.Service!.ToLower(), GameName = d.GameName! })
            .Select(g => new
            {
                g.Key.Service,
                g.Key.GameName,
                Count = g.Count(),
                Bytes = (ulong)g.Sum(x => x.CacheHitBytes + x.CacheMissBytes)
            })
            .ToListAsync(cancellationToken);

        var namedEvictedMap = namedEvictedFlat
            .ToDictionary(x => (x.Service, x.GameName), x => (x.Count, x.Bytes));

        foreach (var game in games)
        {
            if (game.EpicAppId != null)
            {
                if (epicEvictedMap.TryGetValue(game.EpicAppId, out var epicEntry))
                {
                    game.EvictedDownloadsCount = epicEntry.Count;
                    game.EvictedBytes = epicEntry.Bytes;
                }
            }
            else if (game.GameAppId == 0 && game.Service != null)
            {
                if (namedEvictedMap.TryGetValue((game.Service.ToLower(), game.GameName), out var namedEntry))
                {
                    game.EvictedDownloadsCount = namedEntry.Count;
                    game.EvictedBytes = namedEntry.Bytes;
                }
            }
            else if (steamEvictedMap.TryGetValue(game.GameAppId, out var steamEntry))
            {
                game.EvictedDownloadsCount = steamEntry.Count;
                game.EvictedBytes = steamEntry.Bytes;
            }
        }

        foreach (var service in services)
        {
            var key = service.ServiceName.ToLower();
            if (serviceEvictedMap.TryGetValue(key, out var svcEntry))
            {
                service.EvictedDownloadsCount = svcEntry.Count;
                service.EvictedBytes = svcEntry.Bytes;
            }
        }

        var steamEvictedUrlFlat = await dbContext.LogEntries
            .AsNoTracking()
            .Where(le => le.DownloadId != null
                && le.Download != null
                && le.Download.IsEvicted
                && le.Download.GameAppId != null
                && le.Download.EpicAppId == null)
            .Select(le => new { GameAppId = le.Download!.GameAppId!.Value, le.Url })
            .Distinct()
            .ToListAsync(cancellationToken);

        var steamEvictedUrlMap = steamEvictedUrlFlat
            .GroupBy(x => x.GameAppId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Url).Take(20).ToList());

        var epicEvictedUrlFlat = await dbContext.LogEntries
            .AsNoTracking()
            .Where(le => le.DownloadId != null
                && le.Download != null
                && le.Download.IsEvicted
                && le.Download.EpicAppId != null)
            .Select(le => new { EpicAppId = le.Download!.EpicAppId!, le.Url })
            .Distinct()
            .ToListAsync(cancellationToken);

        var epicEvictedUrlMap = epicEvictedUrlFlat
            .GroupBy(x => x.EpicAppId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Url).Take(20).ToList());

        var serviceEvictedUrlFlat = await dbContext.LogEntries
            .AsNoTracking()
            .Where(le => le.DownloadId != null
                && le.Download != null
                && le.Download.IsEvicted
                && le.Download.GameAppId == null
                && le.Download.EpicAppId == null
                && le.Download.Service != null
                && le.Download.GameName == null)
            .Select(le => new { le.Download!.Service, le.Url })
            .Distinct()
            .ToListAsync(cancellationToken);

        var serviceEvictedUrlMap = serviceEvictedUrlFlat
            .GroupBy(x => x.Service!.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Select(x => x.Url).Take(20).ToList());

        var namedEvictedUrlFlat = await dbContext.LogEntries
            .AsNoTracking()
            .Where(le => le.DownloadId != null
                && le.Download != null
                && le.Download.IsEvicted
                && le.Download.GameAppId == null
                && le.Download.EpicAppId == null
                && le.Download.Service != null
                && le.Download.GameName != null)
            .Select(le => new { le.Download!.Service, le.Download.GameName, le.Url })
            .Distinct()
            .ToListAsync(cancellationToken);

        var namedEvictedUrlMap = namedEvictedUrlFlat
            .GroupBy(x => (x.Service!.ToLowerInvariant(), x.GameName!))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Url).Take(20).ToList());

        var steamEvictedDepotFlat = await dbContext.Downloads
            .AsNoTracking()
            .Where(d => d.IsEvicted && d.GameAppId != null && d.EpicAppId == null && d.DepotId != null)
            .Select(d => new { GameAppId = d.GameAppId!.Value, DepotId = (uint)d.DepotId!.Value })
            .Distinct()
            .ToListAsync(cancellationToken);

        var steamEvictedDepotMap = steamEvictedDepotFlat
            .GroupBy(x => x.GameAppId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.DepotId).ToList());

        foreach (var game in games)
        {
            if (game.EpicAppId != null)
            {
                if (epicEvictedUrlMap.TryGetValue(game.EpicAppId, out var epicUrls))
                {
                    game.EvictedSampleUrls = epicUrls;
                }
            }
            else if (game.GameAppId == 0 && game.Service != null)
            {
                // Named (Blizzard/Riot) game: no depots; sample URLs keyed by (Service, GameName).
                if (namedEvictedUrlMap.TryGetValue((game.Service.ToLowerInvariant(), game.GameName), out var namedUrls))
                {
                    game.EvictedSampleUrls = namedUrls;
                }
            }
            else
            {
                if (steamEvictedUrlMap.TryGetValue(game.GameAppId, out var steamUrls))
                {
                    game.EvictedSampleUrls = steamUrls;
                }

                if (steamEvictedDepotMap.TryGetValue(game.GameAppId, out var depotIds))
                {
                    game.EvictedDepotIds = depotIds;
                }
            }
        }

        foreach (var service in services)
        {
            var key = service.ServiceName.ToLower();
            if (serviceEvictedUrlMap.TryGetValue(key, out var svcUrls))
            {
                service.EvictedSampleUrls = svcUrls;
            }
        }

        await EnrichImageUrlsAsync(dbContext, games, cancellationToken);

        if (games.Count == 0 && services.Count == 0)
        {
            return null;
        }

        var lastDetectedTime = DateTime.MinValue;
        if (cachedGames.Count > 0)
        {
            lastDetectedTime = cachedGames.Max(g => g.LastDetectedUtc);
        }

        if (cachedServices.Count > 0)
        {
            var servicesMaxTime = cachedServices.Max(s => s.LastDetectedUtc);
            if (servicesMaxTime > lastDetectedTime)
            {
                lastDetectedTime = servicesMaxTime;
            }
        }

        var loadedStageKey = games.Count > 0 && services.Count > 0
            ? "signalr.gameDetect.loaded.gamesAndServices"
            : "signalr.gameDetect.loaded.gamesOnly";

        var steamCount = games.Count(g => string.IsNullOrEmpty(g.EpicAppId) && !g.IsEvicted);
        var epicCount = games.Count(g => !string.IsNullOrEmpty(g.EpicAppId) && !g.IsEvicted);
        var evictedCount = games.Count(g => g.IsEvicted);
        _logger.LogDebug(
            "[GameDetection] === Detection Summary (cache load) === Steam: {Steam} | Epic: {Epic} | Total: {Total} | Evicted: {Evicted}",
            steamCount,
            epicCount,
            steamCount + epicCount,
            evictedCount);

        var (diskSummary, summaryComputedAt) = await LoadDetectionSummaryAsync(dbContext, cancellationToken);

        return new DetectionOperationResponse
        {
            OperationId = Guid.Empty,
            StartTime = lastDetectedTime,
            Status = OperationStatus.Completed,
            Message = loadedStageKey,
            Games = games,
            Services = services,
            TotalGamesDetected = games.Count,
            TotalServicesDetected = services.Count,
            DiskSummary = diskSummary,
            SummaryComputedAtUtc = summaryComputedAt
        };
    }

    /// <summary>
    /// Recomputes deduplicated on-disk totals from persisted detection rows and stores the singleton summary row.
    /// Runs after detection scans and other detection mutations — not on dashboard reads.
    /// </summary>
    public async Task RefreshDiskSummaryAsync(
        CancellationToken cancellationToken = default,
        Action<int, int>? onPathProgress = null)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        // Ordered by Id so path-claiming attribution (first entity to see a shared path wins it)
        // is deterministic across refreshes instead of depending on Postgres's unspecified
        // default row order, which would otherwise let per-row bytes flap between runs for
        // games/services that share cache paths.
        var cachedGames = await dbContext.CachedGameDetections.OrderBy(g => g.Id).ToListAsync(cancellationToken);
        var cachedServices = await dbContext.CachedServiceDetections.OrderBy(s => s.Id).ToListAsync(cancellationToken);

        if (cachedGames.Count == 0 && cachedServices.Count == 0)
        {
            await ClearDetectionSummaryAsync(dbContext, cancellationToken);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var games = cachedGames.Select(ToGameCacheInfo).ToList();
        var services = cachedServices.Select(ToServiceCacheInfo).ToList();
        var attributed = GamesOnDiskCalculator.ComputeAttributedCacheFromDisk(games, services, onPathProgress);

        ulong retainedGameBytes = 0;
        var retainedGameKeys = new List<string>();

        for (var i = 0; i < cachedGames.Count; i++)
        {
            var cached = cachedGames[i];
            cancellationToken.ThrowIfCancellationRequested();
            if (cached.IsEvicted)
            {
                cached.TotalSizeBytes = 0;
                continue;
            }

            // games is index-aligned with cachedGames (Select above); computing the key from it
            // avoids re-deserializing every row's CacheFilePathsJson a second time just for the key.
            var key = GamesOnDiskCalculator.GetGameKey(games[i]);
            if (attributed.GameBytesByKey.TryGetValue(key, out var bytes))
            {
                cached.TotalSizeBytes = bytes;
            }
            else if (cached.CacheFilesFound > 0 && !attributed.ClaimedElsewhereGameKeys.Contains(key))
            {
                // Re-attribution found none of this game's persisted cache paths on disk right now
                // (e.g. the underlying cache files were reclaimed between the Rust scan and this
                // refresh), but the game itself was not evicted through the tracked eviction flow.
                // Trust the last Rust-computed size instead of clobbering it with 0. Excluded here:
                // games that contributed 0 bytes because at least one of their paths was already
                // claimed by an earlier active game/service this pass (even if another of their
                // paths was merely missing from disk) - those bytes are already counted under the
                // earlier claimant, so retaining this row's persisted size too would double-count
                // it in the aggregate.
                retainedGameBytes += cached.TotalSizeBytes;
                retainedGameKeys.Add(key);
            }
            else
            {
                cached.TotalSizeBytes = 0;
            }
        }

        for (var i = 0; i < cachedServices.Count; i++)
        {
            var cached = cachedServices[i];
            cancellationToken.ThrowIfCancellationRequested();
            if (cached.IsEvicted)
            {
                cached.TotalSizeBytes = 0;
                continue;
            }

            var key = GamesOnDiskCalculator.GetServiceKey(services[i]);
            cached.TotalSizeBytes = attributed.ServiceBytesByKey.TryGetValue(key, out var bytes) ? bytes : 0;
        }

        var aggregate = retainedGameBytes == 0
            ? attributed.Aggregate
            : attributed.Aggregate with
            {
                TotalBytes = attributed.Aggregate.TotalBytes + retainedGameBytes,
                GameBytes = attributed.Aggregate.GameBytes + retainedGameBytes,
                ActiveGameCount = attributed.Aggregate.ActiveGameCount + retainedGameKeys.Count
            };

        await UpsertDetectionSummaryAsync(dbContext, aggregate, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (retainedGameKeys.Count > 0)
        {
            _logger.LogWarning(
                "[GameDetection] Retained persisted size for {Count} non-evicted game(s) whose cache paths did not resolve on disk during refresh: {Keys}",
                retainedGameKeys.Count,
                string.Join(", ", retainedGameKeys));
        }

        _logger.LogInformation(
            "[GameDetection] Refreshed disk summary: {GameCount} games ({GameGb:F2} GB), {ServiceCount} services ({ServiceGb:F2} GB), {IdentifiedGb:F2} GB identified total",
            aggregate.ActiveGameCount,
            aggregate.GameBytes / 1_073_741_824.0,
            aggregate.ActiveServiceCount,
            aggregate.ServiceBytes / 1_073_741_824.0,
            aggregate.TotalBytes / 1_073_741_824.0);
    }

    private static async Task<(IdentifiedCacheAggregate? Aggregate, DateTime? ComputedAtUtc)> LoadDetectionSummaryAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var summary = await dbContext.CachedDetectionSummaries
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == CachedDetectionSummary.SingletonId, cancellationToken);

        if (summary == null)
        {
            return (null, null);
        }

        return (
            new IdentifiedCacheAggregate(
                summary.IdentifiedCacheBytes,
                summary.GamesOnDiskBytes,
                summary.IdentifiedServiceBytes,
                summary.GamesOnDiskCount,
                summary.IdentifiedServiceCount),
            summary.ComputedAtUtc);
    }

    private static async Task UpsertDetectionSummaryAsync(
        AppDbContext dbContext,
        IdentifiedCacheAggregate aggregate,
        CancellationToken cancellationToken)
    {
        var summary = await dbContext.CachedDetectionSummaries
            .FirstOrDefaultAsync(s => s.Id == CachedDetectionSummary.SingletonId, cancellationToken);

        if (summary == null)
        {
            summary = new CachedDetectionSummary { Id = CachedDetectionSummary.SingletonId };
            dbContext.CachedDetectionSummaries.Add(summary);
        }

        summary.GamesOnDiskBytes = aggregate.GameBytes;
        summary.GamesOnDiskCount = aggregate.ActiveGameCount;
        summary.IdentifiedCacheBytes = aggregate.TotalBytes;
        summary.IdentifiedServiceBytes = aggregate.ServiceBytes;
        summary.IdentifiedServiceCount = aggregate.ActiveServiceCount;
        summary.ComputedAtUtc = DateTime.UtcNow;
    }

    private static async Task ClearDetectionSummaryAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await dbContext.CachedDetectionSummaries
            .Where(s => s.Id == CachedDetectionSummary.SingletonId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task InvalidateCacheAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await dbContext.CachedGameDetections.ExecuteDeleteAsync(cancellationToken);
        await dbContext.CachedServiceDetections.ExecuteDeleteAsync(cancellationToken);
        await ClearDetectionSummaryAsync(dbContext, cancellationToken);
        _logger.LogInformation(
            "[GameDetection] Cache invalidated - all cached games and services deleted from database");
    }

    public async Task SaveGamesAsync(
        List<GameCacheInfo> games,
        bool incremental,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Discriminator for named (Blizzard/Riot) games: GameAppId==0 && EpicAppId==null && Service != null
        // && GameName != "". These have no Steam AppId and no Epic AppId; identity is (Service, GameName).
        static bool IsNamed(GameCacheInfo g) =>
            g.EpicAppId == null && g.GameAppId == 0 && g.Service != null && g.GameName != "";

        // Composite (Service, GameName) key for named games. The separator () cannot appear in
        // a service name or game name, matching Rust's '\u{1}' composite-key separator so the build
        // side and lookup side can never collide ambiguously. Always use this helper on BOTH sides.
        static string MakeNamedKey(string service, string gameName) =>
            service.ToLower() + "" + gameName;

        if (!incremental)
        {
            var incomingSteamIds = games
                .Where(g => g.EpicAppId == null && !IsNamed(g))
                .Select(g => g.GameAppId)
                .ToList();
            var incomingEpicIds = games.Where(g => g.EpicAppId != null).Select(g => g.EpicAppId!).ToList();
            // Named keys present in this scan, so the non-incremental delete below preserves them.
            var incomingNamedKeys = games
                .Where(IsNamed)
                .Select(g => MakeNamedKey(g.Service!, g.GameName))
                .ToHashSet();

            // Materialize candidate-for-deletion rows, then filter named rows in memory (EF can't
            // translate the composite (Service,GameName) HashSet membership) so incoming named games
            // are preserved exactly like Steam/Epic.
            var deletionCandidates = await dbContext.CachedGameDetections
                .Where(g => !g.IsEvicted
                    && (g.EpicAppId == null
                            ? (g.GameAppId == 0 && g.Service != null && g.GameName != ""
                                // Named row: keep only if NOT in the incoming named set (handled in memory).
                                ? true
                                : !incomingSteamIds.Contains(g.GameAppId))
                            : !incomingEpicIds.Contains(g.EpicAppId!)))
                .Select(g => new { g.Id, g.GameAppId, g.EpicAppId, g.Service, g.GameName })
                .ToListAsync(cancellationToken);

            var idsToDelete = deletionCandidates
                .Where(g => !(g.EpicAppId == null && g.GameAppId == 0 && g.Service != null && g.GameName != ""
                              && incomingNamedKeys.Contains(MakeNamedKey(g.Service!, g.GameName))))
                .Select(g => g.Id)
                .ToList();

            if (idsToDelete.Count > 0)
            {
                await dbContext.CachedGameDetections
                    .Where(g => idsToDelete.Contains(g.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }
        }

        var steamGames = games.Where(g => g.EpicAppId == null && !IsNamed(g)).ToList();
        var epicGames = games.Where(g => g.EpicAppId != null).ToList();
        var namedGames = games.Where(IsNamed).ToList();

        var uniqueSteamGames = steamGames
            .GroupBy(g => g.GameAppId)
            .Select(group => group.Last())
            .ToList();

        var uniqueEpicGames = epicGames
            .GroupBy(g => g.EpicAppId!)
            .Select(group => group.Last())
            .ToList();

        var uniqueNamedGames = namedGames
            .GroupBy(g => (g.Service!.ToLower(), g.GameName))
            .Select(group => group.Last())
            .ToList();

        var uniqueGames = uniqueSteamGames
            .Concat(uniqueEpicGames)
            .Concat(uniqueNamedGames)
            .ToList();

        if (uniqueGames.Count < games.Count)
        {
            _logger.LogWarning(
                "[GameDetection] Removed {DuplicateCount} duplicate entries from detection results ({Steam} Steam, {Epic} Epic, {Named} named unique)",
                games.Count - uniqueGames.Count,
                uniqueSteamGames.Count,
                uniqueEpicGames.Count,
                uniqueNamedGames.Count);
        }

        var now = DateTime.UtcNow;
        var incomingSteamAppIds = uniqueSteamGames.Select(g => g.GameAppId).ToList();
        var existingSteamDict = incomingSteamAppIds.Count == 0
            ? new Dictionary<long, CachedGameDetection>()
            : await dbContext.CachedGameDetections
                .Where(g => g.EpicAppId == null
                    && !(g.GameAppId == 0 && g.Service != null && g.GameName != "")
                    && incomingSteamAppIds.Contains(g.GameAppId))
                .ToDictionaryAsync(g => g.GameAppId, cancellationToken);

        var incomingEpicAppIds = uniqueEpicGames.Select(g => g.EpicAppId!).ToList();
        var existingEpicDict = incomingEpicAppIds.Count == 0
            ? new Dictionary<string, CachedGameDetection>()
            : await dbContext.CachedGameDetections
                .Where(g => g.EpicAppId != null && incomingEpicAppIds.Contains(g.EpicAppId!))
                .ToDictionaryAsync(g => g.EpicAppId!, cancellationToken);

        // Existing named rows keyed by (Service.ToLower(), GameName). EF can't key a dictionary on a
        // tuple from SQL, so we pull the candidate rows (services in the incoming set) and build the
        // dict in memory.
        var incomingNamedServices = uniqueNamedGames.Select(g => g.Service!.ToLower()).Distinct().ToList();
        var existingNamedDict = new Dictionary<(string Service, string GameName), CachedGameDetection>();
        if (incomingNamedServices.Count > 0)
        {
            var existingNamedRows = await dbContext.CachedGameDetections
                .Where(g => g.EpicAppId == null
                    && g.GameAppId == 0
                    && g.Service != null
                    && g.GameName != ""
                    && incomingNamedServices.Contains(g.Service!.ToLower()))
                .ToListAsync(cancellationToken);

            foreach (var row in existingNamedRows)
            {
                var key = (row.Service!.ToLower(), row.GameName);
                existingNamedDict[key] = row;
            }
        }

        foreach (var game in uniqueGames)
        {
            var cachedGame = new CachedGameDetection
            {
                GameAppId = game.GameAppId,
                GameName = game.GameName,
                CacheFilesFound = game.CacheFilesFound,
                TotalSizeBytes = game.TotalSizeBytes,
                DepotIdsJson = JsonSerializer.Serialize(game.DepotIds),
                SampleUrlsJson = JsonSerializer.Serialize(game.SampleUrls),
                CacheFilePathsJson = JsonSerializer.Serialize(game.CacheFilePaths),
                DatasourcesJson = JsonSerializer.Serialize(game.Datasources),
                Service = game.Service,
                EpicAppId = game.EpicAppId,
                LastDetectedUtc = now,
                CreatedAtUtc = now
            };

            CachedGameDetection? existing = null;
            if (cachedGame.EpicAppId != null)
            {
                existingEpicDict.TryGetValue(cachedGame.EpicAppId, out existing);
            }
            else if (cachedGame.GameAppId == 0 && cachedGame.Service != null && cachedGame.GameName != "")
            {
                // Named (Blizzard/Riot) game: key on (Service, GameName), not GameAppId (always 0).
                existingNamedDict.TryGetValue((cachedGame.Service!.ToLower(), cachedGame.GameName), out existing);
            }
            else
            {
                existingSteamDict.TryGetValue(cachedGame.GameAppId, out existing);
            }

            if (existing != null)
            {
                existing.GameName = cachedGame.GameName;
                existing.CacheFilesFound = cachedGame.CacheFilesFound;
                existing.TotalSizeBytes = cachedGame.TotalSizeBytes;
                existing.DepotIdsJson = cachedGame.DepotIdsJson;
                existing.SampleUrlsJson = cachedGame.SampleUrlsJson;
                existing.CacheFilePathsJson = cachedGame.CacheFilePathsJson;
                existing.DatasourcesJson = cachedGame.DatasourcesJson;
                existing.Service = cachedGame.Service;
                existing.EpicAppId = cachedGame.EpicAppId;
                existing.LastDetectedUtc = now;

                if (cachedGame.CacheFilesFound > 0 && existing.IsEvicted)
                {
                    existing.IsEvicted = false;
                    _logger.LogInformation(
                        "[GameDetection] Self-healed: game {GameAppId} ({GameName}) un-evicted - Rust found {Files} cache files",
                        existing.GameAppId,
                        existing.GameName,
                        cachedGame.CacheFilesFound);
                }
            }
            else
            {
                dbContext.CachedGameDetections.Add(cachedGame);
            }
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is NpgsqlException pgEx && pgEx.SqlState == "23505")
        {
            _logger.LogWarning(
                ex,
                "[GameDetection] UNIQUE constraint error when saving games - some records may already exist. Continuing...");
        }
    }

    public async Task<int> RecoverEvictedGamesAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var evictedGames = await dbContext.Downloads
            .Where(d => d.GameAppId != null
                     && d.GameAppId > 0
                     && d.EpicAppId == null
                     && d.IsEvicted
                     && !dbContext.CachedGameDetections.Any(g => g.GameAppId == d.GameAppId!.Value))
            .GroupBy(d => d.GameAppId!.Value)
            .Select(g => new
            {
                GameAppId = g.Key,
                GameName = g.Max(d => d.GameName),
                Service = g.Min(d => d.Service),
                EpicAppId = g.Max(d => d.EpicAppId)
            })
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var totalRecovered = 0;

        if (evictedGames.Count > 0)
        {
            foreach (var game in evictedGames)
            {
                dbContext.CachedGameDetections.Add(new CachedGameDetection
                {
                    GameAppId = game.GameAppId,
                    GameName = game.GameName ?? "Unknown",
                    CacheFilesFound = 0,
                    TotalSizeBytes = 0,
                    Service = game.Service ?? "steam",
                    EpicAppId = game.EpicAppId,
                    IsEvicted = true,
                    LastDetectedUtc = now,
                    CreatedAtUtc = now
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            totalRecovered += evictedGames.Count;
        }

        var evictedEpicGames = await dbContext.Downloads
            .Where(d => d.EpicAppId != null
                     && d.IsEvicted
                     && !dbContext.CachedGameDetections.Any(g => g.EpicAppId == d.EpicAppId))
            .GroupBy(d => d.EpicAppId!)
            .Select(g => new
            {
                EpicAppId = g.Key,
                GameName = g.Max(d => d.GameName),
                Service = g.Min(d => d.Service)
            })
            .ToListAsync(cancellationToken);

        if (evictedEpicGames.Count > 0)
        {
            foreach (var game in evictedEpicGames)
            {
                dbContext.CachedGameDetections.Add(new CachedGameDetection
                {
                    GameAppId = 0,
                    GameName = game.GameName ?? "Unknown",
                    CacheFilesFound = 0,
                    TotalSizeBytes = 0,
                    Service = game.Service ?? "epicgames",
                    EpicAppId = game.EpicAppId,
                    IsEvicted = true,
                    LastDetectedUtc = now,
                    CreatedAtUtc = now
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "[GameDetection] Recovered {Count} evicted Epic games from already-evicted Downloads",
                evictedEpicGames.Count);
            totalRecovered += evictedEpicGames.Count;
        }

        // FIX (Q2): recover evicted NAMED games (xbox/blizzard/riot) as (Service, GameName) detection rows.
        var evictedNamedGames = await dbContext.Downloads
            .Where(d => d.IsEvicted
                     && d.GameAppId == null
                     && d.EpicAppId == null
                     && d.Service != null
                     && d.GameName != null
                     && !dbContext.CachedGameDetections.Any(
                         g => g.GameAppId == 0 && g.Service == d.Service && g.GameName == d.GameName))
            .GroupBy(d => new { d.Service, d.GameName })
            .Select(g => new { g.Key.Service, g.Key.GameName })
            .ToListAsync(cancellationToken);

        if (evictedNamedGames.Count > 0)
        {
            foreach (var game in evictedNamedGames)
            {
                dbContext.CachedGameDetections.Add(new CachedGameDetection
                {
                    GameAppId = 0,                     // detection-side named sentinel (Downloads col is NULL; detection row is 0)
                    GameName = game.GameName ?? "Unknown",
                    CacheFilesFound = 0,
                    TotalSizeBytes = 0,
                    Service = game.Service!,           // 'xbox' | 'blizzard' | 'riot'
                    EpicAppId = null,
                    IsEvicted = true,
                    LastDetectedUtc = now,
                    CreatedAtUtc = now
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "[GameDetection] Recovered {Count} evicted named games from already-evicted Downloads",
                evictedNamedGames.Count);
            totalRecovered += evictedNamedGames.Count;
        }

        return totalRecovered;
    }

    public async Task<int> RecoverEvictedServicesAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var evictedServices = await dbContext.Downloads
            .Where(d => d.IsEvicted
                     && d.GameAppId == null
                     && d.EpicAppId == null
                     && d.Service != null
                     && d.GameName == null            // FIX (Q2): named games recover as (Service,GameName), not a service row
                     && !dbContext.CachedServiceDetections.Any(
                         s => s.ServiceName == d.Service!))
            .GroupBy(d => d.Service!)
            .Select(g => new
            {
                ServiceName = g.Key,
                Datasource = g.Min(d => d.Datasource)
            })
            .ToListAsync(cancellationToken);

        if (evictedServices.Count == 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        foreach (var svc in evictedServices)
        {
            dbContext.CachedServiceDetections.Add(new CachedServiceDetection
            {
                ServiceName = svc.ServiceName,
                CacheFilesFound = 0,
                TotalSizeBytes = 0,
                SampleUrlsJson = "[]",
                CacheFilePathsJson = "[]",
                DatasourcesJson = $"[\"{svc.Datasource}\"]",
                IsEvicted = true,
                LastDetectedUtc = now,
                CreatedAtUtc = now
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[ServiceDetection] Recovered {Count} evicted services from already-evicted Downloads",
            evictedServices.Count);

        return evictedServices.Count;
    }

    public async Task SaveServicesAsync(
        List<ServiceCacheInfo> services,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var incomingByName = services
            .GroupBy(s => s.ServiceName.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Last());

        var existingDict = await dbContext.CachedServiceDetections
            .ToDictionaryAsync(s => s.ServiceName.ToLowerInvariant(), cancellationToken);

        // Positive-evidence gate for the absence→evict loop below. Mirror games'
        // `g.All(d => d.IsEvicted)` rule: a service must only be badged Evicted when ALL of its
        // service-scoped Downloads (Service==name, no GameAppId/EpicAppId) are already evicted by
        // the disk-probing Rust scan. Absence from this scan's results alone is NOT proof the files
        // were removed (a present-on-disk service whose URLs don't hash-match the cache keys - e.g.
        // wsus host/path/normalization drift - is silently omitted from the Rust report).
        //
        // CASING: the gate key (`kvp.Key`) comes from `existingDict`/`incomingByName`, both keyed
        // with C# `.ToLowerInvariant()`. Build this set the SAME way so the membership check below
        // can never miss on a locale/collation-divergent name. We therefore select the RAW Service
        // string (translatable, no nested projection) and lowercase with `ToLowerInvariant()` in
        // C# AFTER materializing - NOT `.ToLower()` inside the LINQ-to-SQL query (which would key by
        // DB collation and re-introduce the mismatch).
        // ALIVE test: count ALL of a service's non-evicted Downloads, including named-game ones
        // (Blizzard/Riot carry GameName). A service like "riot" whose every Download maps to a named
        // game (League of Legends / Valorant / Legends of Runeterra) still has its 30+ GB of cache
        // present on disk - excluding GameName-bearing rows here would leave it with ZERO "live"
        // downloads and falsely flip it Evicted. The GameName==null filter therefore must NOT be
        // applied to this query. (The size/accounting queries above DO keep GameName==null to avoid
        // double-counting named bytes - that's correct and intentional; only the alive test differs.)
        var servicesWithLiveDownloadsList = await dbContext.Downloads
            .Where(d => d.GameAppId == null
                     && d.EpicAppId == null
                     && d.Service != null
                     && !d.IsEvicted)
            .Select(d => d.Service!)
            .Distinct()
            .ToListAsync(cancellationToken);
        var servicesWithLiveDownloads = new HashSet<string>(
            servicesWithLiveDownloadsList.Select(s => s.ToLowerInvariant()));

        foreach (var kvp in incomingByName)
        {
            var service = kvp.Value;
            if (existingDict.TryGetValue(kvp.Key, out var existing))
            {
                existing.CacheFilesFound = service.CacheFilesFound;
                existing.TotalSizeBytes = service.TotalSizeBytes;
                existing.SampleUrlsJson = JsonSerializer.Serialize(service.SampleUrls);
                existing.CacheFilePathsJson = JsonSerializer.Serialize(service.CacheFilePaths);
                existing.DatasourcesJson = JsonSerializer.Serialize(service.Datasources);
                existing.LastDetectedUtc = now;

                if (service.CacheFilesFound > 0 && existing.IsEvicted)
                {
                    existing.IsEvicted = false;
                    _logger.LogInformation(
                        "[ServiceDetection] Self-healed: service {Name} un-evicted - Rust found {Files} cache files",
                        existing.ServiceName,
                        service.CacheFilesFound);
                }
            }
            else
            {
                dbContext.CachedServiceDetections.Add(new CachedServiceDetection
                {
                    ServiceName = service.ServiceName,
                    CacheFilesFound = service.CacheFilesFound,
                    TotalSizeBytes = service.TotalSizeBytes,
                    SampleUrlsJson = JsonSerializer.Serialize(service.SampleUrls),
                    CacheFilePathsJson = JsonSerializer.Serialize(service.CacheFilePaths),
                    DatasourcesJson = JsonSerializer.Serialize(service.Datasources),
                    LastDetectedUtc = now,
                    CreatedAtUtc = now
                });
            }
        }

        foreach (var kvp in existingDict)
        {
            if (!incomingByName.ContainsKey(kvp.Key))
            {
                var existing = kvp.Value;

                existing.LastDetectedUtc = now;

                // Only flip the Evicted badge when there is POSITIVE eviction evidence: the service
                // has no live (non-evicted) Download. If any service-scoped Download is still
                // !IsEvicted, the files are believed present on disk and this absence is a scan
                // false-negative (hash mismatch / lagged ingest / partial datasource) - leave the
                // badge untouched. The Downloads-keyed self-heal (UnevictCachedServiceDetectionsAsync
                // → GetServicesToUnevictAsync) recovers any that legitimately re-cache later.
                if (!servicesWithLiveDownloads.Contains(kvp.Key))
                {
                    // Positive eviction evidence: zero the snapshot columns and badge it Evicted so
                    // it moves to the Evicted list (CacheFilesFound=0 + IsEvicted=true are consistent).
                    existing.CacheFilesFound = 0;
                    existing.TotalSizeBytes = 0;
                    existing.IsEvicted = true;
                    _logger.LogInformation(
                        "[ServiceDetection] Marked {Name} as evicted - absent from latest scan and all Downloads already evicted",
                        existing.ServiceName);
                }
                else
                {
                    // Scan false-negative: the service is absent from this scan's Rust report but
                    // still has non-evicted Downloads, so the files are believed present on disk.
                    // PRESERVE the last-known CacheFilesFound/TotalSizeBytes - zeroing them would drop
                    // the row below the frontend's active-list filter (getActiveServices requires
                    // cache_files_found > 0) and make the service silently vanish until the next full
                    // rescan re-detects it. Leave the badge and counts untouched; the Downloads-keyed
                    // self-heal reconciles any that legitimately change later.
                    _logger.LogDebug(
                        "[ServiceDetection] {Name} absent from latest scan but has non-evicted Downloads - preserving last-known counts (likely scan false-negative)",
                        existing.ServiceName);
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static List<string> DeserializeStringList(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Loads game detection rows, optionally excluding the CacheFilePathsJson column at the SQL
    /// level. That column dominates the table (one JSON element per cache file, millions of paths
    /// across all rows), so consumers that never read the paths must not pull it into memory.
    /// Reconstructed entities keep the column at its "[]" default in that case, which
    /// <see cref="ToGameCacheInfo"/> turns into an empty list.
    /// </summary>
    private static async Task<List<CachedGameDetection>> LoadGameEntitiesAsync(
        AppDbContext dbContext,
        bool includeCacheFilePaths,
        CancellationToken cancellationToken)
    {
        if (includeCacheFilePaths)
        {
            return await dbContext.CachedGameDetections.AsNoTracking().ToListAsync(cancellationToken);
        }

        var rows = await dbContext.CachedGameDetections.AsNoTracking()
            .Select(g => new
            {
                g.Id,
                g.GameAppId,
                g.GameName,
                g.CacheFilesFound,
                g.TotalSizeBytes,
                g.DepotIdsJson,
                g.SampleUrlsJson,
                g.DatasourcesJson,
                g.Service,
                g.EpicAppId,
                g.LastDetectedUtc,
                g.CreatedAtUtc,
                g.IsEvicted
            })
            .ToListAsync(cancellationToken);

        return rows.Select(g => new CachedGameDetection
        {
            Id = g.Id,
            GameAppId = g.GameAppId,
            GameName = g.GameName,
            CacheFilesFound = g.CacheFilesFound,
            TotalSizeBytes = g.TotalSizeBytes,
            DepotIdsJson = g.DepotIdsJson,
            SampleUrlsJson = g.SampleUrlsJson,
            DatasourcesJson = g.DatasourcesJson,
            Service = g.Service,
            EpicAppId = g.EpicAppId,
            LastDetectedUtc = g.LastDetectedUtc,
            CreatedAtUtc = g.CreatedAtUtc,
            IsEvicted = g.IsEvicted
        }).ToList();
    }

    /// <summary>
    /// Service-detection counterpart of <see cref="LoadGameEntitiesAsync"/>; a single popular
    /// service row (e.g. steam) can carry hundreds of thousands of paths in CacheFilePathsJson.
    /// </summary>
    private static async Task<List<CachedServiceDetection>> LoadServiceEntitiesAsync(
        AppDbContext dbContext,
        bool includeCacheFilePaths,
        CancellationToken cancellationToken)
    {
        if (includeCacheFilePaths)
        {
            return await dbContext.CachedServiceDetections.AsNoTracking().ToListAsync(cancellationToken);
        }

        var rows = await dbContext.CachedServiceDetections.AsNoTracking()
            .Select(s => new
            {
                s.Id,
                s.ServiceName,
                s.CacheFilesFound,
                s.TotalSizeBytes,
                s.SampleUrlsJson,
                s.DatasourcesJson,
                s.LastDetectedUtc,
                s.CreatedAtUtc,
                s.IsEvicted
            })
            .ToListAsync(cancellationToken);

        return rows.Select(s => new CachedServiceDetection
        {
            Id = s.Id,
            ServiceName = s.ServiceName,
            CacheFilesFound = s.CacheFilesFound,
            TotalSizeBytes = s.TotalSizeBytes,
            SampleUrlsJson = s.SampleUrlsJson,
            DatasourcesJson = s.DatasourcesJson,
            LastDetectedUtc = s.LastDetectedUtc,
            CreatedAtUtc = s.CreatedAtUtc,
            IsEvicted = s.IsEvicted
        }).ToList();
    }

    private static GameCacheInfo ToGameCacheInfo(CachedGameDetection cached)
    {
        var datasourcesJson = string.IsNullOrWhiteSpace(cached.DatasourcesJson)
            ? "[]"
            : cached.DatasourcesJson;
        return new GameCacheInfo
        {
            GameAppId = cached.GameAppId,
            GameName = cached.GameName,
            CacheFilesFound = cached.CacheFilesFound,
            TotalSizeBytes = cached.TotalSizeBytes,
            DepotIds = JsonSerializer.Deserialize<List<uint>>(cached.DepotIdsJson) ?? new List<uint>(),
            SampleUrls = DeserializeStringList(cached.SampleUrlsJson),
            CacheFilePaths = DeserializeStringList(cached.CacheFilePathsJson),
            Datasources = DeserializeStringList(datasourcesJson),
            Service = cached.Service,
            EpicAppId = cached.EpicAppId,
            IsEvicted = cached.IsEvicted
        };
    }

    private static ServiceCacheInfo ToServiceCacheInfo(CachedServiceDetection cached)
    {
        var datasourcesJson = string.IsNullOrWhiteSpace(cached.DatasourcesJson)
            ? "[]"
            : cached.DatasourcesJson;
        return new ServiceCacheInfo
        {
            ServiceName = cached.ServiceName,
            CacheFilesFound = cached.CacheFilesFound,
            TotalSizeBytes = cached.TotalSizeBytes,
            SampleUrls = DeserializeStringList(cached.SampleUrlsJson),
            CacheFilePaths = DeserializeStringList(cached.CacheFilePathsJson),
            Datasources = DeserializeStringList(datasourcesJson),
            IsEvicted = cached.IsEvicted
        };
    }

    private static async Task EnrichImageUrlsAsync(
        AppDbContext db,
        List<GameCacheInfo> games,
        CancellationToken cancellationToken)
    {
        if (games.Count == 0)
        {
            return;
        }

        var steamAppIds = games
            .Where(g => !string.Equals(g.Service, "epicgames", StringComparison.OrdinalIgnoreCase)
                     && g.GameAppId > 0)
            .Select(g => g.GameAppId)
            .Distinct()
            .ToList();

        if (steamAppIds.Count > 0)
        {
            var bestUrlBySteamApp =
                await DownloadGameImageUrlQueries.GetLatestUrlsForSteamAppsAsync(db, steamAppIds);

            foreach (var game in games.Where(g =>
                         !string.Equals(g.Service, "epicgames", StringComparison.OrdinalIgnoreCase)
                         && g.GameAppId > 0))
            {
                if (!string.IsNullOrEmpty(game.ImageUrl))
                {
                    continue;
                }

                if (bestUrlBySteamApp.TryGetValue(game.GameAppId, out var url))
                {
                    game.ImageUrl = url;
                }
            }
        }

        var epicIdsMissingUrl = games
            .Where(g => string.Equals(g.Service, "epicgames", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrEmpty(g.EpicAppId)
                     && string.IsNullOrEmpty(g.ImageUrl))
            .Select(g => g.EpicAppId!)
            .Distinct()
            .ToList();

        if (epicIdsMissingUrl.Count == 0)
        {
            return;
        }

        var mappings = await db.EpicGameMappings
            .AsNoTracking()
            .Where(m => epicIdsMissingUrl.Contains(m.AppId))
            .Select(m => new { m.AppId, m.ImageUrl })
            .ToListAsync(cancellationToken);

        var epicUrlByCatalogId = mappings
            .Where(m => !string.IsNullOrEmpty(m.ImageUrl))
            .GroupBy(m => m.AppId)
            .ToDictionary(g => g.Key, g => g.First().ImageUrl!);

        foreach (var game in games.Where(g =>
                     string.Equals(g.Service, "epicgames", StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.IsNullOrEmpty(game.ImageUrl) || string.IsNullOrEmpty(game.EpicAppId))
            {
                continue;
            }

            if (epicUrlByCatalogId.TryGetValue(game.EpicAppId, out var url))
            {
                game.ImageUrl = EpicApiDirectClient.EnsureResizeParams(url);
            }
        }
    }
}

internal readonly record struct CachedGameUnevictTargets(
    IReadOnlyList<long> SteamGameAppIds,
    IReadOnlyList<string> EpicAppIds,
    IReadOnlyList<NamedGameKey> NamedGameKeys);

/// <summary>
/// Identity of a named (Blizzard/Riot) game in the detection/eviction layer:
/// (lowercased Service, GameName). Used where neither GameAppId nor EpicAppId exists.
/// </summary>
internal readonly record struct NamedGameKey(string Service, string GameName);
