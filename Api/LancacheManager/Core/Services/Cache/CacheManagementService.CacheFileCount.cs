using System.Collections.Concurrent;
using System.Globalization;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

public partial class CacheManagementService
{
    /// <summary>
    /// Counts the cache files a removal would delete, and deletes none of them.
    /// </summary>
    /// <remarks>
    /// Runs the same remover binary the removal runs, with --count-only, so it walks the same URL
    /// set and stat-probes the same on-disk slices, then exits before its delete loop. The
    /// datasource plan is built with the same writability rules, so a datasource the removal would
    /// skip is not counted either. The walk costs what the removal's own collection phase costs,
    /// which is minutes on an entity with hundreds of thousands of logged URLs.
    /// </remarks>
    private async Task<int> RunCacheFileCountAsync(
        string logPrefix,
        string rustBinaryPath,
        string binaryDescription,
        string outputPrefix,
        string progressPrefix,
        string entityToken,
        string identityArgument,
        bool requireWritableLogs,
        Guid operationId,
        Func<double, string, Task> onProgress,
        CancellationToken cancellationToken)
    {
        // Same fail-closed key-scheme gate every removal uses: an ambiguous scheme would count
        // files under a recipe the removal would refuse to run.
        var capabilityDenial = _capabilityService.CheckAllCanMapLogicalObjects();
        if (capabilityDenial != null)
        {
            throw new InvalidOperationException(capabilityDenial);
        }

        PublishCount(new CacheFileCountState(operationId, null, null, null, null));

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("{LogPrefix} Counting cache files for '{Entity}'", logPrefix, entityToken);

            var executionPlan = PrepareRemovalExecutionPlan(
                logPrefix,
                rustBinaryPath,
                binaryDescription,
                outputPrefix,
                progressPrefix,
                entityToken,
                requireWritableLogs);

            var cacheFilesFound = 0;
            long bytesFound = 0;
            foreach (var execution in executionPlan.RunnableDatasources)
            {
                var datasource = execution.Datasource;

                var dsCount = await RunRustRemovalProcessAsync<ServiceRemovalProgress, CacheFileCountReport>(
                    logPrefix,
                    execution,
                    () =>
                    {
                        var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                            rustBinaryPath,
                            $"\"{datasource.LogPath}\" \"{datasource.CachePath}\" {identityArgument} \"{execution.OutputJsonPath}\" \"{execution.ProgressJsonPath}\" --progress --count-only --key-scheme {_capabilityService.GetKeySchemeWireValue(datasource)}");
                        _logger.LogInformation("{LogPrefix} Running count for datasource '{DatasourceName}': {Binary} {Args}",
                            logPrefix, datasource.Name, rustBinaryPath, startInfo.Arguments);
                        return startInfo;
                    },
                    "cache_file_counter",
                    cancellationToken,
                    operationId,
                    async progressData =>
                    {
                        PublishCount(new CacheFileCountState(
                            operationId,
                            progressData.Context,
                            null,
                            null,
                            null));
                        await onProgress(progressData.PercentComplete, progressData.StageKey);
                    },
                    result => _rustProcessHelper.ReadOutputJsonAsync<CacheFileCountReport>(
                        result.OutputJsonPath,
                        "CacheFileCount"));

                cacheFilesFound += dsCount.CacheFilesFound;
                bytesFound += dsCount.BytesFound;

                await _rustProcessHelper.DeleteTempFileAsync(execution.ProgressJsonPath);
                await _rustProcessHelper.DeleteTempFileAsync(execution.OutputJsonPath);
            }

            _logger.LogInformation(
                "{LogPrefix} '{Entity}': {Files} cache file(s) found across {Processed} datasource(s), {Skipped} skipped",
                logPrefix, entityToken, cacheFilesFound, executionPlan.RunnableDatasources.Count, executionPlan.DatasourcesSkipped);

            // Published only after every datasource finished. A number from a partial walk would
            // be smaller than what the removal reaches, which is the shape of the defect this
            // count exists to close.
            PublishCount(new CacheFileCountState(operationId, null, cacheFilesFound, bytesFound, null));

