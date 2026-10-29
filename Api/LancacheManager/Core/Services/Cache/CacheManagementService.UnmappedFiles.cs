using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

public partial class CacheManagementService
{
    /// <summary>
    /// Counts the unmapped cache files a removal would delete now, and deletes none of them.
    /// </summary>
    /// <remarks>
    /// Holds no cache lock: the pass only reads, runs for minutes, and the conflict checker already
    /// queues it behind an unmapped removal and every other full sweep. It may run beside a game or
    /// service removal, which deletes a file before its rows, so a file that removal took is not counted.
    /// </remarks>
    public async Task<int> CountUnmappedCacheFilesAsync(
        Guid operationId,
        Func<double, string, Task> onProgress,
        CancellationToken cancellationToken)
    {
        var capabilityDenial = _capabilityService.CheckAllCanMapLogicalObjects();
        if (capabilityDenial != null)
        {
            throw new InvalidOperationException(capabilityDenial);
        }

        PublishCount(new CacheFileCountState(operationId, null, null, null, null));

        var pass = await _gameCacheDetectionService.RunUnmappedFilesPassAsync(
            operationId,
            null,
            null,
            async (percentComplete, stageKey, context) =>
            {
                PublishCount(new CacheFileCountState(operationId, context, null, null, null));
                await onProgress(percentComplete, stageKey);
            },
            cancellationToken);

        PublishCount(new CacheFileCountState(
            operationId, null, pass.EligibleFiles, pass.EligibleBytes, pass.EligibleByService));
        return pass.EligibleFiles;
    }

    /// <summary>
    /// Refuses the request for an unmapped removal for the reasons the run refuses it, and while a
    /// log series read before is missing, naming that series. Lists each log folder.
    /// </summary>
    internal void EnsureUnmappedFileRemovalAllowed()
    {
        EnsureUnmappedRunAllowed();

        foreach (var datasource in _datasourceService.GetDatasources())
        {
            datasource.RefreshLogSources();
            var missing = RustLogProcessorService.MissingSeriesPositions(
                _stateService.GetLogSourcePositions(datasource.Name), datasource.LogSourceStems);
            if (missing.Count > 0)
            {
                // The run itself names this case with a key that needs no values, since a card
                // message carries none.
                var series = string.Join(", ", missing.Keys.Order(StringComparer.Ordinal));
                throw new ConflictException(
                    $"Log series {series} of datasource '{datasource.Name}' was read before and is missing.")
                {
                    StageKey = "errors.unmappedRemoval.logSeriesMissing",
                    Context = new Dictionary<string, object?>
                    {
                        ["series"] = series,
                        ["datasource"] = datasource.Name
                    }
                };
            }
        }
    }

    /// <summary>
    /// Refuses an unmapped removal before a full detection scan has measured the cache, while no
    /// download tracker child reports, or while a client download is writing to it. Memory reads only.
    /// </summary>
    private void EnsureUnmappedRunAllowed()
    {
        if (_stateService.GetGameDetectionLastFullScan() == null)
        {
            throw new ConflictException("Run a full detection scan before removing unmapped files.")
            {
                StageKey = "errors.unmappedRemoval.noFullScan"
            };
        }

        // A tracker with no reporting child has no answer, which is not a client download.
        if (_cacheScanGate.StartDownloadWatch() == null)
        {
            throw CacheScanGate.TrackerStopped();
        }

        if (_cacheScanGate.CheckDownloadInProgress() is { } reason)
        {
            throw new DownloadInProgressException(reason)
            {
                StageKey = "errors.unmappedRemoval.downloadInProgress"
            };
        }
    }

