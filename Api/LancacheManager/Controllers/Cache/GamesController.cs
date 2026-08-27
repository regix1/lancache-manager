using LancacheManager.Models;
using LancacheManager.Core;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.AspNetCore.Mvc;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;
using static LancacheManager.Controllers.CacheRouteGuards;
using Microsoft.AspNetCore.Authorization;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for game cache detection and management
/// Handles game detection operations and game-specific cache removal
/// </summary>
[ApiController]
[Route("api/games")]
[Authorize(Policy = "AccountHolder")]
public class GamesController : ControllerBase
{
    private readonly GameCacheDetectionService _gameCacheDetectionService;
    private readonly CacheManagementService _cacheManagementService;
    private readonly ISignalRNotificationService _notifications;
    private readonly ILogger<GamesController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly IOperationConflictChecker _conflictChecker;
    private readonly IOperationQueue _operationQueue;
    private readonly DatasourceCapabilityService _capabilityService;

    public GamesController(
        GameCacheDetectionService gameCacheDetectionService,
        CacheManagementService cacheManagementService,
        ISignalRNotificationService notifications,
        ILogger<GamesController> logger,
        IPathResolver pathResolver,
        IUnifiedOperationTracker operationTracker,
        IOperationConflictChecker conflictChecker,
        IOperationQueue operationQueue,
        DatasourceCapabilityService capabilityService)
    {
        _capabilityService = capabilityService;
        _gameCacheDetectionService = gameCacheDetectionService;
        _cacheManagementService = cacheManagementService;
        _notifications = notifications;
        _logger = logger;
        _pathResolver = pathResolver;
        _operationTracker = operationTracker;
        _conflictChecker = conflictChecker;
        _operationQueue = operationQueue;
    }

    private static Dictionary<string, object?> RemovalContext(
        string displayName,
        long? gameAppId = null,
        string? epicAppId = null,
        int? filesDeleted = null,
        long? bytesFreed = null,
        ulong? logEntriesRemoved = null,
        string? errorDetail = null)
    {
        var context = new Dictionary<string, object?>
        {
            ["gameName"] = displayName
        };

        if (gameAppId.HasValue)
        {
            context["gameAppId"] = gameAppId.Value;
        }

        if (!string.IsNullOrWhiteSpace(epicAppId))
        {
            context["epicAppId"] = epicAppId;
        }

        if (filesDeleted.HasValue)
        {
            context["files"] = filesDeleted.Value;
        }

        if (bytesFreed.HasValue)
        {
            context["bytesFreed"] = bytesFreed.Value;
            context["gb"] = Math.Round(bytesFreed.Value / (1024d * 1024d * 1024d), 2);
        }

        if (logEntriesRemoved.HasValue)
        {
            context["logEntries"] = logEntriesRemoved.Value;
        }

        if (!string.IsNullOrWhiteSpace(errorDetail))
        {
            context["errorDetail"] = errorDetail;
        }

        return context;
    }

