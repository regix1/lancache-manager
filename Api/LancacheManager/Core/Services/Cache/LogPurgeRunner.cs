using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Core.Services;

/// <summary>
/// One `cache_purge_log_entries` run against one datasource's logs: the input and stem-position
/// files, the nginx reopen check, the child, the reopen, and the saved positions brought back by
/// the lines the child removed. The caller authorizes the run before it calls this.
/// </summary>
internal sealed class LogPurgeRunner
{
    private readonly IPathResolver _pathResolver;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly NginxLogRotationService _nginxLogRotationService;
    private readonly IStateService _stateService;
    private readonly ILogger _logger;

    public LogPurgeRunner(
        IPathResolver pathResolver,
        RustProcessHelper rustProcessHelper,
        NginxLogRotationService nginxLogRotationService,
        IStateService stateService,
        ILogger logger)
    {
        _pathResolver = pathResolver;
        _rustProcessHelper = rustProcessHelper;
        _nginxLogRotationService = nginxLogRotationService;
        _stateService = stateService;
        _logger = logger;
    }

    public async Task<PurgeLogEntriesReport> RunAsync(
        Guid operationId,
        ResolvedDatasource datasource,
        LogPurgeTargets targets,
        Func<PurgeLogProgressData, Task>? onProgress,
        CancellationToken cancellationToken)
    {
        var rustBinaryPath = _pathResolver.GetRustLogPurgePath();
        var operationsDir = _pathResolver.GetOperationsDirectory();
        Directory.CreateDirectory(operationsDir);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var inputJsonPath = Path.Combine(operationsDir, $"log_purge_input_{datasource.Name}_{timestamp}.json");
        var outputJsonPath = Path.Combine(operationsDir, $"log_purge_output_{datasource.Name}_{timestamp}.json");
        var progressJsonPath = Path.Combine(operationsDir, $"log_purge_progress_{datasource.Name}_{timestamp}.json");
        string? stemPositionsPath = null;

        try
        {
            // A null service is left out, so an eviction purge's input stays the
            // {urls, depot_ids} request the binary has always read.
            await File.WriteAllTextAsync(
                inputJsonPath,
                JsonSerializer.Serialize(
                    new { urls = targets.Urls, depot_ids = targets.DepotIds, service = targets.Service },
                    new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    }),
                cancellationToken);

            var args = $"\"{datasource.LogPath}\" \"{inputJsonPath}\" \"{outputJsonPath}\" --progress-json \"{progressJsonPath}\" --progress";
            stemPositionsPath = await _stateService.WriteStemPositionsTempFileAsync(datasource.Name);
            if (stemPositionsPath != null)
            {
                args += $" --stem-positions \"{stemPositionsPath}\"";
            }

            await using var reopenCheck = await _nginxLogRotationService.PrepareReopenCheckAsync(
                new[] { datasource },
                NginxLogRotationService.GetAffectedLogPaths(datasource),
                expectsPublication: true,
                cancellationToken);
            _nginxLogRotationService.ValidateReopenCheck(reopenCheck);
            var start = _rustProcessHelper.CreateProcessStartInfo(rustBinaryPath, args);
            NginxLogRotationService.AttachPublicationCheck(reopenCheck, start);

            _logger.LogInformation(
                "[LogPurge] Running cache_purge_log_entries for datasource '{Datasource}': {Binary} {Args}",
                datasource.Name,
                rustBinaryPath,
                args);

            ProcessExecutionResult purgeResult;
            try
            {
                // Hybrid transport (mirrors CacheClearingService): the stdout progress event is a
                // zero-latency wake-up; the callback re-reads the progress file for the real data.
                purgeResult = await _rustProcessHelper.ExecuteTrackedProcessWithProgressEventsAsync(
                    start,
                    operationId,
                    cancellationToken,
                    onProgress == null
                        ? null
                        : async _ =>
                        {
                            var progress = await _rustProcessHelper.ReadProgressFileAsync<PurgeLogProgressData>(progressJsonPath);

                            // Failure is surfaced via the non-zero exit code below.
                            if (progress == null
                                || string.Equals(progress.Status, "failed", StringComparison.OrdinalIgnoreCase))
                            {
                                return;
                            }

                            await onProgress(progress);
                        },
                    "cache_purge_log_entries");
            }
            catch (Exception error)
            {
                // The child may have replaced a log file before it stopped.
                var failedReopen = await _nginxLogRotationService.CompleteReopenCheckAsync(
                    reopenCheck,
                    physicalChange: true,
                    CancellationToken.None);
                if (!failedReopen.Success)
                {
                    throw new AggregateException(error, new IOException(failedReopen.ErrorMessage!));
                }
                throw;
            }

            // nginx reopens before the report is read, so it stops writing into a replaced file as
            // early as possible. A cancel no longer stops it: the child has already rewritten the log.
            var reopenResult = await _nginxLogRotationService.CompleteReopenCheckAsync(
                reopenCheck,
                physicalChange: true,
                CancellationToken.None);

            if (purgeResult.ExitCode != 0)
            {
                _logger.LogWarning(
                    "[LogPurge] cache_purge_log_entries exited {Code} for datasource '{Datasource}'. stderr: {Err}",
                    purgeResult.ExitCode,
                    datasource.Name,
                    purgeResult.Error);
                // The binary may still have written its report before failing, and a
                // purge that ran shortened the log: harvest the counts so the saved
                // positions come back even on a failed run.
                try
                {
                    var failedReport = await _rustProcessHelper.ReadAndCleanupOutputJsonAsync<PurgeLogEntriesReport>(
                        outputJsonPath,
                        $"cache_purge_log_entries/{datasource.Name}");
                    _stateService.ReduceLogPositionsAfterPurge(
                        datasource.Name,
                        failedReport.LogLinesRemovedBeforePositionBySource,
                        failedReport.LogLinesRemovedBySource);
                }
                catch (Exception failedReportEx)
                {
                    _logger.LogWarning(failedReportEx,
                        "[LogPurge] No readable report after failed run for datasource '{Datasource}'; " +
                        "any purged lines could not adjust the saved log positions",
                        datasource.Name);
                }
                if (!reopenResult.Success)
                {
                    throw new IOException(reopenResult.ErrorMessage!);
                }
                throw new InvalidOperationException(
                    $"Log purge failed for datasource '{datasource.Name}' with exit code {purgeResult.ExitCode}");
            }

            var report = await _rustProcessHelper.ReadAndCleanupOutputJsonAsync<PurgeLogEntriesReport>(
                outputJsonPath,
                $"cache_purge_log_entries/{datasource.Name}");
            _stateService.ReduceLogPositionsAfterPurge(
                datasource.Name,
                report.LogLinesRemovedBeforePositionBySource,
                report.LogLinesRemovedBySource);
            if (!reopenResult.Success)
            {
                throw new InvalidOperationException(reopenResult.ErrorMessage!);
            }
            return report;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(
                error,
                "[LogPurge] Failed to run cache_purge_log_entries for datasource '{Datasource}'",
                datasource.Name);
            throw;
        }
        finally
        {
            await _rustProcessHelper.DeleteTempFileAsync(inputJsonPath);
            await _rustProcessHelper.DeleteTempFileAsync(progressJsonPath);
            if (stemPositionsPath != null)
            {
                await _rustProcessHelper.DeleteTempFileAsync(stemPositionsPath);
            }
        }
    }
}
