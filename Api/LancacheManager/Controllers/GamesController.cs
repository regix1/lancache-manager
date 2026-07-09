using LancacheManager.Models;
using LancacheManager.Core;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.AspNetCore.Mvc;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;
using Microsoft.AspNetCore.Authorization;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for game cache detection and management
/// Handles game detection operations and game-specific cache removal
/// </summary>
[ApiController]
[Route("api/games")]
[Authorize(Policy = "AdminOnly")]
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

    public GamesController(
        GameCacheDetectionService gameCacheDetectionService,
        CacheManagementService cacheManagementService,
        ISignalRNotificationService notifications,
        ILogger<GamesController> logger,
        IPathResolver pathResolver,
        IUnifiedOperationTracker operationTracker,
        IOperationConflictChecker conflictChecker,
        IOperationQueue operationQueue)
    {
        _gameCacheDetectionService = gameCacheDetectionService;
        _cacheManagementService = cacheManagementService;
        _notifications = notifications;
        _logger = logger;
        _pathResolver = pathResolver;
        _operationTracker = operationTracker;
        _conflictChecker = conflictChecker;
        _operationQueue = operationQueue;
    }

    /// <summary>
    /// Checks cache and logs directory write permissions.
    /// Returns a BadRequest IActionResult with PUID/PGID error message if not writable, or null if writable.
    /// </summary>
    private BadRequestObjectResult? EnsureDirectoriesWritable(string operationDescription)
    {
        var cacheWritable = _pathResolver.IsCacheWritable();
        var logsWritable = _pathResolver.IsLogsWritable();

        if (cacheWritable && logsWritable)
            return null;

        var errors = new List<string>();
        if (!cacheWritable) errors.Add("cache directory is read-only");
        if (!logsWritable) errors.Add("logs directory is read-only");

        var errorMessage = $"Cannot {operationDescription}: {string.Join(" and ", errors)}. " +
            "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
            $"The lancache container is configured to run as UID/GID {ContainerEnvironment.UidGid} (configured via PUID/PGID environment variables).";

        return BadRequest(new ErrorResponse { Error = errorMessage });
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
    /// DELETE /api/games/{appId} - Remove game from cache
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [HttpDelete("{appId}")]
    public async Task<IActionResult> RemoveGameFromCacheAsync(long appId, CancellationToken cancellationToken)
    {
        // CRITICAL: Check write permissions BEFORE starting the operation
        var permissionError = EnsureDirectoriesWritable("remove game from cache");
        if (permissionError != null)
        {
            _logger.LogWarning("[RemoveGameFromCache] Permission check failed for AppId {AppId}", appId);
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
    /// DELETE /api/games/epic/{gameName} - Remove Epic game by name
    /// Uses the Rust cache_epic_remove binary to delete cache files, log entries,
    /// and database records - same three-step process as Steam game removal.
    /// </summary>
    [HttpDelete("epic/{gameName}")]
    public async Task<IActionResult> RemoveEpicGameFromCacheAsync(string gameName, CancellationToken cancellationToken)
    {
        // Check write permissions before starting
        var permissionError = EnsureDirectoriesWritable("remove Epic game from cache");
        if (permissionError != null)
        {
            _logger.LogWarning("[RemoveEpicGame] Permission check failed for '{GameName}'", gameName);
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
    /// DELETE /api/games/named/{service}/{gameName} - Remove a named (Blizzard/Riot/Xbox) game by Service + GameName.
    /// These games have no Steam AppId and no Epic AppId; their identity is (Service, GameName).
    /// Dispatches to the per-service Rust binary (cache_{service}_remove) to delete cache files, log
    /// entries, and database records - same three-step process as Epic game removal.
    /// </summary>
    [HttpDelete("named/{service}/{gameName}")]
    public async Task<IActionResult> RemoveNamedGameFromCacheAsync(string service, string gameName, CancellationToken cancellationToken)
    {
        // Check write permissions before starting
        var permissionError = EnsureDirectoriesWritable("remove game from cache");
        if (permissionError != null)
        {
            _logger.LogWarning("[RemoveNamedGame] Permission check failed for '{Service}' / '{GameName}'", service, gameName);
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
        // EntityKind + EpicAppId let the REST recovery endpoints (/api/cache/removals/active,
        // /api/games/removals/active) project scope-aware identity onto GameRemovalInfo.
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
                    Context: RemovalContext(displayName, appId, epicAppId)),
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
    /// GET /api/games/{appId}/removal-status - Get status of game removal operation
    /// Used for restoring progress on page refresh
    /// </summary>
    [HttpGet("{appId}/removal-status")]
    public IActionResult GetGameRemovalStatus(long appId)
    {
        var operation = _operationTracker.GetOperationByEntityKey(OperationType.GameRemoval, appId.ToString());
        if (operation == null)
        {
            return Ok(new RemovalStatusResponse { IsProcessing = false });
        }

        var metrics = operation.Metadata as RemovalMetrics;
        return Ok(new RemovalStatusResponse
        {
            IsProcessing = operation.Status != OperationStatus.Completed && operation.Status != OperationStatus.Failed,
            Status = operation.Status,
            Message = operation.Message,
            GameName = metrics?.EntityName,
            FilesDeleted = metrics?.FilesDeleted ?? 0,
            BytesFreed = metrics?.BytesFreed ?? 0,
            StartedAt = operation.StartedAt,
            OperationId = operation.Id
        });
    }

    /// <summary>
    /// GET /api/games/removals/active - Get all active game removal operations
    /// Used for universal recovery on page refresh
    /// </summary>
    [HttpGet("removals/active")]
    public IActionResult GetActiveGameRemovals()
    {
        var operations = _operationTracker.GetActiveOperations(OperationType.GameRemoval);
        return Ok(new ActiveGameRemovalsResponse
        {
            IsProcessing = operations.Any(),
            Operations = operations.Select(op =>
            {
                var metrics = op.Metadata as RemovalMetrics;
                return ToRemovalInfo(op, metrics);
            })
        });
    }

    /// <summary>
    /// Scope-aware projection from an OperationInfo + its RemovalMetrics into a GameRemovalInfo DTO.
    /// Steam entries emit `GameAppId`; Epic entries emit `EpicAppId`. Legacy rows without
    /// `EntityKind` fall back to numeric parse so existing Steam-only data keeps round-tripping.
    /// </summary>
    private static GameRemovalInfo ToRemovalInfo(OperationInfo op, RemovalMetrics? metrics)
    {
        long? gameAppId = null;
        string? epicAppId = null;

        switch (metrics?.EntityKind)
        {
            case "steam":
                if (long.TryParse(metrics.EntityKey, out var parsedSteamId))
                {
                    gameAppId = parsedSteamId;
                }
                break;
            case "epic":
                epicAppId = metrics.EpicAppId ?? metrics.EntityKey;
                break;
            case "named":
                // Named (Blizzard/Riot) games carry no Steam AppId and no Epic AppId.
                // Identity lives in EntityKey ("service:gameName") + EntityName; leave both ids null.
                break;
            default:
                // Legacy rows persisted before EntityKind existed - preserve numeric compat.
                if (long.TryParse(metrics?.EntityKey, out var legacySteamId))
                {
                    gameAppId = legacySteamId;
                }
                break;
        }

        return new GameRemovalInfo
        {
            GameAppId = gameAppId,
            EpicAppId = epicAppId,
            EntityKind = metrics?.EntityKind ?? (epicAppId != null ? "epic" : gameAppId.HasValue ? "steam" : null),
            GameName = metrics?.EntityName ?? op.Name,
            OperationId = op.Id,
            Status = op.Status,
            Message = op.Message,
            FilesDeleted = metrics?.FilesDeleted ?? 0,
            BytesFreed = metrics?.BytesFreed ?? 0,
            StartedAt = op.StartedAt
        };
    }

    /// <summary>
    /// POST /api/games/detect - Start game detection in cache
    /// Note: POST is acceptable as this starts an asynchronous operation
    /// </summary>
    [HttpPost("detect")]
    public async Task<IActionResult> DetectGamesAsync([FromQuery] bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        // forceRefresh=true means full scan (incremental=false)
        // forceRefresh=false means quick scan (incremental=true)
        var incremental = !forceRefresh;

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        // The service throws InvalidOperationException on its internal already-running race;
        // map that to null so the queue treats it as "already running" instead of faulting.
        async Task<Guid?> StartDetectionCoreAsync()
        {
            try
            {
                return await _gameCacheDetectionService.StartDetectionAsync(incremental);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

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
    /// GET /api/games/detect/active - Get currently running detection operation
    /// </summary>
    [HttpGet("detect/active")]
    public IActionResult GetActiveDetection()
    {
        var activeOperation = _gameCacheDetectionService.GetActiveOperation();

        if (activeOperation == null)
        {
            return Ok(new ActiveDetectionResponse { IsProcessing = false, Operation = null });
        }

        return Ok(new ActiveDetectionResponse { IsProcessing = true, Operation = activeOperation });
    }

    /// <summary>
    /// GET /api/games/detect/{id}/status - Get status of specific detection operation
    /// </summary>
    [HttpGet("detect/{id}/status")]
    public IActionResult GetDetectionStatus(Guid id)
    {
        var status = _gameCacheDetectionService.GetOperationStatus(id);

        if (status == null)
        {
            return NotFound(new NotFoundResponse { Error = "Detection operation not found", OperationId = id });
        }

        return Ok(status);
    }

    /// <summary>
    /// GET /api/games/detect/cached - Get cached detection results
    /// </summary>
    [AllowAnonymous]
    [HttpGet("detect/cached")]
    public async Task<IActionResult> GetCachedDetectionAsync()
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
            summaryComputedAtUtc: cachedResults.SummaryComputedAtUtc));
    }


}
