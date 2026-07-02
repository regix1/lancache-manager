using LancacheManager.Infrastructure.Utilities;
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

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("[ServiceRemoval] Starting service cache removal for '{Service}'", serviceName);

            var rustBinaryPath = _pathResolver.GetRustServiceRemoverPath();
            var executionPlan = PrepareRemovalExecutionPlan(
                "[ServiceRemoval]",
                rustBinaryPath,
                "Service remover",
                "service_removal_output",
                "service_removal",
                serviceName,
                requireWritableLogs: true);
            var aggregatedReport = new ServiceCacheRemovalReport
            {
                ServiceName = serviceName
            };

            int datasourcesProcessed = 0;
            foreach (var execution in executionPlan.RunnableDatasources)
            {
                var datasource = execution.Datasource;

                var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                    rustBinaryPath,
                    $"\"{datasource.LogPath}\" \"{datasource.CachePath}\" \"{serviceName}\" \"{execution.OutputJsonPath}\" \"{execution.ProgressJsonPath}\" --progress");

                _logger.LogInformation("[ServiceRemoval] Running removal for datasource '{DatasourceName}': {Binary} {Args}",
                    datasource.Name, rustBinaryPath, startInfo.Arguments);

                var dsReport = await RunRustRemovalProcessAsync<ServiceRemovalProgressData, ServiceCacheRemovalReport>(
                    "[ServiceRemoval]",
                    execution,
                    startInfo,
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
                                progressData.FilesProcessed,
                                0);
                        }
                    },
                    result =>
                    {
                        var report = new ServiceCacheRemovalReport { ServiceName = serviceName };
                        if (!string.IsNullOrEmpty(result.StdErr))
                        {
                            ExtractServiceRemovalStats(result.StdErr, report);
                        }

                        return Task.FromResult(report);
                    });

                // Send final progress update from the report
                if (onProgress != null)
                {
                    // Synthetic completion tick after Rust exits; empty stageKey → registry default.
                    await onProgress(100, string.Empty, null, dsReport.CacheFilesDeleted, (long)dsReport.TotalBytesFreed);
                }

                // Aggregate results from this datasource
                aggregatedReport.CacheFilesDeleted += dsReport.CacheFilesDeleted;
                aggregatedReport.TotalBytesFreed += dsReport.TotalBytesFreed;
                aggregatedReport.LogEntriesRemoved += dsReport.LogEntriesRemoved;
                aggregatedReport.DatabaseEntriesDeleted += dsReport.DatabaseEntriesDeleted;

                datasourcesProcessed++;

                _logger.LogInformation(
                    "[ServiceRemoval] Datasource '{DatasourceName}': removed {Files} files ({Bytes} bytes) for service '{Service}'",
                    datasource.Name, dsReport.CacheFilesDeleted, dsReport.TotalBytesFreed, serviceName);

                // Clean up progress file for this datasource
                await _rustProcessHelper.DeleteTempFileAsync(execution.ProgressJsonPath);
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

            // Remove this service from cached service detection results so page reload shows correct data
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            // Direct DbContext delete is deliberate: removal drops the detection row outright instead of the load/upsert flow GameCacheDetectionDataService owns.
            await dbContext.CachedServiceDetections
                .Where(s => s.ServiceName == serviceName)
                .ExecuteDeleteAsync();
            _logger.LogInformation("[ServiceRemoval] Removed cached service detection entry for: {Service}", serviceName);

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

        // Format: "Log entries removed: 456"
        var logEntriesMatch = System.Text.RegularExpressions.Regex.Match(stderr, @"Log entries removed:\s*(\d+)");
        if (logEntriesMatch.Success && ulong.TryParse(logEntriesMatch.Groups[1].Value, out var logEntries))
        {
            report.LogEntriesRemoved = logEntries;
        }

        // Format: "Database entries deleted: 789"
        var dbEntriesMatch = System.Text.RegularExpressions.Regex.Match(stderr, @"Database entries deleted:\s*(\d+)");
        if (dbEntriesMatch.Success && int.TryParse(dbEntriesMatch.Groups[1].Value, out var dbEntries))
        {
            report.DatabaseEntriesDeleted = dbEntries;
        }
    }
}
