using LancacheManager.Models;

namespace LancacheManager.Core;

public readonly record struct GamesOnDiskAggregate(
    ulong TotalBytes,
    int ActiveGameCount);

public readonly record struct IdentifiedCacheAggregate(
    ulong TotalBytes,
    ulong GameBytes,
    ulong ServiceBytes,
    int ActiveGameCount,
    int ActiveServiceCount);

/// <summary>
/// Computes deduplicated cache-on-disk aggregates from detection results.
/// File-size totals are computed once after each scan via
/// <see cref="ComputeIdentifiedCacheFromDisk"/> and persisted in
/// <see cref="CachedDetectionSummary"/> for fast dashboard reads.
/// </summary>
public static class GamesOnDiskCalculator
{
    public static GamesOnDiskAggregate ComputeGamesFromDisk(IEnumerable<GameCacheInfo> games)
    {
        var aggregate = ComputeIdentifiedCacheFromDisk(games, []);
        return new GamesOnDiskAggregate(aggregate.GameBytes, aggregate.ActiveGameCount);
    }

    /// <summary>
    /// Deduplicates cache file paths across games and services using actual on-disk file sizes.
    /// Intended to run once after detection scans — not on dashboard requests.
    /// </summary>
    public static IdentifiedCacheAggregate ComputeIdentifiedCacheFromDisk(
        IEnumerable<GameCacheInfo> games,
        IEnumerable<ServiceCacheInfo> services)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ulong gameBytes = 0;
        ulong serviceBytes = 0;
        var activeGameCount = 0;
        var activeServiceCount = 0;

        foreach (var game in games)
        {
            if (game.IsEvicted)
            {
                continue;
            }

            var contributedBytes = AddUniquePathBytesFromDisk(seenPaths, game.CacheFilePaths);
            if (contributedBytes == 0)
            {
                continue;
            }

            gameBytes += contributedBytes;
            activeGameCount++;
        }

        foreach (var service in services)
        {
            if (service.IsEvicted)
            {
                continue;
            }

            var contributedBytes = AddUniquePathBytesFromDisk(seenPaths, service.CacheFilePaths);
            if (contributedBytes == 0)
            {
                continue;
            }

            serviceBytes += contributedBytes;
            activeServiceCount++;
        }

        return new IdentifiedCacheAggregate(
            gameBytes + serviceBytes,
            gameBytes,
            serviceBytes,
            activeGameCount,
            activeServiceCount);
    }

    private static ulong AddUniquePathBytesFromDisk(HashSet<string> seenPaths, List<string>? paths)
    {
        if (paths == null || paths.Count == 0)
        {
            return 0;
        }

        ulong addedBytes = 0;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var normalizedPath = CacheFileSizeHelper.NormalizePath(path);
            if (!seenPaths.Add(normalizedPath))
            {
                continue;
            }

            addedBytes += CacheFileSizeHelper.TryGetFileSize(normalizedPath);
        }

        return addedBytes;
    }
}
