namespace LancacheManager.Core.Services;

public partial class CacheManagementService
{
    /// <summary>
    /// Fails closed when any datasource cannot map logical objects to cache keys: per-game cache
    /// removal requires an unambiguous key scheme across every datasource, rather than partially
    /// deleting a mixed or unknown fleet.
    /// </summary>
    private void RequireUnambiguousKeyScheme()
    {
        var keyCapabilityDenial = _capabilityService.CheckAllCanMapLogicalObjects();
        if (keyCapabilityDenial != null)
        {
            throw new InvalidOperationException(keyCapabilityDenial);
        }
    }

    /// <summary>
    /// The per-datasource removal loop every game-removal flavor (Steam, Epic, named) runs: plans
    /// the datasources, runs the Rust remover once per datasource with scaled progress, restores
    /// the purged log positions from each report, and folds the per-datasource reports into
    /// <paramref name="aggregatedReport"/>. Callers hold the cache lock and do their own
    /// per-service detection cleanup afterwards.
    /// </summary>
    /// <param name="target">The identity argument handed to the Rust binary (app id or game name), already sanitized.</param>
    /// <param name="targetDescription">How log lines name the target (e.g. "game 12345", "Epic game 'X'").</param>
    /// <param name="aggregateExtras">Optional per-datasource fold for report fields only one flavor carries (Steam's depot ids).</param>
    private async Task<GameCacheRemovalReport> RunGameRemovalAcrossDatasourcesAsync(
        string logTag,
        string rustBinaryPath,
        string planDescription,
        string planName,
        string target,
        string targetDescription,
        string rustProcessName,
        string outputReadLabel,
        GameCacheRemovalReport aggregatedReport,
        CancellationToken cancellationToken,
        Func<double, string, Dictionary<string, object?>?, int, long, Task>? onProgress,
        Guid? operationId,
        Action<GameCacheRemovalReport, GameCacheRemovalReport>? aggregateExtras = null)
    {
        _logger.LogInformation("{LogTag} Starting removal for {Target}", logTag, targetDescription);

        var executionPlan = PrepareRemovalExecutionPlan(
            logTag,
            rustBinaryPath,
            planDescription,
            planName,
            planName + "_progress",
            target,
            requireWritableLogs: false);

        int datasourcesProcessed = 0;
        foreach (var execution in executionPlan.RunnableDatasources)
        {
            var datasource = execution.Datasource;

            var dsReport = await RunRustRemovalProcessAsync<GameRemovalProgressData, GameCacheRemovalReport>(
                logTag,
                execution,
                () =>
                {
                    var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                        rustBinaryPath,
                        $"\"{datasource.LogPath}\" \"{datasource.CachePath}\" \"{target}\" \"{execution.OutputJsonPath}\" \"{execution.ProgressJsonPath}\" --progress --key-scheme {_capabilityService.GetKeySchemeWireValue(datasource)}");
                    _logger.LogInformation("{LogTag} Running removal for datasource '{DatasourceName}': {Binary} {Args}",
                        logTag, datasource.Name, rustBinaryPath, startInfo.Arguments);
                    return startInfo;
                },
                rustProcessName,
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
                async result =>
                {
                    var report = await _rustProcessHelper.ReadOutputJsonAsync<GameCacheRemovalReport>(
                        result.OutputJsonPath,
                        outputReadLabel);
                    // Runs on failed exits too (the binaries write the report on their
                    // abort paths): the purge already shortened the log, so the saved
                    // positions must come back regardless of how the run ended.
                    _stateService.ReduceLogPositionsAfterPurge(
                        datasource.Name,
                        report.LogLinesRemovedBeforePositionBySource,
                        report.LogLinesRemovedBySource);
                    return report;
                });

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
            aggregateExtras?.Invoke(aggregatedReport, dsReport);

            datasourcesProcessed++;

            _logger.LogInformation(
                "{LogTag} Datasource '{DatasourceName}': removed {Files} files ({Bytes} bytes) for {Target}",
                logTag, datasource.Name, dsReport.CacheFilesDeleted, dsReport.TotalBytesFreed, targetDescription);
        }

        _logger.LogInformation(
            "{LogTag} Completed for {Target}: {Processed} datasource(s) processed, {Skipped} skipped. " +
            "Total: {Files} files removed, {Bytes} bytes freed",
            logTag, targetDescription, datasourcesProcessed, executionPlan.DatasourcesSkipped,
            aggregatedReport.CacheFilesDeleted, aggregatedReport.TotalBytesFreed);

        return aggregatedReport;
    }

    /// <summary>
    /// The shared tail of every game removal, run after the Rust phase and the per-service
    /// detection cleanup: refresh the persisted disk summary, invalidate the service counts the
    /// log purge changed, and signal nginx to reopen its log files.
    /// </summary>
    private async Task FinalizeGameRemovalAsync(CancellationToken cancellationToken)
    {
        // Refresh persisted disk-summary totals so dashboard reads reflect post-removal state
        await _gameCacheDetectionService.RefreshDiskSummaryAndInvalidateAsync(cancellationToken);

        // Invalidate service counts cache since logs were modified
        await InvalidateServiceCountsAsync();

        // Signal nginx to reopen log files (prevents monolithic container from losing log access)
        await _nginxLogRotationService.ReopenNginxLogsAsync();
    }
}
