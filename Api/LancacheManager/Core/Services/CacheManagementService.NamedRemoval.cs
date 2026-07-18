using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Core.Services;

public partial class CacheManagementService
{
    /// <summary>
    /// Remove all cache files, log entries, and database records for a named game
    /// (Blizzard/Riot/Xbox - keyed by Service + GameName, with GameAppId/EpicAppId both null).
    /// Dispatches to the per-service Rust binary (cache_{service}_remove), each a thin wrapper
    /// over the shared named-removal core, which mirrors the Epic game removal flow:
    /// 1. Deletes cache files from disk (via MD5 cache path calculation)
    /// 2. Removes matching lines from access log text files
    /// 3. Deletes LogEntry and Download records from the database
    /// </summary>
    public async Task<GameCacheRemovalReport> RemoveNamedGameFromCacheAsync(
        string service,
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

        // Sanitize both user-provided arguments to prevent process argument injection
        service = RustProcessHelper.SanitizeProcessArgument(service);
        gameName = RustProcessHelper.SanitizeProcessArgument(gameName);

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("[NamedGameRemoval] Starting removal for '{Service}' / '{GameName}'", service, gameName);

            var rustBinaryPath = _pathResolver.GetRustNamedGameRemoverPath(service);
            var executionPlan = PrepareRemovalExecutionPlan(
                "[NamedGameRemoval]",
                rustBinaryPath,
                "Named game cache remover",
                "named_removal",
                "named_removal_progress",
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

                // Rust positional args (LOCKED CONTRACT): log_dir cache_dir game_name output_json progress_json.
                // The owning service is pinned by the per-service binary (cache_{service}_remove), so it is
                // NOT passed as a positional arg — the contract matches the Epic remover.
                var dsReport = await RunRustRemovalProcessAsync<GameRemovalProgressData, GameCacheRemovalReport>(
                    "[NamedGameRemoval]",
                    execution,
                    () =>
                    {
                        var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                            rustBinaryPath,
                            $"\"{datasource.LogPath}\" \"{datasource.CachePath}\" \"{gameName}\" \"{execution.OutputJsonPath}\" \"{execution.ProgressJsonPath}\" --progress --key-scheme {_capabilityService.GetKeySchemeWireValue(datasource)}");
                        _logger.LogInformation("[NamedGameRemoval] Running removal for datasource '{DatasourceName}': {Binary} {Args}",
                            datasource.Name, rustBinaryPath, startInfo.Arguments);
                        return startInfo;
                    },
                    Path.GetFileNameWithoutExtension(rustBinaryPath),
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
                        "NamedGameRemoval"));

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
                    "[NamedGameRemoval] Datasource '{DatasourceName}': removed {Files} files ({Bytes} bytes) for named game '{Service}' / '{GameName}'",
                    datasource.Name, dsReport.CacheFilesDeleted, dsReport.TotalBytesFreed, service, gameName);
            }

            _logger.LogInformation(
                "[NamedGameRemoval] Completed for '{Service}' / '{GameName}': {Processed} datasource(s) processed, {Skipped} skipped. " +
                "Total: {Files} files removed, {Bytes} bytes freed",
                service, gameName, datasourcesProcessed, executionPlan.DatasourcesSkipped,
                aggregatedReport.CacheFilesDeleted, aggregatedReport.TotalBytesFreed);

            // Remove this named game from cached detection results so page reload shows correct data.
            // Identity is (Service, GameName) with both Steam and Epic ids null. Mirrors the Steam
            // removal's detection-row cleanup above; without it the (xbox/blizzard/riot) detection row
            // survives and the game keeps showing in the Game Cache Detection grid after the frontend
            // refetch (the Xbox cache-split stores Service='xbox' lowercase, matched case-insensitively).
            await _gameCacheDetectionService.RemoveNamedGameFromCacheAsync(service, gameName);

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
