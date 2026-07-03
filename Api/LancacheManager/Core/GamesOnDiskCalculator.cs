using System.Collections.Concurrent;
using LancacheManager.Models;

namespace LancacheManager.Core;

public readonly record struct IdentifiedCacheAggregate(
    ulong TotalBytes,
    ulong GameBytes,
    ulong ServiceBytes,
    int ActiveGameCount,
    int ActiveServiceCount);

/// <summary>
/// Per-entity unique path contributions after global deduplication.
/// First game/service to claim a shared cache file owns its bytes.
/// </summary>
public readonly record struct AttributedCacheResult(
    IdentifiedCacheAggregate Aggregate,
    IReadOnlyDictionary<string, ulong> GameBytesByKey,
    IReadOnlyDictionary<string, ulong> ServiceBytesByKey,
    IReadOnlySet<string> ClaimedElsewhereGameKeys);

/// <summary>
/// Computes deduplicated cache-on-disk aggregates from detection results.
/// File-size totals are computed once after each scan via
/// <see cref="ComputeAttributedCacheFromDisk"/> and persisted in
/// <see cref="CachedDetectionSummary"/> for fast dashboard reads.
/// </summary>
public static class GamesOnDiskCalculator
{
    /// <summary>
    /// Stable per-game attribution key. Mirrors <c>GameCacheDetectionService.BuildGameIdentityKey</c>
    /// and the persistence buckets in <c>GameCacheDetectionDataService.SaveGamesAsync</c> exactly.
    /// Named (Blizzard/Riot) games ALL have <see cref="GameCacheInfo.GameAppId"/> == 0, so keying them
    /// on <c>steam:0</c> collapsed every named game into a single bucket — each one overwrote the same
    /// entry and they all read back the last-written game's size. Named games must therefore key on
    /// <c>(Service, GameName)</c> using the Rust composite-key separator (<c>\x01</c>, which cannot
    /// appear in a service or game name) so each resolves its OWN on-disk size.
    /// </summary>
    public static string GetGameKey(GameCacheInfo game)
    {
        if (!string.IsNullOrEmpty(game.EpicAppId))
        {
            return $"epic:{game.EpicAppId}";
        }

        if (game.GameAppId == 0 && game.Service != null && game.GameName != "")
        {
            return $"named:{game.Service.ToLowerInvariant()}\x01{game.GameName}";
        }

        return $"steam:{game.GameAppId}";
    }

    public static string GetServiceKey(ServiceCacheInfo service) =>
        service.ServiceName.ToLowerInvariant();

    /// <summary>
    /// Deduplicates cache file paths across games and services using actual on-disk file sizes.
    /// Returns global totals and per-entity unique contributions (Option A attribution).
    /// The expensive per-path stat calls run in a parallel pre-fetch phase (sequential stats
    /// over millions of NFS paths took minutes); the first-claimant-wins attribution then runs
    /// sequentially over the pre-fetched sizes so its deterministic ordering is unchanged.
    /// <paramref name="onPathProgress"/> (statted, total) fires throttled from worker threads
    /// during the pre-fetch so callers can surface live progress.
    /// </summary>
    public static AttributedCacheResult ComputeAttributedCacheFromDisk(
        IEnumerable<GameCacheInfo> games,
        IEnumerable<ServiceCacheInfo> services,
        Action<int, int>? onPathProgress = null)
    {
        var gameList = games as IReadOnlyList<GameCacheInfo> ?? games.ToList();
        var serviceList = services as IReadOnlyList<ServiceCacheInfo> ?? services.ToList();

        var sizeByPath = PreStatPathsInParallel(gameList, serviceList, onPathProgress);

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gameBytesByKey = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var serviceBytesByKey = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var claimedElsewhereGameKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeGameCount = 0;
        var activeServiceCount = 0;

        foreach (var game in gameList)
        {
            if (game.IsEvicted)
            {
                continue;
            }

            var (contributedBytes, claimedElsewhere) = AccumulatePaths(seenPaths, game.CacheFilePaths, sizeByPath);
            if (contributedBytes == 0)
            {
                // A zero contribution means either at least one of this game's paths was already
                // claimed by an earlier game/service this pass and no other path contributed new
                // bytes (claimedElsewhere - those bytes are already counted under the earlier
                // claimant, so this key must NOT be retained into the aggregate later or totals
                // would double-count), or none of this game's paths resolved to a file on disk at
                // all and none was claimed by anyone (genuinely stale/reclaimed - safe for the
                // caller to retain the last-known size for).
                if (claimedElsewhere)
                {
                    claimedElsewhereGameKeys.Add(GetGameKey(game));
                }
                continue;
            }

            gameBytesByKey[GetGameKey(game)] = contributedBytes;
            activeGameCount++;
        }

        foreach (var service in serviceList)
        {
            if (service.IsEvicted)
            {
                continue;
            }

            var (contributedBytes, _) = AccumulatePaths(seenPaths, service.CacheFilePaths, sizeByPath);
            if (contributedBytes == 0)
            {
                continue;
            }

            serviceBytesByKey[GetServiceKey(service)] = contributedBytes;
            activeServiceCount++;
        }

        var gameBytes = gameBytesByKey.Values.Aggregate(0UL, static (sum, value) => sum + value);
        var serviceBytes = serviceBytesByKey.Values.Aggregate(0UL, static (sum, value) => sum + value);

        return new AttributedCacheResult(
            new IdentifiedCacheAggregate(
                gameBytes + serviceBytes,
                gameBytes,
                serviceBytes,
                activeGameCount,
                activeServiceCount),
            gameBytesByKey,
            serviceBytesByKey,
            claimedElsewhereGameKeys);
    }