            return cacheFilesFound;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public Task<int> CountServiceCacheFilesAsync(
        string serviceName,
        Guid operationId,
        Func<double, string, Task> onProgress,
        CancellationToken cancellationToken)
    {
        serviceName = RustProcessHelper.SanitizeProcessArgument(serviceName);

        return RunCacheFileCountAsync(
            "[ServiceCount]",
            _pathResolver.GetRustServiceRemoverPath(),
            "Service remover",
            "service_count_output",
            "service_count",
            serviceName,
            $"\"{serviceName}\"",
            requireWritableLogs: true,
            operationId,
            onProgress,
            cancellationToken);
    }

    public Task<int> CountGameCacheFilesAsync(
        long gameAppId,
        Guid operationId,
        Func<double, string, Task> onProgress,
        CancellationToken cancellationToken) =>
        RunCacheFileCountAsync(
            "[GameCount]",
            _pathResolver.GetRustSteamRemoverPath(),
            "Game cache remover",
            "game_count_output",
            "game_count",
            gameAppId.ToString(CultureInfo.InvariantCulture),
            gameAppId.ToString(CultureInfo.InvariantCulture),
            requireWritableLogs: false,
            operationId,
            onProgress,
            cancellationToken);

    public Task<int> CountEpicGameCacheFilesAsync(
        string gameName,
        Guid operationId,
        Func<double, string, Task> onProgress,
        CancellationToken cancellationToken)
    {
        gameName = RustProcessHelper.SanitizeProcessArgument(gameName);

        return RunCacheFileCountAsync(
            "[EpicGameCount]",
            _pathResolver.GetRustEpicRemoverPath(),
            "Epic game cache remover",
            "epic_count_output",
            "epic_count",
            gameName,
            $"\"{gameName}\"",
            requireWritableLogs: false,
            operationId,
            onProgress,
            cancellationToken);
    }

    public Task<int> CountNamedGameCacheFilesAsync(
        string service,
        string gameName,
        Guid operationId,
        Func<double, string, Task> onProgress,
        CancellationToken cancellationToken)
    {
        service = RustProcessHelper.SanitizeProcessArgument(service);
        gameName = RustProcessHelper.SanitizeProcessArgument(gameName);

        return RunCacheFileCountAsync(
            "[NamedGameCount]",
            _pathResolver.GetRustNamedGameRemoverPath(service),
            "Named game cache remover",
            "named_count_output",
            "named_count",
            gameName,
            $"\"{gameName}\"",
            requireWritableLogs: false,
            operationId,
            onProgress,
            cancellationToken);
    }

    /// <summary>What a count run writes for one datasource.</summary>
    private sealed class CacheFileCountReport
    {
        [System.Text.Json.Serialization.JsonPropertyName("cache_files_found")]
        public int CacheFilesFound { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("bytes_found")]
        public long BytesFound { get; set; }
    }

    /// <summary>
    /// One entry per count, keyed by its operation id, read by
    /// GET /api/cache/count/{operationId}/status.
    /// </summary>
    /// <remarks>
    /// Keyed rather than a single slot because a second count starting must not make the first one
    /// look failed. `_cacheLock` serializes the game and service counts (the unmapped count takes no
    /// lock and can run beside them), but not their registration, so a
    /// count that is only queueing for the lock would otherwise replace an answer the first
    /// dialog had not read yet, and that dialog would report a failed count and refuse to confirm.
    /// The concurrent map also publishes each write to the request thread that reads it.
    /// </remarks>
    private readonly ConcurrentDictionary<Guid, CacheFileCountState> _cacheFileCounts = new();

    /// <summary>
    /// The ids of each finished answer no dialog has read yet, oldest first: the count's own id,
    /// plus the waiting id when it started from the queue. An answer stays until its dialog reads
    /// it, so a late poll still reads its result however many other counts, of this game or any
    /// other, finished since. Guarded by locking itself.
    /// </summary>
    private readonly List<List<Guid>> _unreadCounts = new();

