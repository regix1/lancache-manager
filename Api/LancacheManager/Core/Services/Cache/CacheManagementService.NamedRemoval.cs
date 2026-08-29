using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Core.Services;

public partial class CacheManagementService
{
    /// <summary>
    /// Remove all cache files, log entries, and database records for a named game
    /// (Blizzard/Riot/Xbox - keyed by Service + GameName, with GameAppId/EpicAppId both null).
    /// Dispatches to the per-service Rust binary (cache_{service}_remove), each a thin wrapper
    /// over the shared named-removal core, which mirrors the Epic game removal flow:
    /// 1. Deletes cache files from disk (via MD5 cache path calculation)
    /// 2. Removes matching lines from access log text files
    /// 3. Deletes LogEntry and Download records from the database
    /// </summary>
    public async Task<GameCacheRemovalReport> RemoveNamedGameFromCacheAsync(
        string service,
        string gameName,
        CancellationToken cancellationToken = default,
        Func<double, string, Dictionary<string, object?>?, int, long, Task>? onProgress = null,
        Guid? operationId = null)
    {
        RequireUnambiguousKeyScheme();

        // Sanitize both user-provided arguments to prevent process argument injection
        service = RustProcessHelper.SanitizeProcessArgument(service);
        gameName = RustProcessHelper.SanitizeProcessArgument(gameName);

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            // Rust positional args (LOCKED CONTRACT): log_dir cache_dir game_name output_json progress_json.
            // The owning service is pinned by the per-service binary (cache_{service}_remove), so it is
            // NOT passed as a positional arg - the contract matches the Epic remover.
            var rustBinaryPath = _pathResolver.GetRustNamedGameRemoverPath(service);
            var aggregatedReport = await RunGameRemovalAcrossDatasourcesAsync(
                logTag: "[NamedGameRemoval]",
                rustBinaryPath: rustBinaryPath,
                planDescription: "Named game cache remover",
                planName: "named_removal",
                target: gameName,
                targetDescription: $"named game '{service}' / '{gameName}'",
                rustProcessName: Path.GetFileNameWithoutExtension(rustBinaryPath),
                outputReadLabel: "NamedGameRemoval",
                aggregatedReport: new GameCacheRemovalReport { GameAppId = 0, GameName = gameName },
                cancellationToken: cancellationToken,
                onProgress: onProgress,
                operationId: operationId);

            // Remove this named game from cached detection results so page reload shows correct data.
            // Identity is (Service, GameName) with both Steam and Epic ids null. Mirrors the Steam
            // removal's detection-row cleanup; without it the (xbox/blizzard/riot) detection row
            // survives and the game keeps showing in the Game Cache Detection grid after the frontend
            // refetch (the Xbox cache-split stores Service='xbox' lowercase, matched case-insensitively).
            await _gameCacheDetectionService.RemoveNamedGameFromCacheAsync(service, gameName);

            await FinalizeGameRemovalAsync(cancellationToken);

            return aggregatedReport;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
