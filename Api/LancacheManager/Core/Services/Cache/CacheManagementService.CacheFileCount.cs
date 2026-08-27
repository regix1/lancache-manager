using System.Collections.Concurrent;
using System.Globalization;
using LancacheManager.Infrastructure.Utilities;

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

        PublishCount(new CacheFileCountState(operationId, null, null));

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
            foreach (var execution in executionPlan.RunnableDatasources)
            {
                var datasource = execution.Datasource;

                var dsCount = await RunRustRemovalProcessAsync<ServiceRemovalProgressData, CacheFileCountReport>(
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
                            null));
                        await onProgress(progressData.PercentComplete, progressData.StageKey);
                    },
                    result => _rustProcessHelper.ReadOutputJsonAsync<CacheFileCountReport>(
                        result.OutputJsonPath,
                        "CacheFileCount"));

                cacheFilesFound += dsCount.CacheFilesFound;

                await _rustProcessHelper.DeleteTempFileAsync(execution.ProgressJsonPath);
                await _rustProcessHelper.DeleteTempFileAsync(execution.OutputJsonPath);
            }

            _logger.LogInformation(
                "{LogPrefix} '{Entity}': {Files} cache file(s) found across {Processed} datasource(s), {Skipped} skipped",
                logPrefix, entityToken, cacheFilesFound, executionPlan.RunnableDatasources.Count, executionPlan.DatasourcesSkipped);

            // Published only after every datasource finished. A number from a partial walk would
            // be smaller than what the removal reaches, which is the shape of the defect this
            // count exists to close.
            PublishCount(new CacheFileCountState(operationId, null, cacheFilesFound));

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
    }

    /// <summary>
    /// How many finished counts stay readable. A confirmation reads its number within a poll of the
    /// count finishing, so a handful is far more than the dialogs can consume, and the cap is what
    /// stops the map growing for the life of the process.
    /// </summary>
    private const int RetainedCacheFileCounts = 8;

    /// <summary>
    /// One entry per count, keyed by its operation id, read by
    /// GET /api/cache/count/{operationId}/status.
    /// </summary>
    /// <remarks>
    /// Keyed rather than a single slot because a second count starting must not make the first one
    /// look failed. `_cacheLock` serializes the counts themselves, but not their registration, so a
    /// count that is only queueing for the lock would otherwise replace an answer the first
    /// dialog had not read yet, and that dialog would report a failed count and refuse to confirm.
    /// The concurrent map also publishes each write to the request thread that reads it.
    /// </remarks>
    private readonly ConcurrentDictionary<Guid, CacheFileCountState> _cacheFileCounts = new();

    /// <summary>Insertion order for <see cref="_cacheFileCounts"/>, so the oldest is evicted first.</summary>
    private readonly ConcurrentQueue<Guid> _cacheFileCountOrder = new();

    /// <summary>One count's operation id, its latest progress context, and its result.</summary>
    /// <param name="CacheFilesFound">Null until every datasource has been walked.</param>
    public sealed record CacheFileCountState(
        Guid OperationId,
        IReadOnlyDictionary<string, object?>? Context,
        int? CacheFilesFound);

    /// <summary>The named count's latest state, or null if it is unknown or has aged out.</summary>
    public CacheFileCountState? GetCacheFileCount(Guid operationId) =>
        _cacheFileCounts.TryGetValue(operationId, out var state) ? state : null;

    internal void PublishCount(CacheFileCountState state)
    {
        if (!_cacheFileCounts.TryAdd(state.OperationId, state))
        {
            // Every later tick of the same count: replace in place, and leave the order queue
            // alone so a long count cannot evict the very answers it is queued behind.
            _cacheFileCounts[state.OperationId] = state;
            return;
        }

        _cacheFileCountOrder.Enqueue(state.OperationId);
        while (_cacheFileCountOrder.Count > RetainedCacheFileCounts
            && _cacheFileCountOrder.TryDequeue(out var oldest))
        {
            _cacheFileCounts.TryRemove(oldest, out _);
        }
    }
}