    /// <summary>
    /// How many answers stay unread before the oldest is dropped. An answer whose dialog closed
    /// between the count ending and its next poll, or that an API caller never asks for, is never
    /// read, so without a limit those would stay for the life of the process. A dialog polls every
    /// second, and dropping one it still asks for takes this many newer answers finishing before
    /// that poll.
    /// </summary>
    internal const int MaxUnreadCounts = 256;

    /// <summary>One count's operation id, its latest progress context, and its result.</summary>
    /// <param name="CacheFilesFound">Null until every datasource has been walked.</param>
    /// <param name="BytesFound">Bytes of the files found; null until every datasource has been walked.</param>
    /// <param name="UnmappedServices">The unmapped count's files by service; null for the other counts.</param>
    /// <param name="EndingKey">
    /// The reason key of a count that ended without a number. Kept as its answer, because the
    /// tracker drops the count's operation 10 s after it ends and a sleeping dialog asks later.
    /// </param>
    public sealed record CacheFileCountState(
        Guid OperationId,
        IReadOnlyDictionary<string, object?>? Context,
        int? CacheFilesFound,
        long? BytesFound,
        IReadOnlyList<UnmappedService>? UnmappedServices,
        string? EndingKey = null);

    /// <summary>The named count's latest state, or null if it is unknown, was dropped as an unanswered progress entry, or its dialog already read its answer.</summary>
    public CacheFileCountState? GetCacheFileCount(Guid operationId)
    {
        if (!_cacheFileCounts.TryGetValue(operationId, out var state))
        {
            return null;
        }

        if (state.CacheFilesFound != null || state.EndingKey != null)
        {
            // The dialog stops asking once it has its answer, so the answer is dropped here.
            lock (_unreadCounts)
            {
                var unread = _unreadCounts.FindIndex(ids => ids.Contains(operationId));
                if (unread >= 0)
                {
                    DropCounts(_unreadCounts[unread]);
                    _unreadCounts.RemoveAt(unread);
                }
            }
        }

        return state;
    }

    private void DropCounts(IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
        {
            _cacheFileCounts.TryRemove(id, out _);
        }
    }

    internal void PublishCount(CacheFileCountState state)
    {
        if (state.CacheFilesFound == null && state.EndingKey == null)
        {
            if (_cacheFileCounts.TryAdd(state.OperationId, state))
            {
                // A count that ended before publishing an answer leaves its progress entry, so it
                // is dropped here, once its operation has ended, instead of staying for the life
                // of the process.
                foreach (var (id, count) in _cacheFileCounts)
                {
                    if (count.CacheFilesFound == null
                        && count.EndingKey == null
                        && id != state.OperationId
                        && _operationTracker.GetOperation(id) is not { CompletedAt: null })
                    {
                        // Only the progress value read above: an answer published since then stays.
                        _cacheFileCounts.TryRemove(new KeyValuePair<Guid, CacheFileCountState>(id, count));
                    }
                }
            }
            else
            {
                _cacheFileCounts[state.OperationId] = state;
            }

            return;
        }

        // A count that started from the queue is polled by the waiting id the browser holds, and the
        // tracker drops that id's link to this count 10 s after the count ends, so the finished count
        // is also kept under the waiting id. Both are written under the lock, so a poll that sees the
        // answer drops it only after it is listed.
        lock (_unreadCounts)
        {
            List<Guid> answerIds = [state.OperationId];
            _cacheFileCounts[state.OperationId] = state;
            if (_operationTracker.GetOperation(state.OperationId)?.PreviousOperationId is { } waitingId)
            {
                answerIds.Add(waitingId);
                _cacheFileCounts[waitingId] = state with { OperationId = waitingId };
            }

            _unreadCounts.Add(answerIds);
            if (_unreadCounts.Count > MaxUnreadCounts)
            {
                DropCounts(_unreadCounts[0]);
                _unreadCounts.RemoveAt(0);
            }
        }
    }
}
