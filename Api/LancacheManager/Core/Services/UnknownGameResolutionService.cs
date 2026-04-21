using System.Text.Json;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

public sealed class UnknownGameResolutionService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<UnknownGameResolutionService> _logger;

    private static readonly Dictionary<long, (int FailureCount, DateTime? BlacklistedUntil)>
        _unresolvedDepotTracker = new();

    private static readonly object _unresolvedDepotTrackerLock = new();
    private const int MaxResolutionAttempts = 3;
    private static readonly TimeSpan _blacklistDuration = TimeSpan.FromHours(48);

    public UnknownGameResolutionService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<UnknownGameResolutionService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<int> ResolveUnknownGamesInCacheAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var unknownGames = await GetUnknownGamesCachedAsync(dbContext, cancellationToken);
            if (unknownGames.Count == 0)
            {
                return 0;
            }

            var now = DateTime.UtcNow;
            var blacklistedCount = 0;
            Dictionary<long, (int FailureCount, DateTime? BlacklistedUntil)> unresolvedDepotTrackerSnapshot;
            lock (_unresolvedDepotTrackerLock)
            {
                unresolvedDepotTrackerSnapshot = new(_unresolvedDepotTracker);
            }

            var expiredBlacklistedDepotIds = new List<long>();
            unknownGames = unknownGames.Where(g =>
            {
                if (unresolvedDepotTrackerSnapshot.TryGetValue(g.GameAppId, out var tracker))
                {
                    if (tracker.BlacklistedUntil.HasValue && tracker.BlacklistedUntil.Value > now)
                    {
                        blacklistedCount++;
                        return false;
                    }

                    if (tracker.BlacklistedUntil.HasValue)
                    {
                        expiredBlacklistedDepotIds.Add(g.GameAppId);
                    }
                }

                return true;
            }).ToList();

            if (expiredBlacklistedDepotIds.Count > 0)
            {
                lock (_unresolvedDepotTrackerLock)
                {
                    foreach (var depotId in expiredBlacklistedDepotIds)
                    {
                        _unresolvedDepotTracker.Remove(depotId);
                    }
                }
            }

            if (unknownGames.Count == 0)
            {
                if (blacklistedCount > 0)
                {
                    _logger.LogDebug(
                        "[GameDetection] {Count} unknown depot(s) are blacklisted, skipping resolution",
                        blacklistedCount);
                }

                return 0;
            }

            _logger.LogInformation(
                "[GameDetection] Found {Count} unknown games in cache, attempting to resolve{Blacklisted}",
                unknownGames.Count,
                blacklistedCount > 0 ? $" ({blacklistedCount} blacklisted, skipped)" : "");

            var unknownDepotIds = unknownGames.Select(g => g.GameAppId).ToList();
            var unknownDepotIdSet = unknownDepotIds.ToHashSet();

            var downloadsLookup = await dbContext.Downloads
                .Where(d => d.GameName != null && d.GameAppId != null && d.GameAppId > 0 && d.DepotId != null)
                .Select(d => new { d.GameAppId, d.GameName, d.DepotId })
                .Distinct()
                .ToListAsync(cancellationToken);

            var depotToGameFromDownloads = downloadsLookup
                .Where(d => d.DepotId.HasValue && unknownDepotIdSet.Contains(d.DepotId.Value))
                .GroupBy(d => d.DepotId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            if (depotToGameFromDownloads.Count > 0)
            {
                _logger.LogInformation(
                    "[GameDetection] Downloads table pre-lookup found {Count} depot(s) with resolved names",
                    depotToGameFromDownloads.Count);
            }

            var depotMappingsDict = await dbContext.SteamDepotMappings
                .Where(m => unknownDepotIds.Contains(m.DepotId) && m.IsOwner && m.AppName != null)
                .AsNoTracking()
                .ToDictionaryAsync(m => m.DepotId, cancellationToken);

            var resolvedAppIds = depotToGameFromDownloads.Values
                .Select(d => d.GameAppId!.Value)
                .Concat(depotMappingsDict.Values.Select(m => m.AppId))
                .Distinct()
                .ToList();

            var existingGamesByAppId = resolvedAppIds.Count == 0
                ? new Dictionary<long, CachedGameDetection>()
                : await dbContext.CachedGameDetections
                    .Where(g => resolvedAppIds.Contains(g.GameAppId))
                    .ToDictionaryAsync(g => g.GameAppId, cancellationToken);

            var resolvedCount = 0;
            var newlyFailedDepots = new List<long>();
            var entriesToRemove = new List<CachedGameDetection>();
            var pendingAppIdAssignments = new Dictionary<long, CachedGameDetection>();

            foreach (var unknownGame in unknownGames)
            {
                var depotId = unknownGame.GameAppId;

                if (depotToGameFromDownloads.TryGetValue(depotId, out var downloadsMatch))
                {
                    var resolvedAppId = downloadsMatch.GameAppId!.Value;
                    var resolvedName = downloadsMatch.GameName!;

                    _logger.LogInformation(
                        "[GameDetection] Resolved depot {DepotId} -> {AppId} ({Name}) via Downloads table",
                        depotId,
                        resolvedAppId,
                        resolvedName);

                    if (pendingAppIdAssignments.TryGetValue(resolvedAppId, out var pendingGame))
                    {
                        MergeUnknownGameIntoTarget(pendingGame, unknownGame);
                        entriesToRemove.Add(unknownGame);
                        _logger.LogInformation(
                            "[GameDetection] Merged depot {DepotId} into pending game {AppId} ({Name}) via Downloads table",
                            depotId,
                            resolvedAppId,
                            resolvedName);
                        resolvedCount++;
                        continue;
                    }

                    existingGamesByAppId.TryGetValue(resolvedAppId, out var existingGame);
                    if (existingGame != null && existingGame.Id != unknownGame.Id)
                    {
                        MergeUnknownGameIntoTarget(existingGame, unknownGame);
                        entriesToRemove.Add(unknownGame);
                        pendingAppIdAssignments[resolvedAppId] = existingGame;
                        _logger.LogInformation(
                            "[GameDetection] Merged depot {DepotId} into existing game {AppId} ({Name}) via Downloads table",
                            depotId,
                            resolvedAppId,
                            resolvedName);
                    }
                    else
                    {
                        unknownGame.GameName = resolvedName;
                        unknownGame.GameAppId = resolvedAppId;
                        pendingAppIdAssignments[resolvedAppId] = unknownGame;
                    }

                    resolvedCount++;
                    continue;
                }

                depotMappingsDict.TryGetValue(depotId, out var depotMapping);
                if (depotMapping != null)
                {
                    var resolvedAppId = depotMapping.AppId;
                    var resolvedName = depotMapping.AppName!;

                    _logger.LogInformation(
                        "[GameDetection] Resolved depot {DepotId} -> {AppId} ({Name}) via SteamDepotMappings",
                        depotId,
                        resolvedAppId,
                        resolvedName);

                    if (pendingAppIdAssignments.TryGetValue(resolvedAppId, out var pendingGame))
                    {
                        MergeUnknownGameIntoTarget(pendingGame, unknownGame);
                        entriesToRemove.Add(unknownGame);
                        resolvedCount++;
                        continue;
                    }

                    existingGamesByAppId.TryGetValue(resolvedAppId, out var existingGame);
                    if (existingGame != null && existingGame.Id != unknownGame.Id)
                    {
                        MergeUnknownGameIntoTarget(existingGame, unknownGame);
                        entriesToRemove.Add(unknownGame);
                        pendingAppIdAssignments[resolvedAppId] = existingGame;
                    }
                    else
                    {
                        unknownGame.GameName = resolvedName;
                        unknownGame.GameAppId = resolvedAppId;
                        pendingAppIdAssignments[resolvedAppId] = unknownGame;
                    }

                    resolvedCount++;
                    continue;
                }

                newlyFailedDepots.Add(depotId);
            }

            if (entriesToRemove.Count > 0)
            {
                dbContext.CachedGameDetections.RemoveRange(entriesToRemove);
                _logger.LogInformation(
                    "[GameDetection] Removed {Count} duplicate entries after merging",
                    entriesToRemove.Count);
            }

            if (resolvedCount > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "[GameDetection] Resolved {Count} unknown games in cache",
                    resolvedCount);

                var resolvedDepotIds = unknownDepotIds.Except(newlyFailedDepots).ToList();
                lock (_unresolvedDepotTrackerLock)
                {
                    foreach (var depotId in resolvedDepotIds)
                    {
                        _unresolvedDepotTracker.Remove(depotId);
                    }
                }
            }

            if (newlyFailedDepots.Count > 0)
            {
                var blacklisted = new List<long>();
                var retrying = new List<long>();
                int? retryAttempt = null;

                lock (_unresolvedDepotTrackerLock)
                {
                    foreach (var depotId in newlyFailedDepots)
                    {
                        var current = _unresolvedDepotTracker.GetValueOrDefault(depotId);
                        var newCount = current.FailureCount + 1;

                        if (newCount >= MaxResolutionAttempts)
                        {
                            _unresolvedDepotTracker[depotId] =
                                (newCount, DateTime.UtcNow + _blacklistDuration);
                            blacklisted.Add(depotId);
                            continue;
                        }

                        _unresolvedDepotTracker[depotId] = (newCount, null);
                        retrying.Add(depotId);
                        retryAttempt ??= newCount;
                    }
                }

                if (retrying.Count > 0)
                {
                    _logger.LogInformation(
                        "[GameDetection] Could not resolve {Count} depot(s), will retry (attempt {Attempts}/{Max}): {DepotIds}",
                        retrying.Count,
                        retryAttempt ?? 1,
                        MaxResolutionAttempts,
                        string.Join(", ", retrying));
                }

                if (blacklisted.Count > 0)
                {
                    _logger.LogInformation(
                        "[GameDetection] Blacklisted {Count} depot(s) for {Hours}h after {Max} failed attempts: {DepotIds}",
                        blacklisted.Count,
                        _blacklistDuration.TotalHours,
                        MaxResolutionAttempts,
                        string.Join(", ", blacklisted));
                }
            }

            return resolvedCount;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GameDetection] Failed to resolve unknown games in cache");
            return 0;
        }
    }

    private static async Task<List<CachedGameDetection>> GetUnknownGamesCachedAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        return await dbContext.CachedGameDetections
            .Where(g => g.GameName.StartsWith("Unknown Game (Depot"))
            .ToListAsync(cancellationToken);
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

    private static void MergeUnknownGameIntoTarget(CachedGameDetection target, CachedGameDetection source)
    {
        target.CacheFilesFound += source.CacheFilesFound;
        target.TotalSizeBytes += source.TotalSizeBytes;
        target.LastDetectedUtc = target.LastDetectedUtc > source.LastDetectedUtc
            ? target.LastDetectedUtc
            : source.LastDetectedUtc;

        var targetDepots = JsonSerializer.Deserialize<List<uint>>(target.DepotIdsJson) ?? new List<uint>();
        var sourceDepots = JsonSerializer.Deserialize<List<uint>>(source.DepotIdsJson) ?? new List<uint>();
        targetDepots.AddRange(sourceDepots);
        target.DepotIdsJson = JsonSerializer.Serialize(targetDepots.Distinct().ToList());

        var targetPaths = DeserializeStringList(target.CacheFilePathsJson);
        var sourcePaths = DeserializeStringList(source.CacheFilePathsJson);
        targetPaths.AddRange(sourcePaths);
        target.CacheFilePathsJson = JsonSerializer.Serialize(targetPaths.Distinct().ToList());
    }
}
