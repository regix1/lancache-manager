using LancacheManager.Models;
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
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("[GameRemoval] Starting game cache removal for AppID {AppId}", gameAppId);

            var rustBinaryPath = _pathResolver.GetRustSteamRemoverPath();
            var executionPlan = PrepareRemovalExecutionPlan(
                "[GameRemoval]",
                rustBinaryPath,
                "Game cache remover",
                "game_removal",
                "game_removal_progress",
                gameAppId.ToString(),
                requireWritableLogs: false);

            // Fast-path optimization: if every Downloads row for this game is already
            // flagged IsEvicted, the lancache has nothing to delete on disk. Append
            // --skip-file-probe so the Rust binary skips the path.exists() sweep but
            // still rewrites the access logs and deletes the DB rows.
            bool skipFileProbe = false;
            await using (var probeContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
            {
                int totalRows = await probeContext.Downloads
                    .Where(download => download.GameAppId == gameAppId)
                    .CountAsync(cancellationToken);

                if (totalRows > 0)
                {
                    int evictedRows = await probeContext.Downloads
                        .Where(download => download.GameAppId == gameAppId && download.IsEvicted)
                        .CountAsync(cancellationToken);

                    if (totalRows == evictedRows)
                    {
                        skipFileProbe = true;
                        _logger.LogInformation(
                            "[GameRemoval] Fully evicted game {AppId} - using --skip-file-probe optimization ({Evicted}/{Total} rows evicted)",
                            gameAppId, evictedRows, totalRows);
                    }
                }
            }
            string skipFileProbeArg = skipFileProbe ? " --skip-file-probe" : string.Empty;

            var aggregatedReport = new GameCacheRemovalReport
            {
                GameAppId = gameAppId
            };

            int datasourcesProcessed = 0;
            foreach (var execution in executionPlan.RunnableDatasources)
            {
                var datasource = execution.Datasource;

                var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                    rustBinaryPath,
                    $"\"{datasource.LogPath}\" \"{datasource.CachePath}\" {gameAppId} \"{execution.OutputJsonPath}\" \"{execution.ProgressJsonPath}\"{skipFileProbeArg} --progress");

                _logger.LogInformation("[GameRemoval] Running removal for datasource '{DatasourceName}': {Binary} {Args}",
                    datasource.Name, rustBinaryPath, startInfo.Arguments);

                var dsReport = await RunRustRemovalProcessAsync<GameRemovalProgressData, GameCacheRemovalReport>(
                    "[GameRemoval]",
                    execution,
                    startInfo,
                    "game_cache_remover",
                    cancellationToken,
                    operationId,
                    async progressData =>
                    {
                        if (onProgress != null)
                        {
                            var scaledProgress = ScaleRemovalProgress(
                                execution.ExecutionIndex,
                                execution.TotalConfiguredDatasources,
                                progressData.PercentComplete);
                            await onProgress(
                                scaledProgress,
                                progressData.StageKey,
                                progressData.Context,
                                progressData.FilesProcessed,
                                0);
                        }
                    },
                    result => _rustProcessHelper.ReadOutputJsonAsync<GameCacheRemovalReport>(
                        result.OutputJsonPath,
                        "GameRemoval"));

                // Send final progress update from the report
                if (onProgress != null)
                {
                    var scaledProgress = ScaleRemovalProgress(
                        execution.ExecutionIndex + 1,
                        execution.TotalConfiguredDatasources);
                    // Synthetic per-datasource completion tick; Rust has already written its own
                    // "completed" progress entry. Pass an empty stageKey so the frontend falls
                    // through to the registry's default completed message.
                    await onProgress(
                        scaledProgress,
                        string.Empty,
                        null,
                        dsReport.CacheFilesDeleted,
                        (long)dsReport.TotalBytesFreed);
                }

                // Aggregate results from this datasource
                aggregatedReport.CacheFilesDeleted += dsReport.CacheFilesDeleted;
                aggregatedReport.TotalBytesFreed += dsReport.TotalBytesFreed;
                aggregatedReport.EmptyDirsRemoved += dsReport.EmptyDirsRemoved;
                aggregatedReport.LogEntriesRemoved += dsReport.LogEntriesRemoved;
                if (!string.IsNullOrEmpty(dsReport.GameName))
                {
                    aggregatedReport.GameName = dsReport.GameName;
                }
                foreach (long depotId in dsReport.DepotIds)
                {
                    if (!aggregatedReport.DepotIds.Contains(depotId))
                    {
                        aggregatedReport.DepotIds.Add(depotId);
                    }
                }

                datasourcesProcessed++;

                _logger.LogInformation(
                    "[GameRemoval] Datasource '{DatasourceName}': removed {Files} files ({Bytes} bytes) for game {AppId}",
                    datasource.Name, dsReport.CacheFilesDeleted, dsReport.TotalBytesFreed, gameAppId);
            }

            _logger.LogInformation(
                "[GameRemoval] Completed for AppID {AppId}: {Processed} datasource(s) processed, {Skipped} skipped. " +
                "Total: {Files} files removed, {Bytes} bytes freed",
                gameAppId, datasourcesProcessed, executionPlan.DatasourcesSkipped,
                aggregatedReport.CacheFilesDeleted, aggregatedReport.TotalBytesFreed);

            // The Rust phase is done but the operation is not: detection-entry delete,
            // disk-summary refresh, service-counts invalidation, and the nginx log reopen
            // below can take noticeably longer than a --skip-file-probe Rust run. Surface
            // that phase instead of leaving the notification on its last Rust message.
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

            // Refresh persisted disk-summary totals so dashboard reads reflect post-removal state
            await _gameCacheDetectionService.RefreshDiskSummaryAndInvalidateAsync(cancellationToken);

            // Invalidate service counts cache since logs were modified
            await InvalidateServiceCountsAsync();

            // Signal nginx to reopen log files (prevents monolithic container from losing log access)
            await _nginxLogRotationService.ReopenNginxLogsAsync();

            return aggregatedReport;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
