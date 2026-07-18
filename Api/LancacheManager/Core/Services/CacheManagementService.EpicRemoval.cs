using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Core.Services;

public partial class CacheManagementService
{
    /// <summary>
    /// Remove all cache files, log entries, and database records for an Epic game.
    /// Uses the Rust cache_epic_remove binary which mirrors the Steam game removal flow:
    /// 1. Deletes cache files from disk (via MD5 cache path calculation)
    /// 2. Removes matching lines from access log text files
    /// 3. Deletes LogEntry and Download records from the database
    /// </summary>
    public async Task<GameCacheRemovalReport> RemoveEpicGameFromCacheAsync(
        string gameName,
        CancellationToken cancellationToken = default,
        Func<double, string, Dictionary<string, object?>?, int, long, Task>? onProgress = null,
        Guid? operationId = null)
    {
        // Per-game cache removal requires an unambiguous key scheme; fail closed across
        // every datasource rather than partially deleting a mixed or unknown fleet.
        var keyCapabilityDenial = _capabilityService.CheckAllCanMapLogicalObjects();
        if (keyCapabilityDenial != null)
        {
            throw new InvalidOperationException(keyCapabilityDenial);
        }

        // Sanitize user-provided game name to prevent process argument injection
        gameName = RustProcessHelper.SanitizeProcessArgument(gameName);

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("[EpicGameRemoval] Starting removal for '{GameName}'", gameName);

            var rustBinaryPath = _pathResolver.GetRustEpicRemoverPath();
            var executionPlan = PrepareRemovalExecutionPlan(
                "[EpicGameRemoval]",
                rustBinaryPath,
                "Epic game cache remover",
                "epic_removal",
                "epic_removal_progress",
                gameName,
                requireWritableLogs: false);
            var aggregatedReport = new GameCacheRemovalReport
            {
                GameAppId = 0,
                GameName = gameName
            };

            int datasourcesProcessed = 0;
            foreach (var execution in executionPlan.RunnableDatasources)
            {
                var datasource = execution.Datasource;

                var dsReport = await RunRustRemovalProcessAsync<GameRemovalProgressData, GameCacheRemovalReport>(
                    "[EpicGameRemoval]",
                    execution,
                    () =>
                    {
                        var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                            rustBinaryPath,
                            $"\"{datasource.LogPath}\" \"{datasource.CachePath}\" \"{gameName}\" \"{execution.OutputJsonPath}\" \"{execution.ProgressJsonPath}\" --progress --key-scheme {_capabilityService.GetKeySchemeWireValue(datasource)}");
                        _logger.LogInformation("[EpicGameRemoval] Running removal for datasource '{DatasourceName}': {Binary} {Args}",
                            datasource.Name, rustBinaryPath, startInfo.Arguments);
                        return startInfo;
                    },
                    "cache_epic_remove",
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
                        "EpicGameRemoval"));

                if (onProgress != null)
                {
                    var scaledProgress = ScaleRemovalProgress(
                        execution.ExecutionIndex + 1,
                        execution.TotalConfiguredDatasources);
                    // Synthetic per-datasource completion tick; empty stageKey → registry default.
                    await onProgress(
                        scaledProgress,
                        string.Empty,
                        null,
                        dsReport.CacheFilesDeleted,
                        (long)dsReport.TotalBytesFreed);
                }

                // Aggregate results
                aggregatedReport.CacheFilesDeleted += dsReport.CacheFilesDeleted;
                aggregatedReport.TotalBytesFreed += dsReport.TotalBytesFreed;
                aggregatedReport.EmptyDirsRemoved += dsReport.EmptyDirsRemoved;
                aggregatedReport.LogEntriesRemoved += dsReport.LogEntriesRemoved;
                if (!string.IsNullOrEmpty(dsReport.GameName))
                {
                    aggregatedReport.GameName = dsReport.GameName;
                }

                datasourcesProcessed++;

                _logger.LogInformation(
                    "[EpicGameRemoval] Datasource '{DatasourceName}': removed {Files} files ({Bytes} bytes) for Epic game '{GameName}'",
                    datasource.Name, dsReport.CacheFilesDeleted, dsReport.TotalBytesFreed, gameName);
            }

            _logger.LogInformation(
                "[EpicGameRemoval] Completed for '{GameName}': {Processed} datasource(s) processed, {Skipped} skipped. " +
                "Total: {Files} files removed, {Bytes} bytes freed",
                gameName, datasourcesProcessed, executionPlan.DatasourcesSkipped,
                aggregatedReport.CacheFilesDeleted, aggregatedReport.TotalBytesFreed);

            // Remove this Epic game from cached detection results so page reload shows correct data.
            // Epic detection rows carry EpicAppId != null; removal is keyed by GameName (mirrors the
            // Rust cache_epic_remove delete). Mirrors the Steam/named detection-row cleanup; without it
            // the Epic detection row only got pruned later by the Epic mapping loop's full re-detection.
            await _gameCacheDetectionService.RemoveEpicGameFromCacheAsync(gameName);

            // Refresh persisted disk-summary totals so dashboard reads reflect post-removal state
            await _gameCacheDetectionService.RefreshDiskSummaryAndInvalidateAsync(cancellationToken);

            // Invalidate service counts cache since logs were modified
            await InvalidateServiceCountsAsync();

            // Signal nginx to reopen log files
            await _nginxLogRotationService.ReopenNginxLogsAsync();

            return aggregatedReport;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
