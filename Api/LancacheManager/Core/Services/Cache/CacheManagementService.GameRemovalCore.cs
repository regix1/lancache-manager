using System.Runtime.ExceptionServices;
using LancacheManager.Models;

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
    /// the datasources, runs the Rust remover once per datasource with scaled progress, then that
    /// datasource's log step, and folds the per-datasource reports into
    /// <paramref name="aggregatedReport"/>. A datasource that fails does not stop the next; when
    /// another finished, the report keeps the game listed. Callers hold the cache lock and do their
    /// own per-service detection cleanup afterwards.
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

        int datasourcesProcessed = 0;
        // A datasource that fails does not stop the others, as in the cache clear: what finished is
        // kept and the card names the datasources that did not finish.
        var failedDatasources = new List<string>();
        Exception? firstFailure = null;
        foreach (var execution in executionPlan.RunnableDatasources)
        {
            var datasource = execution.Datasource;
            try
            {
                if (operationId.HasValue)
                {
                    await _operationStateService.StartWorkAsync(
                        operationId.Value,
                        datasource.Name,
                        cancellationToken);
                }

                // The binary only deletes cache files; the log lines go in this datasource's log step.
                var dsReport = await RunRustRemovalProcessAsync<GameRemovalProgress, GameCacheRemovalReport>(
                    logTag,
                    execution,
                    () =>
                    {
                        var operationArgument = operationId.HasValue
                            ? $" --operation-id {operationId.Value}"
                            : string.Empty;
                        var process = _rustProcessHelper.CreateProcessStartInfo(
                            rustBinaryPath,
                            $"\"{datasource.LogPath}\" \"{datasource.CachePath}\" \"{target}\" \"{execution.OutputJsonPath}\" \"{execution.ProgressJsonPath}\" --progress --key-scheme {_capabilityService.GetKeySchemeWireValue(datasource)} --skip-db-delete --datasource \"{datasource.Name}\"{operationArgument}");
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
                                aggregatedReport.CacheFilesDeleted,
                                checked((long)aggregatedReport.TotalBytesFreed));
                        }
                    },
                    result => _rustProcessHelper.ReadOutputJsonAsync<GameCacheRemovalReport>(
                        result.OutputJsonPath,
                        outputReadLabel));

                // Accepted before anything else can throw, so a failure after the cache step rolls
                // this datasource's log step forward in the repair.
                await SaveRemovalSourceAsync(
                    operationId,
                    datasource.Name,
                    aggregatedReport.CacheFilesDeleted + dsReport.CacheFilesDeleted,
                    checked((long)(aggregatedReport.TotalBytesFreed + dsReport.TotalBytesFreed)),
                    dsReport.PurgeDepotIds);

                // Aggregate results from this datasource
                aggregatedReport.CacheFilesDeleted += dsReport.CacheFilesDeleted;
                aggregatedReport.TotalBytesFreed += dsReport.TotalBytesFreed;
                aggregatedReport.EmptyDirsRemoved += dsReport.EmptyDirsRemoved;
                if (!string.IsNullOrEmpty(dsReport.GameName))
                {
                    aggregatedReport.GameName = dsReport.GameName;
                }
                aggregateExtras?.Invoke(aggregatedReport, dsReport);

                if (operationId.HasValue)
                {
                    await _operationStateService.StartWorkAsync(
                        operationId.Value,
                        datasource: null,
                        cancellationToken);
                    aggregatedReport.LogEntriesRemoved += await RunRemovalLogStepAsync(
                        operationId.Value,
                        datasource,
                        removalSelection with { DatasourceNames = [datasource.Name] },
                        dsReport.PurgeUrls,
                        async purgeProgress =>
                        {
                            if (onProgress != null)
                            {
                                await onProgress(
                                    ScaleRemovalProgress(
                                        execution.ExecutionIndex,
                                        execution.TotalConfiguredDatasources,
                                        purgeProgress.PercentComplete),
                                    "signalr.gameRemove.logs.removing",
                                    null,
                                    aggregatedReport.CacheFilesDeleted,
                                    checked((long)aggregatedReport.TotalBytesFreed));
                            }
                        },
                        cancellationToken);
                }

                datasourcesProcessed++;

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
                        aggregatedReport.CacheFilesDeleted,
                        checked((long)aggregatedReport.TotalBytesFreed));
                }

                _logger.LogInformation(
                    "{LogTag} Datasource '{DatasourceName}': removed {Files} files ({Bytes} bytes) for {Target}",
                    logTag, datasource.Name, dsReport.CacheFilesDeleted, dsReport.TotalBytesFreed, targetDescription);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                _logger.LogError(failure, "{LogTag} Removal failed for datasource '{DatasourceName}'", logTag, datasource.Name);
                failedDatasources.Add(datasource.Name);
                firstFailure ??= failure;
            }
            catch (OperationCanceledException) when (failedDatasources.Count > 0 && operationId.HasValue)
            {
                // A datasource that already failed keeps its files and history; the canceled card names it.
                await _operationStateService.SetRunWarningAsync(operationId.Value, new RunWarning(
                    "common.notifications.warnings.datasourcesFailed",
                    new Dictionary<string, object?> { ["datasources"] = string.Join(", ", failedDatasources) }));
                throw;
            }
        }

        // Nothing finished anywhere: the run fails with the first error, as it did before.
        if (firstFailure is not null && failedDatasources.Count == executionPlan.RunnableDatasources.Count)
        {
            ExceptionDispatchInfo.Throw(firstFailure);
        }

        // The game's files remain on the datasources that failed, so it keeps its detection row and stays
        // listed until a removal finishes everywhere.
        if (failedDatasources.Count > 0)
        {
            aggregatedReport.EntityKept = true;
            if (operationId.HasValue)
            {
                await _operationStateService.SetRunWarningAsync(operationId.Value, new RunWarning(
                    "common.notifications.warnings.datasourcesFailed",
                    new Dictionary<string, object?> { ["datasources"] = string.Join(", ", failedDatasources) }));
            }
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

    }
}
