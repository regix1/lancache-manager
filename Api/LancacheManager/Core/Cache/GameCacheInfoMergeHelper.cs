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

        AddDatasource(existing.Datasources, datasourceName);
        foreach (var ds in incoming.Datasources)
        {
            AddDatasource(existing.Datasources, ds);
        }
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

    private static void AddDatasource(List<string> datasources, string? name)
    {
        if (string.IsNullOrEmpty(name) || datasources.Contains(name))
        {
            return;
        }

        datasources.Add(name);
    }
}
