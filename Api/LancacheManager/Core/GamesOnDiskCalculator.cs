using LancacheManager.Models;

namespace LancacheManager.Core;

/// <summary>
/// Computes dashboard cache-on-disk aggregates from cached detection results.
/// Deduplicates cache file paths across games and services using actual file sizes
/// so shared CDN / depot files are not counted multiple times.
/// </summary>
public static class GamesOnDiskCalculator
{
    public readonly record struct GamesOnDiskAggregate(
        ulong TotalBytes,
        int ActiveGameCount);

    public readonly record struct IdentifiedCacheAggregate(
        ulong TotalBytes,
        ulong GameBytes,
        ulong ServiceBytes,
        int ActiveGameCount,
        int ActiveServiceCount);

    public static GamesOnDiskAggregate Compute(IEnumerable<GameCacheInfo> games) =>
        ComputeGames(games);

    public static GamesOnDiskAggregate ComputeGames(IEnumerable<GameCacheInfo> games)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ulong totalBytes = 0;
        var activeGameCount = 0;

        foreach (var game in games)
        {
            if (game.IsEvicted)
            {
                continue;
            }

            var contributedBytes = AddUniquePathBytes(seenPaths, game.CacheFilePaths);
            if (contributedBytes == 0)
            {
                continue;
            }

            totalBytes += contributedBytes;
            activeGameCount++;
        }

        return new GamesOnDiskAggregate(totalBytes, activeGameCount);
    }

    public static IdentifiedCacheAggregate ComputeIdentifiedCache(
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

            var contributedBytes = AddUniquePathBytes(seenPaths, game.CacheFilePaths);
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

            var contributedBytes = AddUniquePathBytes(seenPaths, service.CacheFilePaths);
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

    private static ulong AddUniquePathBytes(HashSet<string> seenPaths, IReadOnlyList<string>? paths)
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
