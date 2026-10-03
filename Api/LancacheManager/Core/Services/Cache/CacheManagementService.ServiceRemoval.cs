using System.Runtime.ExceptionServices;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

public partial class CacheManagementService
{
    /// <summary>
    /// Remove all cache files for a specific service across all datasources
    /// </summary>
    public async Task<ServiceCacheRemovalReport> RemoveServiceFromCacheAsync(
        string serviceName,
        CancellationToken cancellationToken = default,
        Func<double, string, Dictionary<string, object?>?, int, long, Task>? onProgress = null,
        Guid? operationId = null)
    {
        // Sanitize user-provided service name to prevent process argument injection
        serviceName = RustProcessHelper.SanitizeProcessArgument(serviceName);

        // Service-scoped cache removal requires an unambiguous key scheme; fail closed
        // across every datasource rather than partially deleting a mixed or unknown fleet. The
        // shared launch helper revalidates again after the lock wait, immediately before
        // each native mutation.
        var capabilityDenial = _capabilityService.CheckAllCanMapLogicalObjects();
        if (capabilityDenial != null)
        {
            throw new InvalidOperationException(capabilityDenial);
        }

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("[ServiceRemoval] Starting service cache removal for '{Service}'", serviceName);

            _datasourceService.RefreshPermissions();
            var rustBinaryPath = _pathResolver.GetRustServiceRemoverPath();
            var executionPlan = PrepareRemovalExecutionPlan(
                "[ServiceRemoval]",
                rustBinaryPath,
                "Service remover",
                "service_removal_output",
                "service_removal",
                serviceName,
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
            var removalSelection = new RemovalSelection(
                executionPlan.RunnableDatasources.Select(execution => execution.Datasource.Name).ToList(),
                RemovalKind.Service,
                Service: serviceName.ToLowerInvariant());
            await ValidateRemovalSelectionAsync(removalSelection, cancellationToken);
            var aggregatedReport = new ServiceCacheRemovalReport
            {
                ServiceName = serviceName
            };

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
                    var dsReport = await RunRustRemovalProcessAsync<ServiceRemovalProgress, ServiceCacheRemovalReport>(
                        "[ServiceRemoval]",
                        execution,
                        () =>
                        {
                            var operationArgument = operationId.HasValue
                                ? $" --operation-id {operationId.Value}"
                                : string.Empty;
                            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                                rustBinaryPath,
                                $"\"{datasource.LogPath}\" \"{datasource.CachePath}\" \"{serviceName}\" \"{execution.OutputJsonPath}\" \"{execution.ProgressJsonPath}\" --progress --key-scheme {_capabilityService.GetKeySchemeWireValue(datasource)} --skip-db-delete --datasource \"{datasource.Name}\"{operationArgument}");
                            _logger.LogInformation("[ServiceRemoval] Running removal for datasource '{DatasourceName}': {Binary} {Args}",
                                datasource.Name, rustBinaryPath, startInfo.Arguments);
                            return startInfo;
                        },
                        "service_remover",
                        cancellationToken,
                        operationId,
                        async progressData =>
                        {
                            if (onProgress != null)
                            {
                                await onProgress(
                                    progressData.PercentComplete,
                                    progressData.StageKey,
                                    progressData.Context,
                                    aggregatedReport.CacheFilesDeleted,
                                    checked((long)aggregatedReport.TotalBytesFreed));
                            }
                        },
                        async result =>
                        {
                            // The report JSON carries the cache counts and the URLs for the log step; the
                            // stderr parse is the fallback for a run that died before writing it, whose
                            // URLs the log step still reads from the rows it deletes.
                            try
                            {
                                return await _rustProcessHelper.ReadOutputJsonAsync<ServiceCacheRemovalReport>(
                                    result.OutputJsonPath,
                                    "ServiceRemoval");
                            }
                            catch (Exception readEx)
                            {
                                _logger.LogWarning(readEx,
                                    "[ServiceRemoval] Output JSON unreadable for '{Service}'; falling back to stderr summary parse",
                                    serviceName);
                                var report = new ServiceCacheRemovalReport { ServiceName = serviceName };
                                if (!string.IsNullOrEmpty(result.StdErr))
                                {
                                    ExtractServiceRemovalStats(result.StdErr, report);
                                }

                                return report;
                            }
                        });

                    // Accepted before anything else can throw, so a failure after the cache step rolls
                    // this datasource's log step forward in the repair.
                    await SaveRemovalSourceAsync(
                        operationId,
                        datasource.Name,
                        aggregatedReport.CacheFilesDeleted + dsReport.CacheFilesDeleted,
                        checked((long)(aggregatedReport.TotalBytesFreed + dsReport.TotalBytesFreed)),
                        []);

                    // Aggregate results from this datasource
                    aggregatedReport.CacheFilesDeleted += dsReport.CacheFilesDeleted;
                    aggregatedReport.TotalBytesFreed += dsReport.TotalBytesFreed;
                    aggregatedReport.DatabaseEntriesDeleted += dsReport.DatabaseEntriesDeleted;

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
                                        purgeProgress.PercentComplete,
                                        "signalr.serviceRemove.logs.removing",
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
                        // Synthetic completion tick after Rust exits; empty stageKey → registry default.
                        await onProgress(
                            100,
                            string.Empty,
                            null,
                            aggregatedReport.CacheFilesDeleted,
                            checked((long)aggregatedReport.TotalBytesFreed));
                    }

