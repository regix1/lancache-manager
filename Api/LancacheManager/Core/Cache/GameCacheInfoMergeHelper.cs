using System.Globalization;
using LancacheManager.Models;

namespace LancacheManager.Core;

/// <summary>
/// Merges game/service detection results for one identity across datasources. Each datasource is
/// scanned separately and sizes only the cache files under its own root, so counts and bytes add.
/// Two datasources pointing at the same root are skipped before this runs
/// (<c>GameCacheDetectionService.RunDetectionAsync</c>), which is what keeps the sum honest.
/// </summary>
internal static class GameCacheInfoMergeHelper
{
    public static void MergeGame(GameCacheInfo existing, GameCacheInfo incoming, string? datasourceName = null)
    {
        existing.CacheFilesFound += incoming.CacheFilesFound;
        existing.TotalSizeBytes += incoming.TotalSizeBytes;

        existing.SampleUrls.AddRange(incoming.SampleUrls.Take(5 - existing.SampleUrls.Count));

        foreach (var depotId in incoming.DepotIds)
        {
            if (!existing.DepotIds.Contains(depotId))
            {
                existing.DepotIds.Add(depotId);
            }
        }

        existing.SliceSizes = UnionSliceSizes(existing.SliceSizes, incoming.SliceSizes);

        AddDatasource(existing.Datasources, datasourceName);
        foreach (var ds in incoming.Datasources)
        {
            AddDatasource(existing.Datasources, ds);
        }
    }

    /// <summary>
    /// Joins two comma-separated slice-size lists (bytes, as the detection report writes them)
    /// into one ascending list without repeats; null when both are empty.
    /// </summary>
    internal static string? UnionSliceSizes(string? first, string? second)
    {
        var sizes = new SortedSet<ulong>();
        foreach (var list in new[] { first, second })
        {
            if (string.IsNullOrEmpty(list))
            {
                continue;
            }

            foreach (var size in list.Split(','))
            {
                sizes.Add(ulong.Parse(size, CultureInfo.InvariantCulture));
            }
        }

        return sizes.Count == 0 ? null : string.Join(',', sizes);
    }

    public static void MergeService(ServiceCacheInfo existing, ServiceCacheInfo incoming, string? datasourceName = null)
    {
        existing.CacheFilesFound += incoming.CacheFilesFound;
        existing.TotalSizeBytes += incoming.TotalSizeBytes;

        existing.SampleUrls.AddRange(incoming.SampleUrls.Take(5 - existing.SampleUrls.Count));

        AddDatasource(existing.Datasources, datasourceName);
        foreach (var ds in incoming.Datasources)
        {
            AddDatasource(existing.Datasources, ds);
        }
    }

    /// <summary>
    /// Adds each reported unmapped total to the running per-service figure, so the same service
    /// reported once per datasource ends as one row. The dictionary must compare keys ignoring case.
    /// </summary>
    public static void MergeUnmapped(Dictionary<string, UnmappedService> byService, IEnumerable<UnmappedService> reported)
    {
        foreach (var unmapped in reported)
        {
            if (!byService.TryGetValue(unmapped.Service, out var existing))
            {
                byService[unmapped.Service] = unmapped;
                continue;
            }

            existing.FileCount += unmapped.FileCount;
            existing.TotalSizeBytes += unmapped.TotalSizeBytes;
            existing.SampleUrls.AddRange(unmapped.SampleUrls.Take(5 - existing.SampleUrls.Count));
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
}