    /// <summary>
    /// Registers the unmapped removal and runs it in the background; returns its operation id. The card
    /// always ends Completed, Failed with a reason key, or Cancelled.
    /// </summary>
    internal Task<Guid?> StartUnmappedFileRemovalAsync(UnmappedFilesLimits limits)
    {
        var cts = new CancellationTokenSource();
        var operationId = _operationTracker.RegisterOperation(
            OperationType.UnmappedFileRemoval,
            "Unmapped File Removal",
            cts);
        _operationTracker.UpdateProgress(operationId, 0, "signalr.unmappedRemove.running");

        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            var lockTaken = false;
            var success = false;
            try
            {
                // Inside the try so a Stop while another removal holds the lock ends this card.
                await _cacheLock.WaitAsync(token);
                lockTaken = true;

                EnsureUnmappedRunAllowed();
                _operationStateService.SetUnmappedRemovalRunning(true);

                // A removal decides what is unclaimed from the Downloads table. Every line nginx
                // wrote before these passes start is in that table once the importer says it read
                // every log to the end; a client download from then on stops the removal, here and
                // in the pass below, so no line the table is missing can cost a file. A download
                // shows up in the watch up to about 7 seconds after its request ends, and the
                // removal also stops when the download tracker stops reporting.
                var watchStart = _cacheScanGate.StartDownloadWatch();
                using (var watch = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var download = _cacheScanGate.WaitForDownloadAsync(watchStart, watch.Token);
                    try
                    {
                        foreach (var datasource in _datasourceService.GetDatasources())
                        {
                            token.ThrowIfCancellationRequested();
                            var read = _logProcessor.ReadToEndAsync(datasource, token);
                            if (await Task.WhenAny(read, download) == download)
                            {
                                // The read keeps going after the run stops waiting on it, so a
                                // later fault is logged here rather than lost.
                                _ = read.ContinueWith(
                                    (Task<bool> abandoned) => _logger.LogWarning(
                                        abandoned.Exception,
                                        "[UnmappedRemoval] Log import failed after a download stopped the run"),
                                    CancellationToken.None,
                                    TaskContinuationOptions.OnlyOnFaulted,
                                    TaskScheduler.Default);
                                throw await download;
                            }

                            if (!await read)
                            {
                                // A missing log is named apart from an unread one: resetting the
                                // position forgets the missing series and does not bring it back.
                                datasource.RefreshLogSources();
                                throw RustLogProcessorService.LogsOutOfSight(
                                    _stateService.GetLogSourcePositions(datasource.Name),
                                    datasource.LogSourceStems)
                                    ? new ConflictException(
                                        $"A log series of datasource '{datasource.Name}' is missing; mount or restore its log folder.")
                                    {
                                        StageKey = "errors.unmappedRemoval.logsMissing"
                                    }
                                    : LogsBehind(datasource.Name);
                            }
                        }
                    }
                    finally
                    {
                        await watch.CancelAsync();
                    }
                }

                InvalidateCachedScan();

                var pass = await _gameCacheDetectionService.RunUnmappedFilesPassAsync(
                    operationId,
                    limits,
                    watchStart,
                    (percentComplete, _, _) =>
                    {
                        _operationTracker.UpdateProgress(operationId, percentComplete, "signalr.unmappedRemove.running");
                        return Task.CompletedTask;
                    },
                    token);

                if (pass.OverLimit)
                {
                    _operationTracker.CompleteOperation(operationId, false, "errors.unmappedRemoval.moreThanConfirmed");
                }
                else if (pass.RemoveFailures > 0)
                {
                    _operationTracker.CompleteOperation(operationId, false, "errors.unmappedRemoval.deleteFailed");
                }
                else
                {
                    _operationTracker.UpdateProgress(operationId, 100, "signalr.unmappedRemove.complete");
                    _operationTracker.CompleteOperation(operationId, true);
                    success = true;
                }
            }
            catch (ApiException refusal)
            {
                // A refusal is an expected answer, not a fault.
                _logger.LogInformation(
                    "[UnmappedRemoval] Run refused with {StageKey}: {Reason}", refusal.StageKey, refusal.Message);
                _operationTracker.CompleteOperation(operationId, false, refusal.StageKey);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[UnmappedRemoval] Run canceled");
                _operationTracker.CompleteOperation(operationId, false, cancelled: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UnmappedRemoval] Run failed");
                _operationTracker.CompleteOperation(operationId, false, "errors.unmappedRemoval.failed");
            }
            finally
            {
                if (lockTaken)
                {
                    _operationStateService.SetUnmappedRemovalRunning(false);
                    _cacheLock.Release();
                }

                await _notifications.NotifyAllAsync(
                    SignalREvents.UnmappedFileRemovalComplete,
                    new { operationId, success });
            }
        }, CancellationToken.None);

        return Task.FromResult<Guid?>(operationId);
    }

    private static ConflictException LogsBehind(string datasourceName) =>
        new($"Log ingestion is behind for datasource '{datasourceName}'; wait for it to catch up.")
        {
            StageKey = "errors.unmappedRemoval.logsBehind"
        };
}