                    _logger.LogInformation(
                        "[ServiceRemoval] Datasource '{DatasourceName}': removed {Files} files ({Bytes} bytes) for service '{Service}'",
                        datasource.Name, dsReport.CacheFilesDeleted, dsReport.TotalBytesFreed, serviceName);

                    // Clean up progress file for this datasource
                    await _rustProcessHelper.DeleteTempFileAsync(execution.ProgressJsonPath);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    _logger.LogError(failure, "[ServiceRemoval] Removal failed for datasource '{DatasourceName}'", datasource.Name);
                    failedDatasources.Add(datasource.Name);
                    firstFailure ??= failure;
                }
            }

            // Nothing finished anywhere: the run fails with the first error, as it did before.
            if (firstFailure is not null && failedDatasources.Count == executionPlan.RunnableDatasources.Count)
            {
                ExceptionDispatchInfo.Throw(firstFailure);
            }

            // The service's files remain on the datasources that failed, so it keeps its detection row and stays listed until a removal finishes everywhere.
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
                "[ServiceRemoval] Completed for service '{Service}': {Processed} datasource(s) processed, {Skipped} skipped. " +
                "Total: {Files} files removed, {Bytes} bytes freed",
                serviceName, datasourcesProcessed, executionPlan.DatasourcesSkipped,
                aggregatedReport.CacheFilesDeleted, aggregatedReport.TotalBytesFreed);

            // The Rust phase is done but the operation is not: the detection-row delete, the
            // disk-summary refresh (minutes on large databases), the service-counts invalidation,
            // and the nginx log reopen below can dwarf the Rust runtime. Surface this phase instead
            // of leaving the notification on its last per-datasource message. (Same fix as game
            // removal's finalizing emit.)
            if (onProgress != null)
            {
                await onProgress(100.0, "signalr.serviceRemove.finalizing", null, aggregatedReport.CacheFilesDeleted, (long)aggregatedReport.TotalBytesFreed);
            }

            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            // A datasource that failed still holds the service's files, so the service keeps its detection row.
            if (!aggregatedReport.EntityKept)
            {
                // Remove this service from cached service detection results so page reload shows correct data
                // Direct DbContext delete is deliberate: removal drops the detection row outright instead of the load/upsert flow GameCacheDetectionDataService owns.
                await dbContext.CachedServiceDetections
                    .Where(s => s.ServiceName == serviceName)
                    .ExecuteDeleteAsync();
                _logger.LogInformation("[ServiceRemoval] Removed cached service detection entry for: {Service}", serviceName);
            }

            // Prefill records a depot and manifest per app, which is Steam's content model, so every
            // row in the table belongs to this one platform and removing its service deletes the
            // files behind all of them - the prefill game picker would otherwise keep showing
            // "Cached" for every game. Removing any other service leaves the Steam cache untouched
            // and those rows still true. Compared against the platform rather than a bare string so
            // the tie to the table's owner is visible.
            var platform = serviceName.ToPrefillPlatform();
            var prefillAppsDeleted = platform.HasValue
                ? await dbContext.PrefillCachedApps.Where(a => a.Platform == platform.Value).ExecuteDeleteAsync() : 0;
            var prefillDepotsDeleted = 0;
            if (platform == PrefillPlatform.Steam)
            {
                prefillDepotsDeleted = await dbContext.PrefillCachedDepots.ExecuteDeleteAsync();
            }
            if (prefillAppsDeleted + prefillDepotsDeleted > 0)
            {
                await _notifications.NotifyAllAsync(SignalREvents.PrefillCacheChanged);
            }

            if (!operationId.HasValue)
            {
                // Untracked internal callers do not enter the retained repair tail.
                await _gameCacheDetectionService.RefreshDiskSummaryAndInvalidateAsync(cancellationToken);
                await InvalidateServiceCountsAsync();
            }

            return aggregatedReport;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private static void ExtractServiceRemovalStats(string stderr, ServiceCacheRemovalReport report)
    {
        // Extract statistics from stderr output
        // Format: "Cache files deleted: 123"
        var cacheFilesMatch = System.Text.RegularExpressions.Regex.Match(stderr, @"Cache files deleted:\s*(\d+)");
        if (cacheFilesMatch.Success && int.TryParse(cacheFilesMatch.Groups[1].Value, out var cacheFiles))
        {
            report.CacheFilesDeleted = cacheFiles;
        }

        // Format: "Bytes freed: 1.23 GB" or "Bytes freed: 123.45 MB"
        var bytesMatch = System.Text.RegularExpressions.Regex.Match(stderr, @"Bytes freed:\s*([\d.]+)\s*(GB|MB)");
        if (bytesMatch.Success && double.TryParse(bytesMatch.Groups[1].Value, out var bytes))
        {
            var unit = bytesMatch.Groups[2].Value;
            report.TotalBytesFreed = unit == "GB"
                ? (ulong)(bytes * 1_073_741_824.0)
                : (ulong)(bytes * 1_048_576.0);
        }

        // Format: "Database entries deleted: 789"
        var dbEntriesMatch = System.Text.RegularExpressions.Regex.Match(stderr, @"Database entries deleted:\s*(\d+)");
        if (dbEntriesMatch.Success && int.TryParse(dbEntriesMatch.Groups[1].Value, out var dbEntries))
        {
            report.DatabaseEntriesDeleted = dbEntries;
        }
    }
}
