using LancacheManager.Models;

namespace LancacheManager.Core;

/// <summary>
/// Computes dashboard "Games on Disk" aggregates from cached game detection results.
/// Deduplicates cache file paths across games so shared CDN / depot files are not
/// counted multiple times when summing per-game sizes.
/// </summary>
public static class GamesOnDiskCalculator
{
    public readonly record struct GamesOnDiskAggregate(
        ulong TotalBytes,
        int ActiveGameCount);

    public static GamesOnDiskAggregate Compute(IEnumerable<GameCacheInfo> games)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ulong deduplicatedBytes = 0;
        var activeGameCount = 0;

        foreach (var game in games)
        {
            if (game.IsEvicted || game.TotalSizeBytes == 0)
            {
                continue;
            }

            var paths = game.CacheFilePaths;
            if (paths == null || paths.Count == 0)
            {
                activeGameCount++;
                deduplicatedBytes += game.TotalSizeBytes;
                continue;
            }

            var bytesPerPath = game.TotalSizeBytes / (ulong)paths.Count;
            var remainder = (int)(game.TotalSizeBytes % (ulong)paths.Count);
            var contributedToGame = false;

            for (var i = 0; i < paths.Count; i++)
            {
                if (!seenPaths.Add(paths[i]))
                {
                    continue;
                }

                deduplicatedBytes += bytesPerPath + (i < remainder ? 1UL : 0UL);
                contributedToGame = true;
            }

            if (contributedToGame)
            {
                activeGameCount++;
            }
        }

        return new GamesOnDiskAggregate(deduplicatedBytes, activeGameCount);
    }
}
