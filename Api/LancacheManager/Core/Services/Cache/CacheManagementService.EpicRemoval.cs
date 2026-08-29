using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Core.Services;

public partial class CacheManagementService
{
    /// <summary>
    /// Remove all cache files, log entries, and database records for an Epic game (keyed by
    /// GameName, matching the Rust cache_epic_remove delete):
    /// 1. Deletes cache files from disk (via MD5 cache path calculation)
    /// 2. Removes matching lines from access log text files
    /// 3. Deletes LogEntry and Download records from the database
    /// </summary>
    public async Task<GameCacheRemovalReport> RemoveEpicGameFromCacheAsync(
        string gameName,
        CancellationToken cancellationToken = default,
        Func<double, string, Dictionary<string, object?>?, int, long, Task>? onProgress = null,
        Guid? operationId = null)
    {
        RequireUnambiguousKeyScheme();

        // Sanitize user-provided game name to prevent process argument injection
        gameName = RustProcessHelper.SanitizeProcessArgument(gameName);

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            var aggregatedReport = await RunGameRemovalAcrossDatasourcesAsync(
                logTag: "[EpicGameRemoval]",
                rustBinaryPath: _pathResolver.GetRustEpicRemoverPath(),
                planDescription: "Epic game cache remover",
                planName: "epic_removal",
                target: gameName,
                targetDescription: $"Epic game '{gameName}'",
                rustProcessName: "cache_epic_remove",
                outputReadLabel: "EpicGameRemoval",
                aggregatedReport: new GameCacheRemovalReport { GameAppId = 0, GameName = gameName },
                cancellationToken: cancellationToken,
                onProgress: onProgress,
                operationId: operationId);

            // Remove this Epic game from cached detection results so page reload shows correct data.
            // Epic detection rows carry EpicAppId != null; removal is keyed by GameName (mirrors the
            // Rust cache_epic_remove delete). Mirrors the Steam/named detection-row cleanup; without it
            // the Epic detection row only got pruned later by the Epic mapping loop's full re-detection.
            await _gameCacheDetectionService.RemoveEpicGameFromCacheAsync(gameName);

            await FinalizeGameRemovalAsync(cancellationToken);

            return aggregatedReport;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
