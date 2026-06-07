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
    IReadOnlyDictionary<string, ulong> ServiceBytesByKey);

/// <summary>
/// Computes deduplicated cache-on-disk aggregates from detection results.
/// File-size totals are computed once after each scan via
/// <see cref="ComputeAttributedCacheFromDisk"/> and persisted in
/// <see cref="CachedDetectionSummary"/> for fast dashboard reads.
/// </summary>
public static class GamesOnDiskCalculator
{
    public static string GetGameKey(GameCacheInfo game) =>
        !string.IsNullOrEmpty(game.EpicAppId)
            ? $"epic:{game.EpicAppId}"
            : $"steam:{game.GameAppId}";

    public static string GetServiceKey(ServiceCacheInfo service) =>
        service.ServiceName.ToLowerInvariant();

    /// <summary>
    /// Deduplicates cache file paths across games and services using actual on-disk file sizes.
    /// Returns global totals and per-entity unique contributions (Option A attribution).
    /// </summary>
    public static AttributedCacheResult ComputeAttributedCacheFromDisk(
        IEnumerable<GameCacheInfo> games,
        IEnumerable<ServiceCacheInfo> services)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gameBytesByKey = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var serviceBytesByKey = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
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

            gameBytesByKey[GetGameKey(game)] = contributedBytes;
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
            serviceBytesByKey);
    }

    public static ulong SumUniquePathBytes(IEnumerable<string> paths)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return AddUniquePathBytesFromDisk(seenPaths, paths.ToList());
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