    /// <summary>
    /// Removes a game from the cache.
    /// </summary>
    /// <remarks>
    /// DELETE is the proper method for removing resources. Deletes every cached file the game
    /// owns, so the caller must declare <see cref="CacheRemovalScope.CacheFiles"/>.
    /// </remarks>
    /// <param name="scope">Must be <see cref="CacheRemovalScope.CacheFiles"/>; any other value is refused.</param>
    [HttpDelete("{appId}")]
    [ProducesResponseType(typeof(QueuedOperationResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(GameRemovalStartResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> RemoveGameFromCacheAsync(long appId, CancellationToken cancellationToken, [FromQuery] string? scope = null)
    {
        var scopeError = EnsureCacheFileScopeDeclared(_logger, scope, $"[RemoveGameFromCache] AppId {appId}:");
        if (scopeError != null)
        {
            return scopeError;
        }

        var capabilityError = DenyIfKeyDependentUnavailable(_capabilityService);
        if (capabilityError != null)
        {
            return capabilityError;
        }

        // CRITICAL: Check write permissions BEFORE starting the operation
        var permissionError = EnsureDirectoriesWritable(
            _pathResolver, _logger, "remove game from cache", $"[RemoveGameFromCache] AppId {appId}:");
        if (permissionError != null)
        {
            return permissionError;
        }

        _logger.LogInformation("Starting background game removal for AppId: {AppId}", appId);

        // Get game name for tracking
        var cachedResults = await _gameCacheDetectionService.GetCachedDetectionAsync();
        var gameName = cachedResults?.Games?.FirstOrDefault(g => g.GameAppId == appId)?.GameName ?? $"Game {appId}";

        // Shared start path so the wait-queue can run it verbatim at promotion time.
        Task<Guid> StartCoreAsync() => StartRemovalAsync(
            entityKey: appId.ToString(),
            displayName: gameName,
            operationLabel: $"Game Removal: {gameName}",
            appId: appId,
            entityKind: "steam",
            epicAppId: null,
            removeFunc: (Guid opId, CancellationToken ct, Func<double, string, Dictionary<string, object?>?, int, long, Task> onProgress) =>
                _cacheManagementService.RemoveGameFromCacheAsync(appId, ct, onProgress, opId),
            onSuccess: async (long _) => await _gameCacheDetectionService.RemoveGameFromCacheAsync(appId));

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        var conflict = await _conflictChecker.CheckAsync(
            OperationType.GameRemoval,
            ConflictScope.SteamGame(appId),
            cancellationToken);
        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.GameRemoval, ConflictScope.SteamGame(appId), $"Game Removal ({gameName})",
                async () => await StartCoreAsync(), cancellationToken));
        }

        var operationId = await StartCoreAsync();

        return Accepted(new GameRemovalStartResponse
        {
            Message = $"Started removal of game {appId} from cache",
            OperationId = operationId,
            AppId = appId.ToString(),
            GameName = gameName,
            Status = OperationStatus.Running
        });
    }

    /// <summary>
    /// Removes an Epic game from the cache by name.
    /// </summary>
    /// <remarks>
    /// Uses the Rust cache_epic_remove binary to delete cache files, log entries, and database
    /// records, the same three-step process as Steam game removal, so the caller must declare
    /// <see cref="CacheRemovalScope.CacheFiles"/>.
    /// </remarks>
    /// <param name="scope">Must be <see cref="CacheRemovalScope.CacheFiles"/>; any other value is refused.</param>
    [HttpDelete("epic/{gameName}")]
    [ProducesResponseType(typeof(QueuedOperationResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(GameRemovalStartResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> RemoveEpicGameFromCacheAsync(string gameName, CancellationToken cancellationToken, [FromQuery] string? scope = null)
    {
        var scopeError = EnsureCacheFileScopeDeclared(_logger, scope, $"[RemoveEpicGame] '{gameName}':");
        if (scopeError != null)
        {
            return scopeError;
        }

        var capabilityError = DenyIfKeyDependentUnavailable(_capabilityService);
        if (capabilityError != null)
        {
            return capabilityError;
        }

        // Check write permissions before starting
        var permissionError = EnsureDirectoriesWritable(
            _pathResolver, _logger, "remove Epic game from cache", $"[RemoveEpicGame] '{gameName}':");
        if (permissionError != null)
        {
            return permissionError;
        }

        _logger.LogInformation("Starting background Epic game removal for: {GameName}", gameName);

        var cachedResults = await _gameCacheDetectionService.GetCachedDetectionAsync();
        var epicGame = cachedResults?.Games?.FirstOrDefault(g =>
            string.Equals(g.GameName, gameName, StringComparison.Ordinal) &&
            string.Equals(g.Service, "epicgames", StringComparison.OrdinalIgnoreCase));
        var epicAppId = epicGame?.EpicAppId;

        // Shared start path so the wait-queue can run it verbatim at promotion time.
        Task<Guid> StartCoreAsync() => StartRemovalAsync(
            entityKey: epicAppId ?? gameName,
            displayName: gameName,
            operationLabel: $"Epic Game Removal: {gameName}",
            appId: null,
            entityKind: "epic",
            epicAppId: epicAppId,
            removeFunc: (Guid opId, CancellationToken ct, Func<double, string, Dictionary<string, object?>?, int, long, Task> onProgress) =>
                _cacheManagementService.RemoveEpicGameFromCacheAsync(gameName, ct, onProgress, opId),
            onSuccess: null);

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        var conflict = await _conflictChecker.CheckAsync(
            OperationType.GameRemoval,
            ConflictScope.EpicGame(epicAppId, gameName),
            cancellationToken);
        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.GameRemoval, ConflictScope.EpicGame(epicAppId, gameName),
                $"Epic Game Removal ({gameName})",
                async () => await StartCoreAsync(), cancellationToken));
        }

        var operationId = await StartCoreAsync();

        return Accepted(new GameRemovalStartResponse
        {
            Message = $"Started removal of Epic game {gameName} from cache",
            OperationId = operationId,
            AppId = string.Empty,
            GameName = gameName,
            Status = OperationStatus.Running
        });
    }

    /// <summary>
    /// Removes a named game (Blizzard, Riot, Xbox) from the cache.
    /// </summary>
    /// <remarks>
    /// These games have no Steam AppId and no Epic AppId; their identity is (Service, GameName).
    /// Dispatches to the per-service Rust binary (cache_{service}_remove) to delete cache files,
    /// log entries, and database records, the same three-step process as Epic game removal, so the
    /// caller must declare <see cref="CacheRemovalScope.CacheFiles"/>.
    /// </remarks>
    /// <param name="scope">Must be <see cref="CacheRemovalScope.CacheFiles"/>; any other value is refused.</param>
    [HttpDelete("named/{service}/{gameName}")]
    [ProducesResponseType(typeof(QueuedOperationResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(GameRemovalStartResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> RemoveNamedGameFromCacheAsync(string service, string gameName, CancellationToken cancellationToken, [FromQuery] string? scope = null)
    {
        var scopeError = EnsureCacheFileScopeDeclared(_logger, scope, $"[RemoveNamedGame] '{service}' / '{gameName}':");
        if (scopeError != null)
        {
            return scopeError;
        }

        var capabilityError = DenyIfKeyDependentUnavailable(_capabilityService);
        if (capabilityError != null)
        {
            return capabilityError;
        }

        // Check write permissions before starting
        var permissionError = EnsureDirectoriesWritable(
            _pathResolver, _logger, "remove game from cache", $"[RemoveNamedGame] '{service}' / '{gameName}':");
        if (permissionError != null)
        {
            return permissionError;
        }

        _logger.LogInformation("Starting background named game removal for: '{Service}' / '{GameName}'", service, gameName);

        // Verify the named game exists in cached detection (by GameName + Service); not strictly required
        // for the removal to run, but keeps logging/consistency aligned with the Epic precedent.
        var cachedResults = await _gameCacheDetectionService.GetCachedDetectionAsync();
        _ = cachedResults?.Games?.FirstOrDefault(g =>
            string.Equals(g.GameName, gameName, StringComparison.Ordinal) &&
            string.Equals(g.Service, service, StringComparison.OrdinalIgnoreCase));

        // Shared start path so the wait-queue can run it verbatim at promotion time.
        Task<Guid> StartCoreAsync() => StartRemovalAsync(
            entityKey: $"{service}:{gameName}",
            displayName: gameName,
            operationLabel: $"Game Removal: {gameName}",
            appId: null,
            entityKind: "named",
            epicAppId: null,
            removeFunc: (Guid opId, CancellationToken ct, Func<double, string, Dictionary<string, object?>?, int, long, Task> onProgress) =>
                _cacheManagementService.RemoveNamedGameFromCacheAsync(service, gameName, ct, onProgress, opId),
            onSuccess: null);

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        var conflict = await _conflictChecker.CheckAsync(
            OperationType.GameRemoval,
            ConflictScope.NamedGame(service, gameName),
            cancellationToken);
        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.GameRemoval, ConflictScope.NamedGame(service, gameName),
                $"Game Removal ({gameName})",
                async () => await StartCoreAsync(), cancellationToken));
        }

        var operationId = await StartCoreAsync();

        return Accepted(new GameRemovalStartResponse
        {
            Message = $"Started removal of game {gameName} from cache",
            OperationId = operationId,
            AppId = string.Empty,
            GameName = gameName,
            Status = OperationStatus.Running
        });
    }

    /// <summary>
    /// Shared background removal wrapper for Steam, Epic, and named (Blizzard/Riot) game removal.
    /// Handles: tracker registration, Started event, Task.Run with progress/complete/error/cancel.
    /// </summary>
    /// <param name="entityKey">Tracker entity key (e.g., "123" for Steam, "epic-GameName" for Epic)</param>
    /// <param name="displayName">Game display name for notifications</param>
    /// <param name="operationLabel">Operation label for the tracker (e.g., "Game Removal: Halo")</param>
    /// <param name="appId">Steam AppId (0 for Epic games)</param>
    /// <param name="removeFunc">The actual removal function that accepts cancellation token and progress callback</param>
    /// <param name="onSuccess">Optional callback after successful removal (e.g., Steam removes from detection cache)</param>
    private async Task<Guid> StartRemovalAsync(
        string entityKey,
        string displayName,
        string operationLabel,
        long? appId,
        string entityKind,
        string? epicAppId,
        Func<Guid, CancellationToken, Func<double, string, Dictionary<string, object?>?, int, long, Task>, Task<CacheManagementService.GameCacheRemovalReport>> removeFunc,
        Func<long, Task>? onSuccess)
    {
        var isEpic = entityKind == "epic";
        // Both Epic and named games are name-keyed: their payloads carry GameAppId=null
        // (Steam is the only appId-keyed kind).
        var isNameKeyed = entityKind != "steam";
        // Named (Blizzard/Riot/Xbox) are name-keyed but not Epic — use appId-free stage keys.
        var isNamed = isNameKeyed && !isEpic;
        var startingStageKey = isEpic ? "signalr.epicRemove.starting" : (isNamed ? "signalr.namedRemove.starting" : "signalr.gameRemove.starting");
        var completeStageKey = isEpic ? "signalr.epicRemove.complete" : (isNamed ? "signalr.namedRemove.complete" : "signalr.gameRemove.complete");
        var cancelledStageKey = isEpic ? "signalr.epicRemove.cancelled" : "signalr.gameRemove.cancelled";
        var errorStageKey = isEpic ? "signalr.epicRemove.error.fatal" : "signalr.gameRemove.error.fatal";

        // Register with unified operation tracker for centralized cancellation and tracking.
        // EntityKind + EpicAppId let the REST recovery endpoint /api/cache/removals/active
        // project scope-aware identity onto GameRemovalInfo.
        var removalMetrics = new RemovalMetrics
        {
            EntityKey = entityKey,
            EntityName = displayName,
            EntityKind = entityKind,
            EpicAppId = epicAppId
        };
        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            _operationTracker,
            _notifications,
            new TrackedRemovalOperationRunner.RemovalOperationConfig<CacheManagementService.GameCacheRemovalReport>(
                OperationType: OperationType.GameRemoval,
                OperationLabel: operationLabel,
                Metadata: removalMetrics,
                StartedEventName: SignalREvents.GameRemovalStarted,
                BuildStartedPayload: id => new GameRemovalStarted(
                    OperationId: id,
                    GameAppId: isNameKeyed ? null : appId,
                    EpicAppId: isEpic ? epicAppId : null,
                    GameName: displayName,
                    StageKey: startingStageKey,
                    Timestamp: DateTime.UtcNow,
                    Context: RemovalContext(displayName, appId, epicAppId)),
                ProgressEventName: SignalREvents.GameRemovalProgress,
                InitialStageKey: startingStageKey,
                BuildInitialProgressPayload: id => new GameRemovalProgress(
                    OperationId: id,
                    GameAppId: isNameKeyed ? null : appId,
                    EpicAppId: isEpic ? epicAppId : null,
                    GameName: displayName,
                    StageKey: startingStageKey,
                    Context: RemovalContext(displayName, appId, epicAppId)),
                BuildProgressPayload: (id, update) => new GameRemovalProgress(
                    OperationId: id,
                    GameAppId: isNameKeyed ? null : appId,
                    EpicAppId: isEpic ? epicAppId : null,
                    GameName: displayName,
                    StageKey: update.StageKey,
                    PercentComplete: update.PercentComplete,
                    FilesDeleted: update.FilesDeleted,
                    BytesFreed: update.BytesFreed,
                    Context: update.Context),
                CompleteEventName: SignalREvents.GameRemovalComplete,
                FinalizingStageKey: "signalr.gameRemove.finalizing",
                BuildFinalizingProgressPayload: (id, report) => new GameRemovalProgress(
                    OperationId: id,
                    GameAppId: isNameKeyed ? null : appId,
                    EpicAppId: isEpic ? epicAppId : null,
                    GameName: displayName,
                    StageKey: "signalr.gameRemove.finalizing",
                    PercentComplete: 100.0,
                    FilesDeleted: report.CacheFilesDeleted,
                    BytesFreed: (long)report.TotalBytesFreed,
                    Context: RemovalContext(
                        displayName,
                        appId,
                        epicAppId,
                        filesDeleted: report.CacheFilesDeleted,
                        bytesFreed: (long)report.TotalBytesFreed,
                        logEntriesRemoved: report.LogEntriesRemoved)),
                BuildSuccessPayload: (id, report) => new GameRemovalComplete(
                    Success: true,
                    OperationId: id,
                    GameAppId: isNameKeyed ? null : appId,
                    EpicAppId: isEpic ? epicAppId : null,
                    StageKey: completeStageKey,
                    GameName: displayName,
                    FilesDeleted: report.CacheFilesDeleted,
                    BytesFreed: (long)report.TotalBytesFreed,
                    LogEntriesRemoved: report.LogEntriesRemoved,
                    Context: RemovalContext(
                        displayName,
                        appId,
                        epicAppId,
                        filesDeleted: report.CacheFilesDeleted,
                        bytesFreed: (long)report.TotalBytesFreed,
                        logEntriesRemoved: report.LogEntriesRemoved)),
                BuildCancelledPayload: id => new GameRemovalComplete(
                    Success: false,
                    OperationId: id,
                    GameAppId: isNameKeyed ? null : appId,
                    EpicAppId: isEpic ? epicAppId : null,
                    StageKey: cancelledStageKey,
                    GameName: displayName,
                    Context: RemovalContext(displayName, appId, epicAppId),
                    // Without this the card reads as a failure: success:false alone is
                    // indistinguishable from a genuine error, so the UI paints it red and
                    // announces it as an alert. A run the user stopped is not a fault.
                    Cancelled: true),
                BuildErrorProgressPayload: (id, ex) => new GameRemovalProgress(
                    OperationId: id,
                    GameAppId: isNameKeyed ? null : appId,
                    EpicAppId: isEpic ? epicAppId : null,
                    GameName: displayName,
                    StageKey: errorStageKey,
                    PercentComplete: 0.0,
                    Context: RemovalContext(displayName, appId, epicAppId, errorDetail: ex.Message)),
                BuildErrorCompletePayload: (id, ex) => new GameRemovalComplete(
                    Success: false,
                    OperationId: id,
                    GameAppId: isNameKeyed ? null : appId,
                    EpicAppId: isEpic ? epicAppId : null,
                    StageKey: errorStageKey,
                    GameName: displayName,
                    Context: RemovalContext(displayName, appId, epicAppId, errorDetail: ex.Message),
                    Error: ex.Message),
                ExecuteAsync: (opId, ct, onProgress) => removeFunc(
                    opId,
                    ct,
                    (percentComplete, stageKey, context, filesDeleted, bytesFreed) =>
                        onProgress(new TrackedRemovalOperationRunner.RemovalProgressUpdate(
                            percentComplete,
                            stageKey,
                            context,
                            filesDeleted,
                            bytesFreed))),
                ApplyProgressMetrics: (metrics, update) =>
                {
                    metrics.FilesDeleted = update.FilesDeleted;
                    metrics.BytesFreed = update.BytesFreed;
                },
                ApplyFinalMetrics: (metrics, report) =>
                {
                    metrics.FilesDeleted = report.CacheFilesDeleted;
                    metrics.BytesFreed = (long)report.TotalBytesFreed;
                },
                OnSuccessAsync: async _ =>
                {
                    if (onSuccess != null && appId.HasValue)
                    {
                        await onSuccess(appId.Value);
                    }
                },
                LogSuccess: (_, report) =>
                {
                    _logger.LogInformation(
                        "Game removal completed for {EntityKey} ({DisplayName}) - Deleted {Files} files, freed {Bytes} bytes",
                        entityKey,
                        displayName,
                        report.CacheFilesDeleted,
                        report.TotalBytesFreed);
                },
                LogCancelled: _ =>
                {
                    _logger.LogInformation("Game removal cancelled for {EntityKey}", entityKey);
                },
                LogFailure: (_, ex) =>
                {
                    _logger.LogError(ex, "Error during game removal for {EntityKey}", entityKey);
                }));

        return operationId;
    }

    /// <summary>
    /// Starts game detection in the cache.
    /// </summary>
    /// <remarks>
    /// POST is acceptable since this starts an asynchronous operation.
    /// </remarks>
    /// <param name="forceRefresh">
    /// When true, runs a full scan of every cache entry. When false (default), runs a quick
    /// incremental scan that only looks at what changed since the last detection.
    /// </param>
    [HttpPost("detect")]
    [ProducesResponseType(typeof(QueuedOperationResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(GameDetectionStartResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> DetectGamesAsync([FromQuery] bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var capabilityError = DenyIfKeyDependentUnavailable(_capabilityService);
        if (capabilityError != null)
        {
            return capabilityError;
        }

        // forceRefresh=true means full scan (incremental=false)
        // forceRefresh=false means quick scan (incremental=true)
        var incremental = !forceRefresh;

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        // StartDetectionAsync returns null only for the already-running race; capability
        // refusals remain failures so queue promotion cannot misclassify them as an active scan.
        Task<Guid?> StartDetectionCoreAsync() =>
            _gameCacheDetectionService.StartDetectionAsync(incremental);

        var conflict = await _conflictChecker.CheckAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            cancellationToken);
        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.GameDetection, ConflictScope.Bulk(), "Game Detection",
                StartDetectionCoreAsync, cancellationToken));
        }

        var operationId = await StartDetectionCoreAsync();

        if (operationId == null)
        {
            // Race: detection started between our checker call and StartDetectionAsync - park it
            // (the queue re-checks under its gate and deduplicates against the now-active op).
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.GameDetection, ConflictScope.Bulk(), "Game Detection",
                StartDetectionCoreAsync, cancellationToken));
        }

        _logger.LogInformation("Started game detection operation: {OperationId} (forceRefresh={ForceRefresh}, incremental={Incremental})", operationId, forceRefresh, incremental);

        return Accepted(new GameDetectionStartResponse
        {
            Message = forceRefresh ? "Full scan started" : "Incremental scan started",
            OperationId = operationId.Value,
            Status = OperationStatus.Running
        });
    }

