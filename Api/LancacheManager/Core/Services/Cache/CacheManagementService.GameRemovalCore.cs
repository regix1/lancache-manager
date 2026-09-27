using LancacheManager.Infrastructure.Services;

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
        RemovalSelection removalSelection,
        GameCacheRemovalReport aggregatedReport,
        CancellationToken cancellationToken,
        Func<double, string, Dictionary<string, object?>?, int, long, Task>? onProgress,
        Guid? operationId,
        Action<GameCacheRemovalReport, GameCacheRemovalReport>? aggregateExtras = null)
    {
        _logger.LogInformation("{LogTag} Starting removal for {Target}", logTag, targetDescription);

        _datasourceService.RefreshPermissions();
        var executionPlan = PrepareRemovalExecutionPlan(
            logTag,
            rustBinaryPath,
            planDescription,
            planName,
            planName + "_progress",
            target,
            requireWritableLogs: true);
        if (executionPlan.RunnableDatasources.Count == 0)
        {
            throw new InvalidOperationException("No enabled datasource can run this removal");
        }
        if (executionPlan.DatasourcesSkipped > 0)
        {
            throw new InvalidOperationException(
                "Every enabled datasource must pass root and permission preflight before removal");
        }

        removalSelection = removalSelection with
        {
            DatasourceNames = executionPlan.RunnableDatasources
                .Select(execution => execution.Datasource.Name)
                .ToList()
        };
        await ValidateRemovalSelectionAsync(removalSelection, cancellationToken);
        await using var reopenChecks = await _nginxLogRotationService.PrepareReopenChecksAsync(
            executionPlan.RunnableDatasources.Select(execution => execution.Datasource).ToList(),
            expectsPublication: true,
            cancellationToken);

        int datasourcesProcessed = 0;
        foreach (var execution in executionPlan.RunnableDatasources)
        {
            var datasource = execution.Datasource;
            await using var reopenCheck = reopenChecks.Take(datasource.Name) ??
                await _nginxLogRotationService.PrepareReopenCheckAsync(
                    new[] { datasource },
                    NginxLogRotationService.GetAffectedLogPaths(datasource),
                    expectsPublication: true,
                    cancellationToken);
            _nginxLogRotationService.ValidateReopenCheck(reopenCheck);

            GameCacheRemovalReport dsReport;
            try
            {
                dsReport = await RunRustRemovalProcessAsync<GameRemovalProgress, GameCacheRemovalReport>(
                    logTag,
                    execution,
                    () =>
                    {
                        var process = _rustProcessHelper.CreateProcessStartInfo(
                            rustBinaryPath,
                            $"\"{datasource.LogPath}\" \"{datasource.CachePath}\" \"{target}\" \"{execution.OutputJsonPath}\" \"{execution.ProgressJsonPath}\" --progress --key-scheme {_capabilityService.GetKeySchemeWireValue(datasource)} --skip-db-delete --datasource \"{datasource.Name}\"");
                        NginxLogRotationService.AttachPublicationCheck(reopenCheck, process);
                        _logger.LogInformation("{LogTag} Running removal for datasource '{DatasourceName}': {Binary} {Args}",
                            logTag, datasource.Name, rustBinaryPath, process.Arguments);
                        return process;
                    },
                    rustProcessName,
                    cancellationToken,
                    operationId,
                    async progress =>
                    {
                        if (onProgress != null)
                        {
                            var scaledProgress = ScaleRemovalProgress(
                                execution.ExecutionIndex,
                                execution.TotalConfiguredDatasources,
                                progress.PercentComplete);
                            await onProgress(
                                scaledProgress,
                                progress.StageKey,
                                progress.Context,
                                progress.FilesProcessed,
                                0);
                        }
                    },
                    async result =>
                    {
                        var report = await _rustProcessHelper.ReadOutputJsonAsync<GameCacheRemovalReport>(
                            result.OutputJsonPath,
                            outputReadLabel);
                        _stateService.ReduceLogPositionsAfterPurge(
                            datasource.Name,
                            report.LogLinesRemovedBeforePositionBySource,
                            report.LogLinesRemovedBySource);
                        return report;
                    });
            }
            catch (Exception error)
            {
                await _nginxLogRotationService.InvalidateReopenCheckAsync(reopenCheck, CancellationToken.None);
                var reopen = await _nginxLogRotationService.CompleteReopenCheckAsync(
                    reopenCheck,
                    physicalChange: true,
                    CancellationToken.None);
                if (!reopen.Success)
                {
                    throw new AggregateException(
                        error,
                        new IOException(reopen.ErrorMessage!));
                }
                throw;
            }

            var reopenResult = await _nginxLogRotationService.CompleteReopenCheckAsync(
                reopenCheck,
                dsReport.LogEntriesRemoved > 0,
                cancellationToken);
            if (!reopenResult.Success)
            {
                throw new IOException(reopenResult.ErrorMessage!);
            }

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

        await CleanupRemovalAsync(removalSelection, cancellationToken);

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

    }
}
