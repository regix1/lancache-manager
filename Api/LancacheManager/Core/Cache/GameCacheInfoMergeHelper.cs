using LancacheManager.Models;

namespace LancacheManager.Core;

/// <summary>
/// Merges game/service detection results across datasources without double-counting
/// cache files that appear in more than one scan.
/// </summary>
internal static class GameCacheInfoMergeHelper
{
    public static void MergeGame(GameCacheInfo existing, GameCacheInfo incoming, string? datasourceName = null)
    {
        MergeCacheFiles(existing, incoming);

        existing.SampleUrls.AddRange(incoming.SampleUrls.Take(5 - existing.SampleUrls.Count));

        foreach (var depotId in incoming.DepotIds)
        {
            if (!existing.DepotIds.Contains(depotId))
            {
                existing.DepotIds.Add(depotId);
            }
        }

        AddDatasource(existing.Datasources, datasourceName);
        foreach (var ds in incoming.Datasources)
        {
            AddDatasource(existing.Datasources, ds);
        }
    }

    public static void MergeService(ServiceCacheInfo existing, ServiceCacheInfo incoming, string? datasourceName = null)
    {
        MergeCacheFiles(existing, incoming);

        existing.SampleUrls.AddRange(incoming.SampleUrls.Take(5 - existing.SampleUrls.Count));

        AddDatasource(existing.Datasources, datasourceName);
        foreach (var ds in incoming.Datasources)
        {
            AddDatasource(existing.Datasources, ds);
        }
    }

    private static void AddDatasource(List<string> datasources, string? name)
    {
        if (string.IsNullOrEmpty(name) || datasources.Contains(name))
        {
            return;
        }

        datasources.Add(name);
    }

    private static void MergeCacheFiles(GameCacheInfo existing, GameCacheInfo incoming)
    {
        existing.CacheFilePaths ??= [];
        var knownPaths = new HashSet<string>(existing.CacheFilePaths, StringComparer.OrdinalIgnoreCase);

        foreach (var path in incoming.CacheFilePaths ?? [])
        {
            if (knownPaths.Add(path))
            {
                existing.CacheFilePaths.Add(path);
            }
        }

        if (existing.CacheFilePaths.Count > 0)
        {
            existing.TotalSizeBytes = GamesOnDiskCalculator.SumPaths(existing.CacheFilePaths);
            existing.CacheFilesFound = existing.CacheFilePaths.Count;
            return;
        }

        existing.CacheFilesFound += incoming.CacheFilesFound;
    }

    private static void MergeCacheFiles(ServiceCacheInfo existing, ServiceCacheInfo incoming)
    {
        existing.CacheFilePaths ??= [];
        var knownPaths = new HashSet<string>(existing.CacheFilePaths, StringComparer.OrdinalIgnoreCase);

        foreach (var path in incoming.CacheFilePaths ?? [])
        {
            if (knownPaths.Add(path))
            {
                existing.CacheFilePaths.Add(path);
            }
        }

        if (existing.CacheFilePaths.Count > 0)
        {
            existing.TotalSizeBytes = GamesOnDiskCalculator.SumPaths(existing.CacheFilePaths);
            existing.CacheFilesFound = existing.CacheFilePaths.Count;
            return;
        }

        existing.CacheFilesFound += incoming.CacheFilesFound;
    }
}
