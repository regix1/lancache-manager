using System.Text.Json;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using DetectionOperationResponse = LancacheManager.Core.Services.GameCacheDetectionService.DetectionOperationResponse;

namespace LancacheManager.Core.Services;

public sealed class GameCacheDetectionDataService
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

        return new CachedGameUnevictTargets(steamGameIdsToUnevict, epicAppIdsToUnevict);
    }

    public async Task<DetectionOperationResponse?> LoadDetectionFromDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var cachedGames = await dbContext.CachedGameDetections.AsNoTracking().ToListAsync(cancellationToken);
        var cachedServices = await dbContext.CachedServiceDetections.AsNoTracking().ToListAsync(cancellationToken);

        if (cachedGames.Count > 0)
        {
            var nonEvictedSteamGames = cachedGames.Where(g => !g.IsEvicted && g.EpicAppId == null).ToList();
            if (nonEvictedSteamGames.Count > 0)
            {
                var gameAppIds = nonEvictedSteamGames.Select(g => (long?)g.GameAppId).ToHashSet();

                var evictionStatus = await dbContext.Downloads
                    .Where(d => d.GameAppId != null && gameAppIds.Contains(d.GameAppId))
                    .GroupBy(d => d.GameAppId)
                    .Select(g => new
                    {
                        GameAppId = g.Key!.Value,
                        AllEvicted = g.All(d => d.IsEvicted)
                    })
                    .ToDictionaryAsync(x => x.GameAppId, x => x.AllEvicted, cancellationToken);

                foreach (var game in nonEvictedSteamGames)
                {
                    if (evictionStatus.TryGetValue(game.GameAppId, out var allEvicted) && allEvicted)
                    {
                        game.IsEvicted = true;
                    }
                }
            }

            var nonEvictedEpicGames = cachedGames.Where(g => !g.IsEvicted && g.EpicAppId != null).ToList();
            if (nonEvictedEpicGames.Count > 0)
            {
                var epicAppIds = nonEvictedEpicGames.Select(g => g.EpicAppId!).Distinct().ToList();
                var epicEvictionStatus = await dbContext.Downloads
                    .Where(d => d.EpicAppId != null && epicAppIds.Contains(d.EpicAppId))
                    .GroupBy(d => d.EpicAppId)
                    .Select(g => new
                    {
                        EpicAppId = g.Key!,
                        AllEvicted = g.All(d => d.IsEvicted)
                    })
                    .ToDictionaryAsync(x => x.EpicAppId, x => x.AllEvicted, cancellationToken);

                foreach (var game in nonEvictedEpicGames)
                {
                    if (epicEvictionStatus.TryGetValue(game.EpicAppId!, out var epicAllEvicted) &&
                        epicAllEvicted)
                    {
                        game.IsEvicted = true;
                    }
                }
            }

            foreach (var game in cachedGames)
            {
                if (!game.IsEvicted && game.CacheFilesFound == 0)
                {
                    game.IsEvicted = true;
                }
            }
        }

        var games = cachedGames.Select(ConvertToGameCacheInfo).ToList();
        var services = cachedServices.Select(ConvertToServiceCacheInfo).ToList();

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

        var serviceEvictedMap = await dbContext.Downloads
            .Where(d => d.IsEvicted && d.GameAppId == null && d.EpicAppId == null && d.Service != null)
            .GroupBy(d => d.Service!.ToLower())
            .Select(g => new
            {
                Key = g.Key,
                Count = g.Count(),
                Bytes = (ulong)g.Sum(x => x.CacheHitBytes + x.CacheMissBytes)
            })
            .ToDictionaryAsync(x => x.Key, x => (x.Count, x.Bytes), cancellationToken);

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
                && le.Download.Service != null)
            .Select(le => new { le.Download!.Service, le.Url })
            .Distinct()
            .ToListAsync(cancellationToken);

        var serviceEvictedUrlMap = serviceEvictedUrlFlat
            .GroupBy(x => x.Service!.ToLowerInvariant())
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

        await EnrichGameImageUrlsFromDatabaseAsync(dbContext, games, cancellationToken);

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

        return new DetectionOperationResponse
        {
            OperationId = Guid.Empty,
            StartTime = lastDetectedTime,
            Status = OperationStatus.Completed,
            Message = loadedStageKey,
            Games = games,
            Services = services,
            TotalGamesDetected = games.Count,
            TotalServicesDetected = services.Count
        };
    }

    public async Task InvalidateCacheAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await dbContext.CachedGameDetections.ExecuteDeleteAsync(cancellationToken);
        await dbContext.CachedServiceDetections.ExecuteDeleteAsync(cancellationToken);
        _logger.LogInformation(
            "[GameDetection] Cache invalidated - all cached games and services deleted from database");
    }

    public async Task RemoveGameFromCacheAsync(long gameAppId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var game = await dbContext.CachedGameDetections
            .FirstOrDefaultAsync(g => g.GameAppId == gameAppId, cancellationToken);

        if (game == null)
        {
            return;
        }

        dbContext.CachedGameDetections.Remove(game);
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "[GameDetection] Removed game {AppId} ({GameName}) from cache",
            gameAppId,
            game.GameName);
    }

    public async Task RemoveServiceFromCacheAsync(
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var normalizedServiceName = serviceName.ToLower();
        var service = await dbContext.CachedServiceDetections
            .FirstOrDefaultAsync(s => s.ServiceName.ToLower() == normalizedServiceName, cancellationToken);

        if (service == null)
        {
            return;
        }

        dbContext.CachedServiceDetections.Remove(service);
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("[GameDetection] Removed service '{ServiceName}' from cache", serviceName);
    }

    public async Task SaveGamesToDatabaseAsync(
        List<GameCacheInfo> games,
        bool incremental,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (!incremental)
        {
            var incomingSteamIds = games.Where(g => g.EpicAppId == null).Select(g => g.GameAppId).ToList();
            var incomingEpicIds = games.Where(g => g.EpicAppId != null).Select(g => g.EpicAppId!).ToList();

            await dbContext.CachedGameDetections
                .Where(g => !g.IsEvicted
                    && (g.EpicAppId == null ? !incomingSteamIds.Contains(g.GameAppId)
                                            : !incomingEpicIds.Contains(g.EpicAppId!)))
                .ExecuteDeleteAsync(cancellationToken);
        }

        var steamGames = games.Where(g => g.EpicAppId == null).ToList();
        var epicGames = games.Where(g => g.EpicAppId != null).ToList();

        var uniqueSteamGames = steamGames
            .GroupBy(g => g.GameAppId)
            .Select(group => group.Last())
            .ToList();

        var uniqueEpicGames = epicGames
            .GroupBy(g => g.EpicAppId!)
            .Select(group => group.Last())
            .ToList();

        var uniqueGames = uniqueSteamGames.Concat(uniqueEpicGames).ToList();

        if (uniqueGames.Count < games.Count)
        {
            _logger.LogWarning(
                "[GameDetection] Removed {DuplicateCount} duplicate entries from detection results ({Steam} Steam, {Epic} Epic unique)",
                games.Count - uniqueGames.Count,
                uniqueSteamGames.Count,
                uniqueEpicGames.Count);
        }

        var now = DateTime.UtcNow;
        var incomingSteamAppIds = uniqueSteamGames.Select(g => g.GameAppId).ToList();
        var existingSteamDict = incomingSteamAppIds.Count == 0
            ? new Dictionary<long, CachedGameDetection>()
            : await dbContext.CachedGameDetections
                .Where(g => g.EpicAppId == null && incomingSteamAppIds.Contains(g.GameAppId))
                .ToDictionaryAsync(g => g.GameAppId, cancellationToken);

        var incomingEpicAppIds = uniqueEpicGames.Select(g => g.EpicAppId!).ToList();
        var existingEpicDict = incomingEpicAppIds.Count == 0
            ? new Dictionary<string, CachedGameDetection>()
            : await dbContext.CachedGameDetections
                .Where(g => g.EpicAppId != null && incomingEpicAppIds.Contains(g.EpicAppId!))
                .ToDictionaryAsync(g => g.EpicAppId!, cancellationToken);

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
                        "[GameDetection] Self-healed: game {GameAppId} ({GameName}) un-evicted — Rust found {Files} cache files",
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
                     && !d.IsActive
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

            var evictedAppIds = evictedGames.Select(g => (long?)g.GameAppId).ToList();
            await dbContext.Downloads
                .Where(d => d.GameAppId != null && evictedAppIds.Contains(d.GameAppId) && !d.IsEvicted)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsEvicted, true), cancellationToken);

            totalRecovered += evictedGames.Count;
        }

        var evictedEpicGames = await dbContext.Downloads
            .Where(d => d.EpicAppId != null
                     && !d.IsActive
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

            var evictedEpicIds = evictedEpicGames.Select(g => g.EpicAppId).ToList();
            await dbContext.Downloads
                .Where(d => d.EpicAppId != null && evictedEpicIds.Contains(d.EpicAppId) && !d.IsEvicted)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsEvicted, true), cancellationToken);

            _logger.LogInformation(
                "[GameDetection] Recovered {Count} evicted Epic games from Downloads history",
                evictedEpicGames.Count);
            totalRecovered += evictedEpicGames.Count;
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
                     && !dbContext.CachedServiceDetections.Any(
                         s => s.ServiceName.ToLower() == d.Service!.ToLower()))
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
            "[ServiceDetection] Recovered {Count} evicted services from Downloads history",
            evictedServices.Count);

        return evictedServices.Count;
    }

    public async Task SaveServicesToDatabaseAsync(
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
                        "[ServiceDetection] Self-healed: service {Name} un-evicted — Rust found {Files} cache files",
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
                existing.IsEvicted = true;
                existing.CacheFilesFound = 0;
                existing.TotalSizeBytes = 0;
                existing.LastDetectedUtc = now;
                _logger.LogInformation(
                    "[ServiceDetection] Marked {Name} as evicted — no cache files found on latest scan",
                    existing.ServiceName);
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

    private static GameCacheInfo ConvertToGameCacheInfo(CachedGameDetection cached)
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

    private static ServiceCacheInfo ConvertToServiceCacheInfo(CachedServiceDetection cached)
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

    private static async Task EnrichGameImageUrlsFromDatabaseAsync(
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
    IReadOnlyList<string> EpicAppIds);