    public static ulong SumPaths(IEnumerable<string> paths)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return AccumulatePaths(seenPaths, paths.ToList()).Bytes;
    }

    /// <summary>
    /// Accumulates the on-disk size of paths not already claimed by an earlier game/service in
    /// this pass. Returns whether this entity contributed zero new bytes AND at least one of its
    /// paths had already been claimed by another entity this pass - the signal a caller needs to
    /// tell "these bytes (if any were owed) are already counted under another active entity"
    /// apart from "none of these paths exist on disk anywhere" (the genuinely-stale case, which
    /// must NOT set this flag so the caller can safely retain a last-known size for it).
    /// A partially-claimed entity (one path claimed elsewhere, another new-but-missing-from-disk)
    /// still contributes 0 bytes and must be treated as claimed-elsewhere too, otherwise its stale
    /// persisted size would be retained on top of the earlier claimant's already-counted bytes.
    /// </summary>
    private static (ulong Bytes, bool ClaimedElsewhere) AccumulatePaths(
        HashSet<string> seenPaths,
        List<string>? paths,
        IReadOnlyDictionary<string, ulong>? sizeByPath = null)
    {
        if (paths == null || paths.Count == 0)
        {
            return (0, false);
        }

        ulong addedBytes = 0;
        var anyPathClaimedByOther = false;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var normalizedPath = CacheFileSizeHelper.NormalizePath(path);
            if (!seenPaths.Add(normalizedPath))
            {
                anyPathClaimedByOther = true;
                continue;
            }

            // Sizes come from the parallel pre-fetch when available (the attribution pass
            // itself must stay sequential for deterministic first-claimant ordering);
            // direct stat is the fallback for callers without a pre-fetch (SumPaths).
            addedBytes += sizeByPath != null
                ? (sizeByPath.TryGetValue(normalizedPath, out var size) ? size : 0UL)
                : CacheFileSizeHelper.TryGetFileSize(normalizedPath);
        }

        return (addedBytes, addedBytes == 0 && anyPathClaimedByOther);
    }

    /// <summary>
    /// Stats every unique normalized cache path of all non-evicted entities in parallel.
    /// The stat syscall dominates the disk-summary refresh (millions of paths, sequential
    /// round-trips on network filesystems); fanning it out cuts the wall clock by roughly
    /// the degree of parallelism while leaving the attribution semantics untouched.
    /// </summary>
    private static Dictionary<string, ulong> PreStatPathsInParallel(
        IReadOnlyList<GameCacheInfo> games,
        IReadOnlyList<ServiceCacheInfo> services,
        Action<int, int>? onPathProgress)
    {
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Collect(List<string>? paths)
        {
            if (paths == null)
            {
                return;
            }

            foreach (var path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    uniquePaths.Add(CacheFileSizeHelper.NormalizePath(path));
                }
            }
        }

        foreach (var game in games)
        {
            if (!game.IsEvicted)
            {
                Collect(game.CacheFilePaths);
            }
        }

        foreach (var service in services)
        {
            if (!service.IsEvicted)
            {
                Collect(service.CacheFilePaths);
            }
        }

        var total = uniquePaths.Count;
        var sizeByPath = new ConcurrentDictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var statted = 0;

        // Stat calls are I/O-bound (network round-trips, not CPU), so go well past core
        // count; 32 concurrent stats is a safe ceiling for NFS/SMB servers.
        Parallel.ForEach(
            uniquePaths,
            new ParallelOptions { MaxDegreeOfParallelism = 32 },
            path =>
            {
                sizeByPath[path] = CacheFileSizeHelper.TryGetFileSize(path);

                var done = Interlocked.Increment(ref statted);
                if (onPathProgress != null && (done % 25_000 == 0 || done == total))
                {
                    onPathProgress(done, total);
                }
            });

        return new Dictionary<string, ulong>(sizeByPath, StringComparer.OrdinalIgnoreCase);
    }
}
