namespace LancacheManager.Infrastructure.Services.Scheduling;

/// <summary>
/// The English title of each schedule's run, matching its notification card. A run's tracked
/// operation carries it as its name: logs read that name; the browser names a run and its blocker
/// from their schedule key or operation type, and the queue matches a schedule's runs by their
/// schedule. Every schedule that
/// registers a run through this table needs an entry; the lookup throws on a missing one rather
/// than show the internal key.
/// </summary>
public static class ScheduleTitles
{
    public static readonly IReadOnlyDictionary<string, string> ByServiceKey =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cacheReconciliation"] = "Eviction Scan",
            ["cacheSizeScan"] = "Cache File Scan",
            ["gameDetection"] = "Game Detection",
            ["logRotation"] = "Log Rotation",
            ["gameImageFetch"] = "Game Image Fetch",
            ["cacheSnapshot"] = "Cache Snapshot",
            ["operationHistoryCleanup"] = "Operation History Cleanup",
            ["dashboardCacheWarmer"] = "Dashboard Cache Warmer",
            ["depotMapping"] = "Depot Mapping",
            ["epicMapping"] = "Epic Game Mapping",
            ["xboxMapping"] = "Xbox Game Mapping",
            ["battleNetMapping"] = "Battle.net Game Mapping",
            ["riotMapping"] = "Riot Game Mapping",
        };
}
