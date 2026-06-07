using LancacheManager.Models;

namespace LancacheManager.Core;

/// <summary>
/// Merges game/service detection results across datasources without double-counting
/// cache files that appear in more than one scan.
/// </summary>
internal static class GameCacheInfoMergeHelper
{
    public static void MergeGameInto(GameCacheInfo existing, GameCacheInfo incoming, string? datasourceName = null)
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

    public static void MergeServiceInto(ServiceCacheInfo existing, ServiceCacheInfo incoming, string? datasourceName = null)
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
        var addedBytes = 0UL;

        foreach (var path in incoming.CacheFilePaths ?? [])
        {
            if (!knownPaths.Add(path))
            {
                continue;
            }

            existing.CacheFilePaths.Add(path);
            addedBytes += TryGetFileSize(path);
        }

        if (addedBytes > 0 || incoming.CacheFilePaths is { Count: > 0 })
        {
            existing.TotalSizeBytes += addedBytes;
            existing.CacheFilesFound = existing.CacheFilePaths.Count;
            return;
        }

        existing.CacheFilesFound += incoming.CacheFilesFound;
        existing.TotalSizeBytes += incoming.TotalSizeBytes;
    }

    private static void MergeCacheFiles(ServiceCacheInfo existing, ServiceCacheInfo incoming)
    {
        existing.CacheFilePaths ??= [];
        var knownPaths = new HashSet<string>(existing.CacheFilePaths, StringComparer.OrdinalIgnoreCase);
        var addedBytes = 0UL;

        foreach (var path in incoming.CacheFilePaths ?? [])
        {
            if (!knownPaths.Add(path))
            {
                continue;
            }

            existing.CacheFilePaths.Add(path);
            addedBytes += TryGetFileSize(path);
        }

        if (addedBytes > 0 || incoming.CacheFilePaths is { Count: > 0 })
        {
            existing.TotalSizeBytes += addedBytes;
            existing.CacheFilesFound = existing.CacheFilePaths.Count;
            return;
        }

        existing.CacheFilesFound += incoming.CacheFilesFound;
        existing.TotalSizeBytes += incoming.TotalSizeBytes;
    }

    private static ulong TryGetFileSize(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return 0;
            }

            return (ulong)new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }
}
