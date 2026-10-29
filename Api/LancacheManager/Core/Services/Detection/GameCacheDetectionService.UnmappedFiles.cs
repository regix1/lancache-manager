using System.Text.Json;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

public partial class GameCacheDetectionService
{
    /// <summary>
    /// Runs the full claim over every datasource and counts the cache files no game or service
    /// claims (<paramref name="removeLimits"/> null), or deletes them within the remaining confirmed
    /// limits. A remove pass saves the remaining unmapped list from every datasource's report, and
    /// clears it when the pass ends early after deleting files.
    /// </summary>
    internal async Task<UnmappedFilesPassResult> RunUnmappedFilesPassAsync(
        Guid operationId,
        UnmappedFilesLimits? removeLimits,
        long? downloadWatchStart,
        Func<double, string, IReadOnlyDictionary<string, object?>?, Task> onProgress,
        CancellationToken cancellationToken)
    {
        var operationsDir = _pathResolver.GetOperationsDirectory();
        var rustBinaryPath = _pathResolver.GetRustGameDetectorPath();
        _rustProcessHelper.EnsureBinaryExists(rustBinaryPath, "Game cache detector");

        var datasources = _datasourceService.GetDatasources();
        var eligibleByService = new Dictionary<string, UnmappedService>(StringComparer.OrdinalIgnoreCase);
        var remainingByService = new Dictionary<string, UnmappedService>(StringComparer.OrdinalIgnoreCase);
        var firstDatasourceByCachePath = new Dictionary<string, string>(StringComparer.Ordinal);
        long removedFiles = 0;
        long removedBytes = 0;
        long removeFailures = 0;
        var overLimit = false;
        var reports = 0;
        var emptyReports = 0;
        var datasourceIndex = 0;

        // A download that starts during a minutes-long pass is invisible to the checks made before
        // it, and a cache HIT does not change a file's modified time, so the age rule alone does
        // not protect it. A watch over each remove child cancels this source, which kills the child.
        using var passCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        DownloadInProgressException? downloadStop = null;

        async Task StopOnDownloadAsync(CancellationToken watchToken)
        {
            try
            {
                downloadStop = await _cacheScanGate.WaitForDownloadAsync(downloadWatchStart, watchToken);
                await passCancellation.CancelAsync();
            }
            catch (OperationCanceledException)
            {
                // The child reached its report, or the pass ended.
            }
        }

        // A remove child that ends without a report may have deleted files before it stopped.
        var removeChildUnreported = false;
        var savedListCleared = false;
        // A list that could not be cleared would keep naming the files the child deletes, so the
        // failure stops the child the way a download does.
        Exception? clearFailure = null;

        try
        {
            foreach (var datasource in datasources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cachePath = datasource.CachePath;
                if (firstDatasourceByCachePath.TryGetValue(cachePath, out var firstDatasourceName))
                {
                    _logger.LogWarning(
                        "[UnmappedFiles] Skipping datasource '{DatasourceName}': cache path {CachePath} was already scanned for datasource '{FirstDatasourceName}'",
                        datasource.Name,
                        cachePath,
                        firstDatasourceName);
                    datasourceIndex++;
                    continue;
                }

                firstDatasourceByCachePath[cachePath] = datasource.Name;

                var outputJson = Path.Combine(operationsDir, $"unmapped_files_{operationId}_{datasource.Name}.json");
                var progressFilePath = Path.Combine(operationsDir, $"unmapped_files_{operationId}_{datasource.Name}_progress.json");
                var currentIndex = datasourceIndex;

                try
                {
                    var keyScheme = _capabilityService.GetKeySchemeWireValue(datasource);
                    // Removed bytes are measured at delete time and can exceed the indexed sizes the cap
                    // was checked against; the binary refuses a negative limit.
                    var modeFlags = removeLimits is { } limits
                        ? $"--unmapped-files remove --max-files {Math.Max(0, limits.MaxFiles - removedFiles)} --max-bytes {Math.Max(0, limits.MaxBytes - removedBytes)}"
                        : "--unmapped-files count";
                    var arguments = $"\"{cachePath}\" \"{outputJson}\" --progress-file \"{progressFilePath}\" --progress --key-scheme {keyScheme} {modeFlags}";
                    var detector = _rustProcessHelper.CreateProcessStartInfo(rustBinaryPath, arguments);

                    ProcessExecutionResult? result = null;
                    removeChildUnreported = removeLimits != null;
                    // The watch also covers the claim queries, which send no progress while they run.
                    using var childWatch = CancellationTokenSource.CreateLinkedTokenSource(passCancellation.Token);
                    var watch = removeLimits == null ? Task.CompletedTask : StopOnDownloadAsync(childWatch.Token);
                    try
                    {
                        result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressEventsAsync(
                            detector,
                            operationId,
                            passCancellation.Token,
                            async progressEvent =>
                            {
                                // The child sends these after its last delete, and writes its report before
                                // it removes the emptied folders: a download can no longer cost a file, and
                                // the report row's 92% would step the bar back from 95.5%.
                                if (removeLimits != null &&
                                    (progressEvent.Event == "complete" || progressEvent.StageKey == "signalr.gameDetect.writing"))
                                {
                                    await childWatch.CancelAsync();
                                    return;
                                }

                                var progress = await _rustProcessHelper.ReadProgressFileAsync<GameDetectionProgressData>(progressFilePath);
                                // The child does not wait for this handler, so the file can already hold
                                // a later row than this event: the report's 92% or the final 100%, which
                                // would step the bar back or end it before the list is saved.
                                if (progress?.StageKey is not { } stageKey ||
                                    (removeLimits != null &&
                                     stageKey is "signalr.gameDetect.writing" or "signalr.gameDetect.complete.default"))
                                {
                                    return;
                                }

                                // The child writes this stage before its first delete. Clearing the list
                                // here keeps it from naming deleted files after a kill during the delete pass,
                                // except a hard kill of this process between the child's first delete and
                                // this save; the catch below only covers a pass that ends while this process lives.
                                if (removeLimits != null && !savedListCleared && stageKey == "signalr.unmappedRemove.running")
                                {
                                    // The helper only logs a fault in this callback and keeps reading the child.
                                    try
                                    {
                                        await SaveRemainingUnmappedAsync(null);
                                    }
                                    catch (Exception error)
                                    {
                                        clearFailure = error;
                                        await passCancellation.CancelAsync();
                                        return;
                                    }
                                    savedListCleared = true;
                                }

                                await onProgress(
                                    (currentIndex + progress.PercentComplete / 100.0) / datasources.Count * 100.0,
                                    stageKey,
                                    progress.Context);
                            },
                            "cache_game_detect");
                    }
                    catch (OperationCanceledException) when (
                        downloadStop is not null && !cancellationToken.IsCancellationRequested && File.Exists(outputJson))
                    {
                        // The child writes its report only after its last delete, so stopping now saves
                        // no file, and the report is this datasource's result.
                    }
                    catch (OperationCanceledException) when (
                        clearFailure is not null && !cancellationToken.IsCancellationRequested)
                    {
                        throw clearFailure;
                    }
                    finally
                    {
                        await childWatch.CancelAsync();
                        await watch;
                    }

                    if (result != null)
                    {
                        if (!string.IsNullOrWhiteSpace(result.Error))
                        {
                            _logger.LogInformation("[UnmappedFiles] Diagnostic output for '{DatasourceName}':\n{DiagnosticOutput}",
                                datasource.Name, result.Error);
                        }

                        result.EnsureSuccess("cache_game_detect", datasource.Name, passCancellation.Token);
                    }

                    if (!File.Exists(outputJson))
                    {
                        throw new FileNotFoundException($"Output file not found: {outputJson}");
                    }

                    GameDetectionResult? report;
                    await using (var outputStream = File.OpenRead(outputJson))
                    {
                        report = await JsonSerializer.DeserializeAsync<GameDetectionResult>(
                            outputStream, cancellationToken: cancellationToken);
                    }

                    if (report == null)
                    {
                        throw new InvalidDataException($"Failed to parse unmapped files results for datasource '{datasource.Name}'");
                    }

                    removeChildUnreported = false;
                    reports++;
                    if (report.IndexedCacheFiles == 0)
                    {
                        emptyReports++;
                        _logger.LogWarning(
                            "[UnmappedFiles] Datasource '{DatasourceName}' cache directory {CachePath} exists but contained no cache files; the report is not evidence and adds nothing",
                            datasource.Name, cachePath);
                    }
                    else
                    {
                        if (report.UnmappedEligible is { } reportedEligible)
                        {
                            GameCacheInfoMergeHelper.MergeUnmapped(eligibleByService, reportedEligible);
                        }

                        if (report.UnmappedServices is { } reportedRemaining)
                        {
                            GameCacheInfoMergeHelper.MergeUnmapped(remainingByService, reportedRemaining);
                        }

                        removedFiles += report.UnmappedRemovedFiles ?? 0;
                        removedBytes += report.UnmappedRemovedBytes ?? 0;
                        removeFailures += report.UnmappedRemoveFailures ?? 0;
                        overLimit |= report.UnmappedOverLimit == true;
                    }

                    _logger.LogInformation(
                        "[UnmappedFiles] Datasource '{DatasourceName}': {RemovedFiles} file(s) removed, {Failures} failure(s)",
                        datasource.Name, report.UnmappedRemovedFiles ?? 0, report.UnmappedRemoveFailures ?? 0);
                }
                catch (Exception error) when (
                    error is OperationCanceledException or JsonException &&
                    downloadStop is not null &&
                    !cancellationToken.IsCancellationRequested)
                {
                    // A JsonException here is a report the cancel cut off while the child wrote it.
                    throw downloadStop;
                }
                finally
                {
                    // A child that ended without a report deleted files only if it reached its delete
                    // stage, which it writes to this file before its first delete.
                    if (removeChildUnreported)
                    {
                        var stage = await _rustProcessHelper.ReadProgressFileAsync<GameDetectionProgressData>(progressFilePath);
                        removeChildUnreported = stage?.StageKey is
                            "signalr.unmappedRemove.running" or "signalr.gameDetect.writing" or "signalr.gameDetect.complete.default";
                    }

                    await _rustProcessHelper.DeleteTempFileAsync(outputJson);
                    await _rustProcessHelper.DeleteTempFileAsync(progressFilePath);
                }

                datasourceIndex++;
            }

            // A Stop after the last child must not complete the pass or save its partial list.
            cancellationToken.ThrowIfCancellationRequested();

            // Every datasource indexed zero files: nothing about the disk was observed, so saving
            // would wipe the stored list over a mount or path problem.
            if (emptyReports == reports)
            {
                throw new ConflictException("The unmapped files pass found no cache files in any datasource.")
                {
                    StageKey = "errors.unmappedRemoval.cacheEmpty"
                };
            }
        }
        catch when (removeLimits != null && (removedFiles > 0 || removeChildUnreported))
        {
            // The run ended after files were deleted, so the saved list still names some of them.
            // It is cleared rather than kept, and the next full scan measures it again.
            try
            {
                await SaveRemainingUnmappedAsync(null);
            }
            catch (Exception saveError)
            {
                // The card reports the failure that ended the run, not this one.
                _logger.LogError(saveError, "[UnmappedFiles] Could not clear the saved list after the pass failed");
            }
            throw;
        }

        if (removeLimits != null)
        {
            await SaveRemainingUnmappedAsync(remainingByService.Values.ToList());
        }

        return new UnmappedFilesPassResult(
            (int)eligibleByService.Values.Sum(service => service.FileCount),
            eligibleByService.Values.Sum(service => (long)service.TotalSizeBytes),
            eligibleByService.Values.ToList(),
            removedFiles,
            removedBytes,
            removeFailures,
            overLimit);
    }

    /// <summary>
    /// Saves the unmapped list a remove pass leaves behind, or clears it (null) when the pass ended
    /// partway, then refreshes the Storage totals.
    /// </summary>
    private async Task SaveRemainingUnmappedAsync(IReadOnlyList<UnmappedService>? remaining)
    {
        // The deletes are done, so a Stop no longer applies: a cancel landing while the database
        // commits can surface after the commit and skip the cache clear below.
        await _detectionDataService.SaveUnmappedServicesAsync(remaining, CancellationToken.None);

        // The saved list is the run's result. An unmapped removal changes no game or service
        // total, so a failed refresh is logged rather than failing the removal.
        try
        {
            await _detectionDataService.RefreshDiskSummaryAsync(CancellationToken.None);
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "[UnmappedFiles] Disk summary refresh after the removal failed");
        }
        finally
        {
            InvalidateDetectionCache();
        }
    }
}