    /// <summary>
    /// Gets the currently running detection operation.
    /// </summary>
    [HttpGet("detect/active")]
    [ProducesResponseType(typeof(ActiveDetectionResponse), StatusCodes.Status200OK)]
    public ActionResult<ActiveDetectionResponse> GetActiveDetection()
    {
        var activeOperation = _gameCacheDetectionService.GetActiveOperation();

        if (activeOperation == null)
        {
            return Ok(new ActiveDetectionResponse { IsProcessing = false, Operation = null });
        }

        return Ok(new ActiveDetectionResponse
        {
            IsProcessing = true,
            Operation = activeOperation,
            ShowNotification = activeOperation.ShowNotification
        });
    }

    /// <summary>
    /// Gets cached game detection results.
    /// </summary>
    /// <remarks>
    /// Returns the full (non-slim) shape, including per-game cache_file_paths, which the
    /// dashboard's slim variant deliberately omits to keep its payload small. Used by the admin
    /// management screen, which needs the actual file paths to act on.
    /// </remarks>
    [HttpGet("detect/cached")]
    [ProducesResponseType(typeof(CachedDetectionResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CachedDetectionResponse>> GetCachedDetectionAsync()
    {
        // Non-slim response carries cache_file_paths, which the singleton cache deliberately
        // omits - load them per request instead.
        var cachedResults = await _gameCacheDetectionService.GetCachedDetectionWithPathsAsync(HttpContext.RequestAborted);

        if (cachedResults == null)
        {
            return Ok(CachedDetectionResponseBuilder.BuildEmpty());
        }

        var games = cachedResults.Games ?? [];

        return Ok(CachedDetectionResponseBuilder.Build(
            games,
            cachedResults.Services,
            cachedResults.TotalServicesDetected,
            cachedResults.StartTime.AsUtc(),
            slimForDashboard: false,
            diskSummary: cachedResults.DiskSummary,
            summaryComputedAtUtc: cachedResults.SummaryComputedAtUtc,
            detectionStale: await _cacheManagementService.IsDetectionSummaryStaleAsync()));
    }


}
