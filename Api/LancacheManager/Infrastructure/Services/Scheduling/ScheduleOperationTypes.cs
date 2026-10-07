using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services.Scheduling;

public static class ScheduleOperationTypes
{
    /// <summary>
    /// Maps a schedule service key to the operation type the run-status endpoint queries on the tracker.
    /// Covers both the pipeline-less maintenance services (each owns its own operation type) and the
    /// existing pipelines (eviction/cache-size/detection/depot/epic/xbox/prefill) so a single generic
    /// recovery route can rehydrate any card's in-progress bar after a refresh.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, OperationType> ByServiceKey =
        new Dictionary<string, OperationType>(StringComparer.OrdinalIgnoreCase)
        {
            ["logRotation"] = OperationType.LogRotation,
            ["gameImageFetch"] = OperationType.GameImageFetch,
            ["cacheSnapshot"] = OperationType.CacheSnapshot,
            ["operationHistoryCleanup"] = OperationType.OperationHistoryCleanup,
            ["dashboardCacheWarmer"] = OperationType.DashboardCacheWarmer,
            ["cacheReconciliation"] = OperationType.EvictionScan,
            ["cacheSizeScan"] = OperationType.CacheSizeScan,
            ["gameDetection"] = OperationType.GameDetection,
            ["depotMapping"] = OperationType.DepotMapping,
            ["epicMapping"] = OperationType.EpicMapping,
            ["xboxMapping"] = OperationType.XboxMapping,
            ["battleNetMapping"] = OperationType.BattleNetMapping,
            ["riotMapping"] = OperationType.RiotMapping,
            ["scheduledPrefill"] = OperationType.ScheduledPrefill,
        };

    /// <summary>The schedule that owns runs of <paramref name="operationType"/>; null for a type no schedule owns.</summary>
    public static string? FindServiceKey(OperationType operationType)
        => ByServiceKey.FirstOrDefault(entry => entry.Value == operationType).Key;
}
