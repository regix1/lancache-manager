using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

public partial class CacheManagementService
{
    /// <summary>
    /// Remove all cache files for a specific game across all datasources
    /// </summary>
    public async Task<GameCacheRemovalReport> RemoveGameFromCacheAsync(
        long gameAppId,
        CancellationToken cancellationToken = default,
        Func<double, string, Dictionary<string, object?>?, int, long, Task>? onProgress = null,
        Guid? operationId = null)
    {
        RequireUnambiguousKeyScheme();

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            var aggregatedReport = await RunGameRemovalAcrossDatasourcesAsync(
                logTag: "[GameRemoval]",
                rustBinaryPath: _pathResolver.GetRustSteamRemoverPath(),
                planDescription: "Game cache remover",
                planName: "game_removal",
                target: gameAppId.ToString(),
                targetDescription: $"AppID {gameAppId}",
                rustProcessName: "game_cache_remover",
                outputReadLabel: "GameRemoval",
                aggregatedReport: new GameCacheRemovalReport { GameAppId = gameAppId },
                cancellationToken: cancellationToken,
                onProgress: onProgress,
                operationId: operationId,
                aggregateExtras: (aggregated, dsReport) =>
                {
                    foreach (long depotId in dsReport.DepotIds)
                    {
                        if (!aggregated.DepotIds.Contains(depotId))
                        {
                            aggregated.DepotIds.Add(depotId);
                        }
                    }
                });

            // The Rust phase is done but the operation is not: detection-entry delete,
            // disk-summary refresh, service-counts invalidation, and the nginx log reopen
            // below can take noticeably longer than the Rust run itself on a game with few
            // cache files. Surface that phase instead of leaving the notification on its
            // last Rust message.
            if (onProgress != null)
            {
                await onProgress(100.0, "signalr.gameRemove.finalizing", null, aggregatedReport.CacheFilesDeleted, (long)aggregatedReport.TotalBytesFreed);
            }

            // Remove this game from cached game detection results so page reload shows correct data
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            // Direct DbContext delete is deliberate: removal drops the detection row outright instead of the load/upsert flow GameCacheDetectionDataService owns.
            await dbContext.CachedGameDetections
                .Where(CachedGameDetection => CachedGameDetection.GameAppId == gameAppId)
                .ExecuteDeleteAsync();
            _logger.LogInformation("[GameRemoval] Removed cached game detection entry for AppID: {AppId}", gameAppId);

            await FinalizeGameRemovalAsync(cancellationToken);

            return aggregatedReport;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
