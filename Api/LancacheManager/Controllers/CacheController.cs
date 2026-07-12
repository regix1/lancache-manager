using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Hubs;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for cache management operations
/// Handles cache information, clearing, corruption detection, and service/game cache management
/// </summary>
[ApiController]
[Route("api/cache")]
[Authorize]
public class CacheController : ControllerBase
{
    // Mirrors DatasourceService's datasource-name validation: only safe service identifiers,
    // so a malicious {service} route value can't traverse paths via Path.Combine downstream.
    private static readonly Regex _serviceNameRegex = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    private readonly CacheManagementService _cacheService;
    private readonly CacheClearingService _cacheClearingService;
    private readonly ILogger<CacheController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly ISignalRNotificationService _notifications;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly NginxLogRotationService _nginxLogRotationService;
    private readonly CorruptionDetectionService _corruptionDetectionService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly DatasourceService _datasourceService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly CacheReconciliationService _reconciliationService;
    private readonly IOperationConflictChecker _conflictChecker;
    private readonly IOperationQueue _operationQueue;
    private readonly GameCacheDetectionService? _gameCacheDetectionService;

    public CacheController(
        CacheManagementService cacheService,
        CacheClearingService cacheClearingService,
        CorruptionDetectionService corruptionDetectionService,
        ILogger<CacheController> logger,
        IPathResolver pathResolver,
        ISignalRNotificationService notifications,
        RustProcessHelper rustProcessHelper,
        NginxLogRotationService nginxLogRotationService,
        IUnifiedOperationTracker operationTracker,
        DatasourceService datasourceService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        CacheReconciliationService reconciliationService,
        IOperationConflictChecker conflictChecker,
        IOperationQueue operationQueue,
        GameCacheDetectionService? gameCacheDetectionService = null)
    {
        _cacheService = cacheService;
        _cacheClearingService = cacheClearingService;
        _corruptionDetectionService = corruptionDetectionService;
        _logger = logger;
        _pathResolver = pathResolver;
        _notifications = notifications;
        _rustProcessHelper = rustProcessHelper;
        _nginxLogRotationService = nginxLogRotationService;
        _operationTracker = operationTracker;
        _datasourceService = datasourceService;
        _dbContextFactory = dbContextFactory;
        _reconciliationService = reconciliationService;
        _conflictChecker = conflictChecker;
        _operationQueue = operationQueue;
        _gameCacheDetectionService = gameCacheDetectionService;
    }

    /// <summary>
    /// GET /api/cache - Get cache information (size, path, etc.)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetCacheInfoAsync()
    {
        var info = await _cacheService.GetCacheInfoAsync();
        return Ok(info);
    }

    /// <summary>
    /// GET /api/cache/size - Read the cached size or start an asynchronous queued rescan when force=true
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("size")]
    [Authorize]
    public async Task<IActionResult> GetCacheSizeAsync(
        [FromQuery] string? datasource = null,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (force && string.IsNullOrEmpty(datasource))
        {
            // An explicit full rescan is a heavy operation. Always enter through the queue gate,
            // even when no conflict is visible yet: the gate closes the check/start race, starts
            // immediately when eligible, and parks/deduplicates otherwise. The singleton service
            // owns the promoted worker, so it outlives this HTTP request.
            Task<Guid?> StartCacheSizeScanAsync() => _cacheService.StartCacheSizeScanInBackgroundAsync();

            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.CacheSizeScan,
                ConflictScope.Bulk(),
                "Cache File Scan",
                StartCacheSizeScanAsync,
                cancellationToken));
        }

        var result = await _cacheService.GetCacheSizeAsync(force, datasource, cancellationToken);
        if (result == null)
        {
            // Graceful outcomes only apply to the cached "all datasources" scan - a
            // per-datasource scan has no cache/scan-tracker seam to fall back to (comment
            // on GetCacheSizeAsync: per-datasource scans are always live, never cached).
            if (string.IsNullOrEmpty(datasource))
            {
                var activeScan = _operationTracker.GetActiveOperations(OperationType.CacheSizeScan).FirstOrDefault();
                var staleResult = activeScan == null ? await _cacheService.GetStaleCachedSizeResultAsync() : null;
                var outcome = CacheSizeNullOutcome.Resolve(activeScan?.Id, staleResult);

                if (outcome.Kind == CacheSizeNullOutcomeKind.Scanning)
                {
                    return Accepted(new CacheSizeScanningResponse { Scanning = true, OperationId = outcome.ScanOperationId });
                }

                if (outcome.Kind == CacheSizeNullOutcomeKind.Stale)
                {
                    return Ok(outcome.StaleResult);
                }

                // No persisted result is a normal initial/invalidated state. Ordinary reads
                // never launch the heavy scan; the schedule or explicit Refresh will populate it.
                return Ok(new CacheSizeUnavailableResponse());
            }

            return StatusCode(500, new ErrorResponse { Error = "Failed to calculate cache size" });
        }

        return Ok(result);
    }

    /// <summary>
    /// GET /api/cache/size/scan/status - recovery endpoint for the cache file scan
    /// notification card (page-refresh recovery polls this, mirroring
    /// GET /api/stats/eviction/scan/status for the eviction scan).
    /// </summary>
    [HttpGet("size/scan/status")]
    public IActionResult GetCacheSizeScanStatus()
    {
        var activeScan = _operationTracker.GetActiveOperations(OperationType.CacheSizeScan).FirstOrDefault();
        if (activeScan == null)
        {
            return Ok(new
            {
                isProcessing = false,
                status = OperationStatus.Completed,
                percentComplete = 0.0,
                message = string.Empty,
                stageKey = (string?)null,
                context = (object?)null,
                operationId = (string?)null
            });
        }

        // UpdateProgress stores the current progress stage key in Message (see
        // RelayCacheSizeScanProgressAsync), so it doubles as the i18n stageKey the frontend
        // recovery card interpolates. The tracker's OperationInfo carries no context dictionary,
        // so CacheManagementService exposes the latest progress context for placeholder-bearing
        // keys like signalr.cacheSizeScan.scanning.
        var stageKey = string.IsNullOrWhiteSpace(activeScan.Message) ? null : activeScan.Message;

        return Ok(new
        {
            isProcessing = true,
            status = activeScan.Status,
            percentComplete = activeScan.PercentComplete,
            message = stageKey ?? "Scanning cache files...",
            stageKey,
            context = _cacheService.CurrentCacheSizeScanProgressContext,
            operationId = activeScan.Id
        });
    }

    /// <summary>
    /// GET /api/cache/permissions - Check cache directory permissions
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("permissions")]
    public IActionResult GetDirectoryPermissions()
    {
        var cachePath = _pathResolver.GetCacheDirectory();
        var cacheWritable = _pathResolver.IsCacheWritable();

        return Ok(new DirectoryPermission
        {
            Path = cachePath,
            Writable = cacheWritable,
            ReadOnly = !cacheWritable
        });
    }

    /// <summary>
    /// DELETE /api/cache - Clear all cache (all datasources)
    /// RESTful: DELETE is proper method for clearing/removing resources
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete]
    public async Task<IActionResult> ClearAllCacheAsync(CancellationToken cancellationToken)
    {
        // Use cached permission flags (refreshed by DirectoryPermissionMonitor).
        var defaultDatasource = _datasourceService.GetDefaultDatasource();
        var cacheWritable = defaultDatasource?.CacheWritable ?? _pathResolver.IsCacheWritable();

        if (!cacheWritable)
        {
            var errorMessage = "Cannot clear cache: cache directory is read-only. " +
                "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
                $"The lancache container is configured to run as UID/GID {ContainerEnvironment.UidGid} (configured via PUID/PGID environment variables).";

            _logger.LogWarning("[ClearAllCache] Permission check failed: {Error}", errorMessage);
            return BadRequest(new ErrorResponse { Error = errorMessage });
        }

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        async Task<Guid?> StartClearAllAsync()
        {
            _cacheService.InvalidateCachedScan();
            return await _cacheClearingService.StartCacheClearAsync();
        }

        var conflict = await _conflictChecker.CheckAsync(
            OperationType.CacheClearing,
            ConflictScope.Bulk(),
            cancellationToken);
        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.CacheClearing, ConflictScope.Bulk(), "Cache Clear (All)",
                StartClearAllAsync, cancellationToken));
        }

        var operationId = await StartClearAllAsync();

        if (operationId == null)
        {
            // Race: clearing began between our check and StartCacheClearAsync - park it.
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.CacheClearing, ConflictScope.Bulk(), "Cache Clear (All)",
                StartClearAllAsync, cancellationToken));
        }

        _logger.LogInformation("Started cache clear operation for all datasources: {OperationId}", operationId);

        return Accepted(new CacheOperationResponse
        {
            Message = "Cache clearing started in background for all datasources",
            OperationId = operationId,
            Status = OperationStatus.Running
        });
    }

    /// <summary>
    /// DELETE /api/cache/datasources/{name} - Clear cache for a specific datasource
    /// RESTful: DELETE is proper method for clearing/removing resources
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("datasources/{name}")]
    public async Task<IActionResult> ClearDatasourceCacheAsync(string name, CancellationToken cancellationToken)
    {
        var datasource = _datasourceService.GetDatasources()
            .FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (datasource == null)
        {
            return NotFound(new NotFoundResponse { Error = $"Datasource '{name}' not found" });
        }

        // Use cached permission flags (refreshed by DirectoryPermissionMonitor).
        if (!datasource.CacheWritable)
        {
            var errorMessage = $"Cannot clear cache for datasource '{name}': cache directory is read-only. " +
                "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
                $"The lancache container is configured to run as UID/GID {ContainerEnvironment.UidGid} (configured via PUID/PGID environment variables).";

            _logger.LogWarning("[ClearDatasourceCache] Permission check failed for {Datasource}: {Error}", name, errorMessage);
            return BadRequest(new ErrorResponse { Error = errorMessage });
        }

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        async Task<Guid?> StartClearDatasourceAsync()
        {
            _cacheService.InvalidateCachedScan();
            return await _cacheClearingService.StartCacheClearAsync(name);
        }

        var conflict = await _conflictChecker.CheckAsync(
            OperationType.CacheClearing,
            ConflictScope.Bulk(),
            cancellationToken);
        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.CacheClearing, ConflictScope.Bulk(), $"Cache Clear ({name})",
                StartClearDatasourceAsync, cancellationToken));
        }

        var operationId = await StartClearDatasourceAsync();

        if (operationId == null)
        {
            // Race: clearing began between our check and StartCacheClearAsync - park it.
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.CacheClearing, ConflictScope.Bulk(), $"Cache Clear ({name})",
                StartClearDatasourceAsync, cancellationToken));
        }

        _logger.LogInformation("Started cache clear operation for datasource {Datasource}: {OperationId}", name, operationId);

        return Accepted(new CacheOperationResponse
        {
            Message = $"Cache clearing started for datasource: {name}",
            OperationId = operationId,
            Status = OperationStatus.Running
        });
    }

    /// <summary>
    /// GET /api/cache/operations - List all active cache operations
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("operations")]
    public IActionResult GetActiveOperations()
    {
        var operations = _cacheClearingService.GetActiveOperations();
        var isProcessing = operations.Any(op =>
            op.Status != OperationStatus.Completed
            && op.Status != OperationStatus.Failed
            && op.Status != OperationStatus.Cancelled);
        return Ok(new ActiveOperationsResponse { IsProcessing = isProcessing, Operations = operations });
    }

    /// <summary>
    /// GET /api/cache/operations/{id}/status - Get status of specific cache clear operation
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("operations/{id}/status")]
    public IActionResult GetCacheClearStatus(Guid id)
    {
        var status = _cacheClearingService.GetCacheClearStatus(id);

        if (status == null)
        {
            return NotFound(new NotFoundResponse { Error = "Cache clear operation not found", OperationId = id });
        }

        return Ok(status);
    }

    /// <summary>
    /// GET /api/cache/corruption/cached - Get cached corruption detection results
    /// Returns immediately with cached results (if available) without running a new scan.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("corruption/cached")]
    public async Task<IActionResult> GetCachedCorruptionAsync()
    {
        var cachedResults = await _corruptionDetectionService.GetDetectionAsync();

        if (cachedResults == null)
        {
            return Ok(new CachedCorruptionResponse { HasCachedResults = false });
        }

        return Ok(BuildCachedCorruptionResponse(cachedResults));
    }

    private static CachedCorruptionResponse BuildCachedCorruptionResponse(
        CachedCorruptionResult cachedResults)
    {
        var lastDetectionTimeUtc = cachedResults.LastDetectionTime.AsUtc();

        return new CachedCorruptionResponse
        {
            HasCachedResults = true,
            ScanId = cachedResults.ScanId,
            DetectionMode = cachedResults.DetectionMode,
            Threshold = cachedResults.Threshold,
            LookbackDays = cachedResults.LookbackDays,
            ContractVersion = cachedResults.ContractVersion,
            CorruptionCounts = cachedResults.CorruptionCounts,
            RemovableServiceCounts = cachedResults.RemovableServiceCounts,
            ReviewOnlyServiceCounts = cachedResults.ReviewOnlyServiceCounts,
            TotalServicesWithCorruption = cachedResults.TotalServicesWithCorruption,
            TotalCorruptedChunks = cachedResults.TotalCorruptedChunks,
            RemovableTotal = cachedResults.RemovableTotal,
            ReviewOnlyTotal = cachedResults.ReviewOnlyTotal,
            LastDetectionTime = lastDetectionTimeUtc.ToString("o"),
            RemovalAllowed = cachedResults.RemovalAllowed,
            ServiceRemovalAllowed = cachedResults.ServiceRemovalAllowed
        };
    }

    /// <summary>
    /// POST /api/cache/corruption/detect - Start a background corruption detection scan
    /// Returns immediately with an operation ID. Results sent via SignalR when complete.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpPost("corruption/detect")]
    public async Task<IActionResult> StartCorruptionDetectionAsync(
        [FromQuery] int threshold = 3,
        [FromQuery] string detectionMode = "cache_and_logs",
        [FromQuery] int lookbackDays = CorruptionDetectionService.DefaultLookbackDays,
        CancellationToken cancellationToken = default)
    {
        var mode = CorruptionDetectionService.ValidateScanInput(
            detectionMode,
            threshold,
            lookbackDays);

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        // The bulk corruption scan is a heavy data op (OperationConflictChecker section 1a), so
        // it queues behind any other active heavy operation instead of running alongside it.
        // Deliberately no request token in the delegate: it may run at queue promotion, long
        // after this HTTP request completed (the operation owns its own CTS via the tracker).
        async Task<Guid?> StartDetectionAsync() =>
            await _corruptionDetectionService.StartDetectionAsync(
                threshold,
                mode.ToWireString(),
                lookbackDays);

        var conflict = await _conflictChecker.CheckAsync(
            OperationType.CorruptionDetection,
            ConflictScope.Bulk(),
            cancellationToken);
        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.CorruptionDetection, ConflictScope.Bulk(), "Corruption Detection",
                StartDetectionAsync, cancellationToken));
        }

        var operationId = await StartDetectionAsync();
        return Accepted(new { operationId, message = "Corruption detection started", status = OperationStatus.Running });
    }

    /// <summary>
    /// GET /api/cache/corruption/detect/status - Get the status of the active corruption detection operation
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("corruption/detect/status")]
    public IActionResult GetCorruptionDetectionStatus()
    {
        var activeOp = _corruptionDetectionService.GetActiveOperation();
        if (activeOp == null)
        {
            return Ok(new { isRunning = false });
        }

        return Ok(new
        {
            isRunning = activeOp.Status == OperationStatus.Running,
            operationId = activeOp.Id,
            status = activeOp.Status,
            message = activeOp.Message,
            startTime = activeOp.StartedAt.ToString("o")
        });
    }

    /// <summary>
    /// GET /api/cache/services/{name}/corruption - Get detailed corruption info for specific service
    /// Returns the exact stored candidates from the requested completed scan.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("services/{service}/corruption")]
    public async Task<IActionResult> GetCorruptionDetailsAsync(
        string service,
        CancellationToken cancellationToken,
        [FromQuery] Guid scanId)
    {
        if (!_serviceNameRegex.IsMatch(service))
        {
            throw new ValidationException("Invalid service name");
        }

        if (scanId == Guid.Empty)
        {
            throw new ValidationException("A corruption scan ID is required");
        }

        var details = await _corruptionDetectionService.GetDetailsAsync(scanId, service, cancellationToken);
        return Ok(details);
    }

    /// <summary>
    /// DELETE /api/cache/services/{service}/corruption/review-findings - Dismiss saved
    /// review-only findings for one service without changing source cache or log data.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("services/{service}/corruption/review-findings")]
    public async Task<IActionResult> DismissServiceCorruptionReviewFindingsAsync(
        string service,
        CancellationToken cancellationToken,
        [FromQuery] Guid scanId)
    {
        if (!_serviceNameRegex.IsMatch(service))
        {
            throw new ValidationException("Invalid service name");
        }

        return await DismissCorruptionReviewFindingsAsync(scanId, service, cancellationToken);
    }

    /// <summary>
    /// DELETE /api/cache/corruption/review-findings - Dismiss every saved review-only
    /// finding without changing source cache or log data.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("corruption/review-findings")]
    public Task<IActionResult> DismissAllCorruptionReviewFindingsAsync(
        CancellationToken cancellationToken,
        [FromQuery] Guid scanId) =>
        DismissCorruptionReviewFindingsAsync(
            scanId,
            service: null,
            cancellationToken: cancellationToken);

    private async Task<IActionResult> DismissCorruptionReviewFindingsAsync(
        Guid scanId,
        string? service,
        CancellationToken cancellationToken)
    {
        if (scanId == Guid.Empty)
        {
            throw new ValidationException("A corruption scan ID is required");
        }

        var dismissal = await _corruptionDetectionService.DismissReviewOnlyFindingsAsync(
            scanId,
            service,
            cancellationToken);
        return Ok(new DismissCorruptionReviewResponse
        {
            DismissedCount = dismissal.DismissedCount,
            Result = BuildCachedCorruptionResponse(dismissal.Detection)
        });
    }

    /// <summary>
    /// DELETE /api/cache/evicted/services/{service}/historical-evidence - Permanently purge the
    /// current scan's exact review-only log/database observations for one service. Cache files are
    /// never passed to or modified by this operation.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("evicted/services/{service}/historical-evidence")]
    public async Task<IActionResult> PurgeServiceHistoricalEvidenceAsync(
        string service,
        CancellationToken cancellationToken,
        [FromQuery] Guid scanId)
    {
        if (!_serviceNameRegex.IsMatch(service))
        {
            throw new ValidationException("Invalid service name");
        }

        return await PurgeHistoricalEvidenceAsync(scanId, service, cancellationToken);
    }

    /// <summary>
    /// DELETE /api/cache/evicted/historical-evidence - Permanently purge every current review-only
    /// exact log/database observation. This is intentionally distinct from cache removal.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("evicted/historical-evidence")]
    public Task<IActionResult> PurgeAllHistoricalEvidenceAsync(
        CancellationToken cancellationToken,
        [FromQuery] Guid scanId) =>
        PurgeHistoricalEvidenceAsync(scanId, service: null, cancellationToken);

    private async Task<IActionResult> PurgeHistoricalEvidenceAsync(
        Guid scanId,
        string? service,
        CancellationToken cancellationToken)
    {
        if (scanId == Guid.Empty)
        {
            throw new ValidationException("A corruption scan ID is required");
        }

        var preview = await _corruptionDetectionService.GetHistoricalEvidencePurgeSelectionAsync(
            scanId,
            service,
            cancellationToken);
        var previewDatasources = ResolveDatasources(
            preview.CandidatesByDatasource.Keys,
            _datasourceService.GetDatasources());
        var permissionError = CheckHistoricalEvidenceLogsWritable(previewDatasources);
        if (permissionError != null)
        {
            throw new ValidationException(permissionError);
        }

        async Task<Guid?> StartHistoricalEvidencePurgeAsync()
        {
            // Queue promotion must re-resolve the current persisted scope. This rejects a stale
            // scan and never carries client-provided candidate/path/observation identity forward.
            var selection = await _corruptionDetectionService.GetHistoricalEvidencePurgeSelectionAsync(
                scanId,
                service);
            var datasources = ResolveDatasources(
                selection.CandidatesByDatasource.Keys,
                _datasourceService.GetDatasources());
            var currentPermissionError = CheckHistoricalEvidenceLogsWritable(datasources);
            if (currentPermissionError != null)
            {
                throw new ValidationException(currentPermissionError);
            }

            return await StartHistoricalEvidencePurgeOperationAsync(selection, datasources);
        }

        // Always enter through the queue gate. Keep the logical service key only so a different
        // service request queues instead of being deduplicated as the same request; the conflict
        // checker deliberately treats every HistoricalEvidencePurge as a physical bulk log lock.
        var queueScope = service == null ? ConflictScope.Bulk() : ConflictScope.Service(service);
        var queued = await _operationQueue.EnqueueAsync(
            OperationType.HistoricalEvidencePurge,
            queueScope,
            service == null
                ? "Purge historical evidence (all services)"
                : $"Purge historical evidence ({service})",
            StartHistoricalEvidencePurgeAsync,
            cancellationToken);

        return Accepted(new HistoricalEvidencePurgeAcceptedResponse
        {
            OperationId = queued.OperationId,
            Scope = preview.Scope,
            CandidateCount = preview.CandidateIds.Count,
            Queued = queued.Queued,
            AlreadyRunning = queued.AlreadyRunning,
            Status = queued.Status
        });
    }

    /// <summary>
    /// DELETE /api/cache/services/{name}/corruption - Remove corrupted chunks for specific service
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("services/{service}/corruption")]
    public async Task<IActionResult> RemoveCorruptedChunksAsync(
        string service,
        CancellationToken cancellationToken,
        [FromQuery] Guid scanId,
        [FromQuery] string? candidateIds = null)
    {
        if (!_serviceNameRegex.IsMatch(service))
        {
            throw new ValidationException("Invalid service name");
        }

        if (scanId == Guid.Empty)
        {
            throw new ValidationException("A corruption scan ID is required");
        }

        var requestedCandidateIds = ParseCsvValues(candidateIds, StringComparer.Ordinal);
        var previewSelection = await _corruptionDetectionService.GetRemovalSelectionAsync(
            scanId,
            service,
            requestedCandidateIds,
            cancellationToken);
        var datasources = ResolveDatasourcesForSelection(
            previewSelection,
            _datasourceService.GetDatasources());

        // CRITICAL: Check write permissions BEFORE starting (or queueing) the operation
        // This prevents the DB/filesystem state mismatch when PUID/PGID is wrong
        var permissionError = CheckDatasourcesWritable(datasources, service);
        if (permissionError != null)
        {
            throw new ValidationException(permissionError);
        }

        // The whole start path lives in this local function so the wait-queue can run it
        // verbatim at promotion time (captures only singleton services + this controller).
        async Task<Guid?> StartCorruptionRemovalAsync()
        {
            // A queued request may be promoted after a new scan replaces the preview.
            // Resolve the exact scope again at start time and fail closed if it is stale.
            var selection = await _corruptionDetectionService.GetRemovalSelectionAsync(
                scanId,
                service,
                requestedCandidateIds);
            var currentDatasources = ResolveDatasourcesForSelection(
                selection,
                _datasourceService.GetDatasources());
            var currentPermissionError = CheckDatasourcesWritable(currentDatasources, service);
            if (currentPermissionError != null)
            {
                throw new ValidationException(currentPermissionError);
            }

            _cacheService.InvalidateCachedScan();
            var operationIdReady = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(async () =>
            {
                try
                {
                    await LiveLogMonitorService.PauseAsync();
                    _logger.LogInformation("Paused LiveLogMonitorService for corruption removal");
                    try
                    {
                        await RunCorruptionRemovalCoreAsync(
                            selection,
                            currentDatasources,
                            onRegistered: id => operationIdReady.TrySetResult(id));
                    }
                    catch (OperationCanceledException)
                    {
                        // The core already completed the operation as cancelled.
                    }
                }
                finally
                {
                    await LiveLogMonitorService.ResumeAsync();
                    _logger.LogInformation("Resumed LiveLogMonitorService after corruption removal");
                    operationIdReady.TrySetResult(Guid.Empty);
                }
            });

            var startedId = await operationIdReady.Task;
            return startedId == Guid.Empty ? null : startedId;
        }

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        var conflict = await _conflictChecker.CheckAsync(
            OperationType.CorruptionRemoval,
            ConflictScope.Service(service),
            cancellationToken);
        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.CorruptionRemoval, ConflictScope.Service(service),
                $"Corruption Removal ({service})", StartCorruptionRemovalAsync, cancellationToken));
        }

        var operationId = await StartCorruptionRemovalAsync();
        if (operationId == null)
        {
            // Race: the core refused to start - park it.
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.CorruptionRemoval, ConflictScope.Service(service),
                $"Corruption Removal ({service})", StartCorruptionRemovalAsync, cancellationToken));
        }

        return Accepted(new CacheOperationResponse
        {
            Message = $"Started corruption removal for service: {service}",
            Service = service,
            OperationId = operationId,
            Status = OperationStatus.Running
        });
    }

    /// <summary>
    /// DELETE /api/cache/corruption - Remove corrupted chunks for ALL services at once.
    /// Queries the cached corruption detection results and processes each service sequentially.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("corruption")]
    public async Task<IActionResult> RemoveAllCorruptedChunksAsync(
        CancellationToken cancellationToken,
        [FromQuery] Guid scanId,
        [FromQuery] string? services = null)
    {
        if (scanId == Guid.Empty)
        {
            throw new ValidationException("A corruption scan ID is required");
        }

        var cachedDetection = await _corruptionDetectionService.GetDetectionAsync(cancellationToken);
        if (cachedDetection == null)
        {
            throw new NotFoundException("Corruption scan");
        }

        if (cachedDetection.ScanId != scanId)
        {
            throw new ConflictException("The corruption scan is stale. Reload results and try again");
        }

        if (cachedDetection.CorruptionCounts.Count == 0)
        {
            return Ok(new { Message = "No corruption data found. Run a corruption detection scan first." });
        }

        var availableServices = new HashSet<string>(
            cachedDetection.CorruptionCounts.Keys,
            StringComparer.OrdinalIgnoreCase);
        var requestedServices = ParseCsvValues(services, StringComparer.OrdinalIgnoreCase);
        List<string> servicesToRemove;
        if (requestedServices is { Count: > 0 })
        {
            var unknown = requestedServices.Where(service => !availableServices.Contains(service)).ToList();
            if (unknown.Count > 0)
            {
                throw new ValidationException(
                    "One or more requested services are not part of the stored corruption scan");
            }

            servicesToRemove = cachedDetection.RemovableServiceCounts.Keys
                .Where(requestedServices.Contains)
                .ToList();
        }
        else
        {
            servicesToRemove = cachedDetection.RemovableServiceCounts.Keys.ToList();
        }

        if (servicesToRemove.Count == 0)
        {
            throw new ForbiddenException("This stored corruption scan has no removable candidates");
        }

        var previewSelections = new List<CorruptionRemovalSelection>();
        foreach (var service in servicesToRemove)
        {
            previewSelections.Add(await _corruptionDetectionService.GetRemovalSelectionAsync(
                scanId,
                service,
                cancellationToken: cancellationToken));
        }

        var datasources = ResolveDatasourcesForSelections(
            previewSelections,
            _datasourceService.GetDatasources());

        // CRITICAL: Check write permissions BEFORE starting (or queueing) the operation
        var permissionError = CheckDatasourcesWritable(datasources, service: null);
        if (permissionError != null)
        {
            throw new ValidationException(permissionError);
        }

        // The whole start path lives in this local function so the wait-queue can run it
        // verbatim at promotion time. Returns a synthetic handle (the per-service cores
        // register their own per-service operations once running).
        async Task<Guid?> StartAllCorruptionRemovalAsync()
        {
            var selections = new List<CorruptionRemovalSelection>();
            foreach (var service in servicesToRemove)
            {
                selections.Add(await _corruptionDetectionService.GetRemovalSelectionAsync(scanId, service));
            }

            var currentDatasources = ResolveDatasourcesForSelections(
                selections,
                _datasourceService.GetDatasources());
            var currentPermissionError = CheckDatasourcesWritable(currentDatasources, service: null);
            if (currentPermissionError != null)
            {
                throw new ValidationException(currentPermissionError);
            }

            _logger.LogInformation(
                "[CorruptionRemoval] Starting scan-bound removal for {Count} service(s): {Services}",
                selections.Count,
                string.Join(", ", selections.Select(selection => selection.Service)));
            _cacheService.InvalidateCachedScan();

            _ = Task.Run(async () =>
            {
                try
                {
                    await LiveLogMonitorService.PauseAsync();
                    _logger.LogInformation("Paused LiveLogMonitorService for all-services corruption removal");
                    var bulkState = new BulkCorruptionRemovalState { ServiceCount = selections.Count };
                    var cancelled = false;

                    for (var serviceIndex = 0; serviceIndex < selections.Count; serviceIndex++)
                    {
                        var selection = selections[serviceIndex];
                        bulkState.ServiceIndex = serviceIndex + 1;
                        var serviceDatasources = ResolveDatasourcesForSelection(selection, currentDatasources);
                        try
                        {
                            await RunCorruptionRemovalCoreAsync(selection, serviceDatasources, bulk: bulkState);
                        }
                        catch (OperationCanceledException)
                        {
                            cancelled = true;
                            break;
                        }
                    }

                    var processedCount = bulkState.SucceededServices + bulkState.FailedServices;
                    if (cancelled)
                    {
                        await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                            new CorruptionRemovalComplete(false, "all",
                                StageKey: "signalr.corruptionRemove.allCancelled",
                                OperationId: bulkState.LastOperationId,
                                Context: new Dictionary<string, object?>
                                {
                                    ["completedCount"] = processedCount,
                                    ["serviceCount"] = bulkState.ServiceCount
                                }));
                    }
                    else if (bulkState.FailedServices > 0)
                    {
                        var context = bulkState.Totals.ToContext("all");
                        context["failedCount"] = bulkState.FailedServices;
                        context["serviceCount"] = bulkState.ServiceCount;
                        await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                            new CorruptionRemovalComplete(false, "all",
                                StageKey: "signalr.corruptionRemove.allCompleteWithFailures",
                                OperationId: bulkState.LastOperationId,
                                Context: context));
                    }
                    else
                    {
                        var context = bulkState.Totals.ToContext("all");
                        context["serviceCount"] = bulkState.ServiceCount;
                        await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                            new CorruptionRemovalComplete(true, "all",
                                StageKey: "signalr.corruptionRemove.allComplete",
                                OperationId: bulkState.LastOperationId,
                                Context: context));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error during all-services corruption removal");
                }
                finally
                {
                    await LiveLogMonitorService.ResumeAsync();
                    _logger.LogInformation("Resumed LiveLogMonitorService after all-services corruption removal");
                }
            });

            return Guid.NewGuid();
        }

        // Wait-queue model: if any service is blocked, park the whole all-services run behind
        // the FIRST conflicting service's scope (promotion re-checks that scope; see report -
        // other services' conflicts are handled per-service by the cores once running).
        foreach (var svc in servicesToRemove)
        {
            var conflict = await _conflictChecker.CheckAsync(
                OperationType.CorruptionRemoval,
                ConflictScope.Service(svc),
                cancellationToken);
            if (conflict != null)
            {
                _logger.LogInformation("[CorruptionRemoval] All-services removal queued: service '{Service}' conflicts with active {ActiveType}",
                    svc, conflict.ActiveOperationType);
                return Accepted(await _operationQueue.EnqueueAsync(
                    OperationType.CorruptionRemoval, ConflictScope.Service(svc),
                    "Corruption Removal (all services)", StartAllCorruptionRemovalAsync, cancellationToken));
            }
        }

        await StartAllCorruptionRemovalAsync();

        return Accepted(new { Message = "Corruption removal started for all services" });
    }

    private static HashSet<string>? ParseCsvValues(
        string? value,
        IEqualityComparer<string> comparer)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var values = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(comparer);
        return values.Count == 0 ? null : values;
    }

    private static List<ResolvedDatasource> ResolveDatasourcesForSelection(
        CorruptionRemovalSelection selection,
        IReadOnlyList<ResolvedDatasource> configuredDatasources) =>
        ResolveDatasources(selection.CandidatesByDatasource.Keys, configuredDatasources);

    private static List<ResolvedDatasource> ResolveDatasourcesForSelections(
        IEnumerable<CorruptionRemovalSelection> selections,
        IReadOnlyList<ResolvedDatasource> configuredDatasources) =>
        ResolveDatasources(
            selections.SelectMany(selection => selection.CandidatesByDatasource.Keys),
            configuredDatasources);

    private static List<ResolvedDatasource> ResolveDatasources(
        IEnumerable<string> datasourceNames,
        IReadOnlyList<ResolvedDatasource> configuredDatasources)
    {
        var configuredByName = configuredDatasources.ToDictionary(
            datasource => datasource.Name,
            StringComparer.OrdinalIgnoreCase);
        var requestedNames = datasourceNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missing = requestedNames.Where(name => !configuredByName.ContainsKey(name)).ToList();
        if (missing.Count > 0)
        {
            throw new ConflictException(
                "One or more corruption candidates belong to a datasource that is no longer configured");
        }

        return requestedNames.Select(name => configuredByName[name]).ToList();
    }

    /// <summary>
    /// Verifies every datasource's cache and log directories are writable before a corruption
    /// removal starts. Returns a user-facing error string when a directory is read-only, or
    /// null when all datasources are writable. <paramref name="service"/> is included in the
    /// log line for the single-service path; pass null for the all-services path.
    /// </summary>
    private string? CheckDatasourcesWritable(IReadOnlyList<ResolvedDatasource> datasources, string? service)
    {
        foreach (ResolvedDatasource datasource in datasources)
        {
            var cacheWritable = datasource.CacheWritable;
            var logsWritable = datasource.LogsWritable;

            if (!cacheWritable || !logsWritable)
            {
                var errors = new List<string>();
                if (!cacheWritable) errors.Add($"cache directory is read-only ({datasource.CachePath})");
                if (!logsWritable) errors.Add($"logs directory is read-only ({datasource.LogPath})");

                var errorMessage = $"Cannot remove corrupted chunks for datasource '{datasource.Name}': {string.Join(" and ", errors)}. " +
                    "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
                    $"The lancache container is configured to run as UID/GID {ContainerEnvironment.UidGid} (configured via PUID/PGID environment variables).";

                if (service != null)
                {
                    _logger.LogWarning("[CorruptionRemoval] Permission check failed for service {Service} on datasource {Datasource}: {Error}",
                        service, datasource.Name, errorMessage);
                }
                else
                {
                    _logger.LogWarning("[CorruptionRemoval] Permission check failed on datasource {Datasource}: {Error}",
                        datasource.Name, errorMessage);
                }

                return errorMessage;
            }
        }

        return null;
    }

    /// <summary>
    /// Historical evidence purge needs writable logs only. Cache writability is deliberately
    /// excluded because no cache path is supplied to the Rust command.
    /// </summary>
    private string? CheckHistoricalEvidenceLogsWritable(IReadOnlyList<ResolvedDatasource> datasources)
    {
        foreach (var datasource in datasources)
        {
            if (datasource.LogsWritable)
            {
                continue;
            }

            var message = $"Cannot purge historical evidence for datasource '{datasource.Name}': "
                + $"logs directory is read-only ({datasource.LogPath}). "
                + "Check the PUID/PGID permissions for the lancache logs directory.";
            _logger.LogWarning(
                "[HistoricalEvidencePurge] Permission check failed on datasource {Datasource}: {Error}",
                datasource.Name,
                message);
            return message;
        }

        return null;
    }

    private sealed class HistoricalEvidencePurgeReport
    {
        public required int CandidateCount { get; init; }
        public long LogLinesRemoved { get; set; }
        public long LogEntriesRemoved { get; set; }
        public long DownloadsDeleted { get; set; }

        public Dictionary<string, object?> ToContext(string scope) => new()
        {
            ["scope"] = scope,
            ["count"] = CandidateCount,
            ["logLines"] = LogLinesRemoved,
            ["logEntries"] = LogEntriesRemoved,
            ["downloads"] = DownloadsDeleted,
            ["files"] = 0,
            ["bytesFreed"] = 0
        };
    }

    private async Task<Guid> StartHistoricalEvidencePurgeOperationAsync(
        HistoricalEvidencePurgeSelection selection,
        IReadOnlyList<ResolvedDatasource> datasources)
    {
        var scope = selection.Scope;
        var candidateCount = selection.CandidateIds.Count;
        var cts = new CancellationTokenSource();
        HistoricalEvidencePurgeReport? capturedReport = null;
        Exception? capturedException = null;
        Guid operationId = Guid.Empty;
        operationId = _operationTracker.RegisterOperation(
            OperationType.HistoricalEvidencePurge,
            scope == "all"
                ? "Purge historical evidence (all services)"
                : $"Purge historical evidence ({scope})",
            cts,
            new HistoricalEvidencePurgeMetadata
            {
                ScanId = selection.ScanId,
                Scope = scope,
                CandidateCount = candidateCount
            },
            onTerminalEmit: info =>
            {
                var terminalCancelled = info.Cancelled && !info.Success;
                var status = terminalCancelled
                    ? OperationStatus.Cancelled
                    : info.Success ? OperationStatus.Completed : OperationStatus.Failed;
                var report = capturedReport;
                return _notifications.NotifyAllAsync(
                    SignalREvents.HistoricalEvidencePurgeComplete,
                    new HistoricalEvidencePurgeComplete(
                        OperationId: operationId,
                        Success: info.Success,
                        Status: status,
                        Scope: scope,
                        StageKey: terminalCancelled
                            ? "signalr.historicalEvidencePurge.cancelled"
                            : info.Success
                                ? "signalr.historicalEvidencePurge.complete"
                                : "signalr.historicalEvidencePurge.failed",
                        Cancelled: terminalCancelled,
                        Error: info.Success ? null : capturedException?.Message ?? info.Error,
                        CandidateCount: report?.CandidateCount ?? candidateCount,
                        LogLinesRemoved: report?.LogLinesRemoved ?? 0,
                        LogEntriesRemoved: report?.LogEntriesRemoved ?? 0,
                        DownloadsDeleted: report?.DownloadsDeleted ?? 0,
                        Context: report?.ToContext(scope)));
            });

        try
        {
            await _notifications.NotifyAllAsync(
                SignalREvents.HistoricalEvidencePurgeStarted,
                new HistoricalEvidencePurgeStarted(
                    operationId,
                    scope,
                    candidateCount,
                    "signalr.historicalEvidencePurge.starting",
                    DateTime.UtcNow,
                    new Dictionary<string, object?>
                    {
                        ["scope"] = scope,
                        ["count"] = candidateCount
                    }));
        }
        catch (Exception ex)
        {
            capturedException = ex;
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
            throw;
        }

        _ = Task.Run(async () =>
        {
            HistoricalEvidencePurgeReport? report = null;
            Exception? failure = null;
            var cancelled = false;
            var paused = false;
            try
            {
                await LiveLogMonitorService.PauseAsync();
                paused = true;
                _logger.LogInformation(
                    "Paused LiveLogMonitorService for historical evidence purge {OperationId}",
                    operationId);
                report = await RunHistoricalEvidencePurgeCoreAsync(
                    operationId,
                    selection,
                    datasources,
                    cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                cancelled = true;
                _logger.LogInformation(
                    "Historical evidence purge {OperationId} was cancelled",
                    operationId);
            }
            catch (Exception ex)
            {
                failure = ex;
                _logger.LogError(
                    ex,
                    "Historical evidence purge {OperationId} failed for scope {Scope}",
                    operationId,
                    scope);
            }
            finally
            {
                if (paused)
                {
                    try
                    {
                        await LiveLogMonitorService.ResumeAsync();
                        _logger.LogInformation(
                            "Resumed LiveLogMonitorService after historical evidence purge {OperationId}",
                            operationId);
                    }
                    catch (Exception ex)
                    {
                        failure ??= ex;
                        cancelled = false;
                        _logger.LogError(
                            ex,
                            "Failed to resume LiveLogMonitorService after historical evidence purge {OperationId}",
                            operationId);
                    }
                }
            }

            if (!cancelled && failure == null)
            {
                try
                {
                    cts.Token.ThrowIfCancellationRequested();
                    // Resume and every refresh/reopen step have already succeeded. Pruning is the
                    // final fallible mutation; once it commits, the operation publishes success.
                    await _corruptionDetectionService.ApplyHistoricalEvidencePurgeSuccessAsync(
                        selection.ScanId,
                        selection.CandidateIds,
                        CancellationToken.None);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    _logger.LogError(
                        ex,
                        "Failed to prune saved historical evidence candidates for operation {OperationId}",
                        operationId);
                }
            }

            if (cancelled)
            {
                _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
                return;
            }

            if (failure != null)
            {
                capturedException = failure;
                _operationTracker.CompleteOperation(operationId, success: false, error: failure.Message);
                return;
            }

            capturedReport = report!;
            _operationTracker.CompleteOperation(operationId, success: true);
        }, CancellationToken.None);

        return operationId;
    }

    private async Task<HistoricalEvidencePurgeReport> RunHistoricalEvidencePurgeCoreAsync(
        Guid operationId,
        HistoricalEvidencePurgeSelection selection,
        IReadOnlyList<ResolvedDatasource> datasources,
        CancellationToken cancellationToken)
    {
        var scope = selection.Scope;
        var report = new HistoricalEvidencePurgeReport
        {
            CandidateCount = selection.CandidateIds.Count
        };
        var datasourceCount = datasources.Count;

        for (var datasourceIndex = 0; datasourceIndex < datasourceCount; datasourceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentIndex = datasourceIndex;
            var datasource = datasources[datasourceIndex];
            var progressFilePath = Path.Combine(
                _pathResolver.GetOperationsDirectory(),
                $"historical_evidence_purge_{operationId}_{datasource.Name}.json");
            var evidenceFilePath = Path.Combine(
                _pathResolver.GetOperationsDirectory(),
                $"historical_evidence_{operationId}_{datasource.Name}.json");

            try
            {
                var evidence = new HistoricalEvidencePurgeEvidence
                {
                    ContractVersion = selection.ContractVersion,
                    ScanId = selection.ScanId,
                    Mode = selection.Mode,
                    Threshold = selection.Threshold,
                    Datasource = datasource.Name,
                    Candidates = selection.CandidatesByDatasource[datasource.Name].ToList()
                };
                Directory.CreateDirectory(_pathResolver.GetOperationsDirectory());
                await System.IO.File.WriteAllTextAsync(
                    evidenceFilePath,
                    JsonSerializer.Serialize(evidence),
                    cancellationToken);

                var result = await _rustProcessHelper.RunHistoricalEvidencePurgeAsync(
                    datasource.LogPath,
                    scope,
                    evidenceFilePath,
                    progressFilePath,
                    cancellationToken,
                    operationId,
                    async _ =>
                    {
                        var progress = await _rustProcessHelper
                            .ReadProgressFileAsync<CorruptionRemovalProgressData>(progressFilePath);
                        if (progress == null || progress.Status == "completed")
                        {
                            return;
                        }

                        var percent = (currentIndex * 100.0 + progress.PercentComplete) / datasourceCount;
                        var context = progress.Context ?? new Dictionary<string, object?>();
                        context["scope"] = scope;
                        context["datasource"] = datasource.Name;
                        await _notifications.NotifyAllAsync(
                            SignalREvents.HistoricalEvidencePurgeProgress,
                            new HistoricalEvidencePurgeProgress(
                                operationId,
                                scope,
                                progress.Status,
                                progress.StageKey ?? "signalr.historicalEvidencePurge.processing",
                                percent,
                                ReadContextCount(context, "logLines"),
                                ReadContextCount(context, "logEntries"),
                                ReadContextCount(context, "downloads"),
                                context));
                        _operationTracker.UpdateProgress(
                            operationId,
                            percent,
                            progress.StageKey ?? "signalr.historicalEvidencePurge.processing");
                    });

                if (!result.Success)
                {
                    throw new RustProcessException(
                        "corruption_manager",
                        result.ExitCode,
                        result.Error,
                        $"historical evidence datasource '{datasource.Name}'");
                }

                var finalProgress = await _rustProcessHelper
                    .ReadProgressFileAsync<CorruptionRemovalProgressData>(progressFilePath);
                if (finalProgress is { Status: "completed", Context: not null })
                {
                    report.LogLinesRemoved += ReadContextCount(finalProgress.Context, "logLines");
                    report.LogEntriesRemoved += ReadContextCount(finalProgress.Context, "logEntries");
                    report.DownloadsDeleted += ReadContextCount(finalProgress.Context, "downloads");
                }
            }
            finally
            {
                await _rustProcessHelper.DeleteTempFileAsync(progressFilePath);
                await _rustProcessHelper.DeleteTempFileAsync(evidenceFilePath);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var reopenResult = await _nginxLogRotationService.ReopenNginxLogsAsync();
        if (!reopenResult.Success)
        {
            throw new InvalidOperationException(
                reopenResult.ErrorMessage ?? "nginx failed to reopen access logs after historical evidence purge");
        }

        await _cacheService.InvalidateServiceCountsAsync();
        _gameCacheDetectionService?.InvalidateDetectionCache();
        await _notifications.NotifyAllAsync(
            SignalREvents.DownloadsRefresh,
            new { reason = "historical-evidence-purge", scope });

        await _notifications.NotifyAllAsync(
            SignalREvents.HistoricalEvidencePurgeProgress,
            new HistoricalEvidencePurgeProgress(
                operationId,
                scope,
                "running",
                "signalr.historicalEvidencePurge.finalizing",
                100,
                report.LogLinesRemoved,
                report.LogEntriesRemoved,
                report.DownloadsDeleted,
                report.ToContext(scope)));
        _operationTracker.UpdateProgress(
            operationId,
            100,
            "signalr.historicalEvidencePurge.finalizing");

        return report;
    }

    /// <summary>
    /// Per-service outcome totals harvested from the Rust binary's final progress checkpoint
    /// (both remove flows persist {count, files, logLines, downloads, logEntries} at 100%),
    /// summed across datasources. Drives the rich terminal notification message.
    /// </summary>
    private sealed class CorruptionRemovalTotals
    {
        public long UrlsRemoved;
        public long FilesDeleted;
        public long LogLinesRemoved;
        public long DownloadsDeleted;
        public long LogEntriesDeleted;

        public bool AnythingRemoved =>
            UrlsRemoved > 0 || FilesDeleted > 0 || LogLinesRemoved > 0
            || DownloadsDeleted > 0 || LogEntriesDeleted > 0;

        public void Add(CorruptionRemovalTotals other)
        {
            UrlsRemoved += other.UrlsRemoved;
            FilesDeleted += other.FilesDeleted;
            LogLinesRemoved += other.LogLinesRemoved;
            DownloadsDeleted += other.DownloadsDeleted;
            LogEntriesDeleted += other.LogEntriesDeleted;
        }

        public Dictionary<string, object?> ToContext(string service) => new()
        {
            ["service"] = service,
            ["count"] = UrlsRemoved,
            ["files"] = FilesDeleted,
            ["logLines"] = LogLinesRemoved,
            ["downloads"] = DownloadsDeleted,
            ["logEntries"] = LogEntriesDeleted
        };
    }

    /// <summary>
    /// Shared state for an all-services corruption removal run. The per-service cores suppress
    /// their individual terminal SignalR emits and report into this instead, so the singleton
    /// notification card walks service by service as ONE running operation and completes exactly
    /// once with aggregated totals. Previously every service (and every datasource within it)
    /// raced its own completion onto the same card: mid-run "completed" checkpoints armed
    /// auto-dismiss timers, later events were dropped against the no-longer-running card, and
    /// the final state was whichever writer happened to land last.
    /// </summary>
    private sealed class BulkCorruptionRemovalState
    {
        public required int ServiceCount { get; init; }
        public int ServiceIndex; // 1-based position of the service currently running
        public int SucceededServices;
        public int FailedServices;
        public Guid LastOperationId;
        public CorruptionRemovalTotals Totals { get; } = new();
    }

    /// <summary>
    /// Reads a numeric value out of a Rust progress-checkpoint context, which deserializes
    /// as JsonElement values inside the object dictionary.
    /// </summary>
    private static long ReadContextCount(Dictionary<string, object?>? context, string key)
    {
        if (context == null || !context.TryGetValue(key, out var value) || value == null)
        {
            return 0;
        }

        return value switch
        {
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetInt64(),
            long l => l,
            int i => i,
            _ => long.TryParse(value.ToString(), out var parsed) ? parsed : 0
        };
    }

    /// <summary>
    /// Shared per-service corruption-removal core: registers a tracked operation, emits the
    /// start notification, runs the corruption-remove Rust binary across every datasource with
    /// progress monitoring, and completes the operation. Caller responsibilities (NOT done here):
    /// conflict/permission checks,
    /// LiveLogMonitorService Pause/Resume (the all-services path pauses ONCE around its loop),
    /// and the HTTP response. On cancellation the operation is completed as cancelled and an
    /// <see cref="OperationCanceledException"/> is rethrown so the all-services loop can break;
    /// non-cancellation failures complete the operation as failed and are swallowed so the
    /// all-services loop continues with the next service. <paramref name="onRegistered"/> fires
    /// synchronously with the operationId the moment the operation is registered.
    /// When <paramref name="bulk"/> is provided (all-services run) the terminal SignalR emit is
    /// suppressed - the bulk loop emits ONE aggregated completion after the last service - and
    /// every event context carries serviceIndex/serviceCount so the card shows which service is
    /// being worked and how far through the run it is. Returns true when every datasource
    /// succeeded for this service.
    /// </summary>
    private async Task<bool> RunCorruptionRemovalCoreAsync(
        CorruptionRemovalSelection selection,
        List<ResolvedDatasource> datasources,
        Action<Guid>? onRegistered = null,
        BulkCorruptionRemovalState? bulk = null)
    {
        var service = selection.Service;
        // Create CancellationTokenSource and register with unified operation tracker for cancel support
        var cts = new CancellationTokenSource();
        var metadata = new RemovalMetrics { EntityKey = service.ToLowerInvariant(), EntityName = service };
        var serviceName = service;
        var totals = new CorruptionRemovalTotals();
        Guid operationId = Guid.Empty;
        operationId = _operationTracker.RegisterOperation(
            OperationType.CorruptionRemoval,
            $"Corruption removal: {service}",
            cts,
            metadata,
            onTerminalEmit: info =>
            {
                // In an all-services run the terminal emit belongs to the bulk loop (one
                // aggregated completion after the last service). Emitting per service here is
                // what made completions race each other on the singleton notification card.
                if (bulk != null)
                {
                    return Task.CompletedTask;
                }

                if (info.Cancelled)
                {
                    return _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                        new CorruptionRemovalComplete(false, serviceName,
                            StageKey: "signalr.corruptionRemove.cancelled",
                            OperationId: operationId));
                }

                if (!info.Success)
                {
                    return _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                        new CorruptionRemovalComplete(false, serviceName,
                            StageKey: "signalr.corruptionRemove.failed.generic",
                            OperationId: operationId,
                            Error: info.Error));
                }

                // Success carries the Rust binary's real numbers (harvested from its final
                // progress checkpoint) instead of a generic "successfully removed" line.
                return totals.AnythingRemoved
                    ? _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                        new CorruptionRemovalComplete(true, serviceName,
                            StageKey: "signalr.corruptionRemove.complete",
                            OperationId: operationId,
                            Context: totals.ToContext(serviceName)))
                    : _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                        new CorruptionRemovalComplete(true, serviceName,
                            StageKey: "signalr.corruptionRemove.noChunksFoundService",
                            OperationId: operationId,
                            Context: new Dictionary<string, object?> { ["service"] = serviceName }));
            });

        onRegistered?.Invoke(operationId);

        // Send start notification via SignalR
        var startContext = new Dictionary<string, object?> { ["service"] = service };
        var startStageKey = "signalr.corruptionRemove.starting";
        if (bulk != null)
        {
            bulk.LastOperationId = operationId;
            startContext["serviceIndex"] = bulk.ServiceIndex;
            startContext["serviceCount"] = bulk.ServiceCount;
            startStageKey = "signalr.corruptionRemove.startingService";
        }

        _notifications.NotifyAllFireAndForget(SignalREvents.CorruptionRemovalStarted,
            new CorruptionRemovalStarted(
                service,
                operationId,
                startStageKey,
                DateTime.UtcNow,
                startContext));

        try
        {
            // Revalidate the scan-bound candidate IDs at operation start. This closes
            // the queue-promotion window and prevents a newly completed scan from
            // being interpreted through an older in-memory selection.
            selection = await _corruptionDetectionService.GetRemovalSelectionAsync(
                selection.ScanId,
                service,
                selection.CandidateIds,
                cts.Token);

            // Update tracking
            _operationTracker.UpdateProgress(operationId, 0, "signalr.corruptionRemove.starting");

            _logger.LogInformation("[CorruptionRemoval] Processing {Count} datasource(s) for service {Service}",
                datasources.Count, service);
            bool allSucceeded = true;
            string? lastError = null;

            var datasourceCount = datasources.Count;
            for (var datasourceIndex = 0; datasourceIndex < datasourceCount; datasourceIndex++)
            {
                cts.Token.ThrowIfCancellationRequested();

                // Copy for the async progress closure: the for variable is shared across
                // iterations and a late callback must not see the next iteration's index.
                var dsIndex = datasourceIndex;
                var datasource = datasources[datasourceIndex];

                var logsPath = datasource.LogPath;
                var cachePath = datasource.CachePath;
                var progressFilePath = Path.Combine(_pathResolver.GetOperationsDirectory(),
                    $"corruption_removal_{operationId}_{datasource.Name}.json");
                var evidenceFilePath = Path.Combine(_pathResolver.GetOperationsDirectory(),
                    $"corruption_evidence_{operationId}_{datasource.Name}.json");

                _logger.LogInformation("[CorruptionRemoval] Processing datasource '{Datasource}' (logs: {LogsPath}, cache: {CachePath})",
                    datasource.Name, logsPath, cachePath);

                try
                {
                    var evidence = new CorruptionRemovalEvidence
                    {
                        ContractVersion = selection.ContractVersion,
                        ScanId = selection.ScanId,
                        Mode = selection.Mode,
                        Threshold = selection.Threshold,
                        Datasource = datasource.Name,
                        Candidates = selection.CandidatesByDatasource[datasource.Name].ToList()
                    };
                    Directory.CreateDirectory(_pathResolver.GetOperationsDirectory());
                    await System.IO.File.WriteAllTextAsync(
                        evidenceFilePath,
                        JsonSerializer.Serialize(evidence),
                        cts.Token);

                    // Hybrid transport (mirrors CacheClearingService): the stdout progress event
                    // from corruption_manager is a zero-latency wake-up that triggers exactly one
                    // read of the (Rust-side-unchanged) progress file, replacing the previous
                    // standalone Task.Run poll-every-500ms loop.
                    var result = await _rustProcessHelper.RunCorruptionManagerAsync(
                        "remove",
                        logsPath,
                        cachePath,
                        service: service,
                        evidenceFile: evidenceFilePath,
                        progressFile: progressFilePath,
                        cancellationToken: cts.Token,
                        operationId: operationId,
                        onProgressEvent: async _ =>
                        {
                            var progress = await _rustProcessHelper.ReadProgressFileAsync<CorruptionRemovalProgressData>(progressFilePath);
                            if (progress == null)
                            {
                                return;
                            }

                            // "completed" checkpoints are DATA for the terminal emit, not live
                            // progress: relaying them fired once per datasource and per service,
                            // flipping the notification card to completed while work was still
                            // running - the reason completions never displayed reliably. The
                            // tracker's CompleteOperation / the bulk loop own the completion.
                            // "failed" checkpoints still relay: they carry the rich errorDetail
                            // (error.fatal) that the generic terminal failure message lacks.
                            if (progress.Status == "completed")
                            {
                                return;
                            }

                            // One continuous 0-100 across all datasources instead of the bar
                            // snapping back to zero when the next datasource starts.
                            var overallPercent = (dsIndex * 100.0 + progress.PercentComplete) / datasourceCount;

                            _operationTracker.UpdateProgress(operationId, overallPercent, progress.StageKey ?? "");
                            _operationTracker.UpdateMetadata(operationId, (object meta) =>
                            {
                                var m = (RemovalMetrics)meta;
                                m.FilesProcessed = progress.FilesProcessed;
                                m.TotalFiles = progress.TotalFiles;
                            });

                            // Every progress context names the service (the Rust stage contexts
                            // don't), plus the run position during an all-services removal.
                            var context = progress.Context ?? new Dictionary<string, object?>();
                            context["service"] = service;
                            if (bulk != null)
                            {
                                context["serviceIndex"] = bulk.ServiceIndex;
                                context["serviceCount"] = bulk.ServiceCount;
                            }

                            // Send progress notification via SignalR
                            await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalProgress,
                                new CorruptionRemovalProgress(
                                    service,
                                    operationId,
                                    progress.Status,
                                    progress.StageKey ?? string.Empty,
                                    DateTime.UtcNow,
                                    progress.FilesProcessed,
                                    progress.TotalFiles,
                                    overallPercent,
                                    context));
                        });

                    // Harvest this datasource's outcome numbers from the final checkpoint
                    // before the finally below deletes the file. Both Rust remove flows persist
                    // {count, files, logLines, downloads, logEntries} in their 100% checkpoint.
                    if (result.Success)
                    {
                        var finalProgress = await _rustProcessHelper.ReadProgressFileAsync<CorruptionRemovalProgressData>(progressFilePath);
                        if (finalProgress is { Status: "completed", Context: not null })
                        {
                            totals.UrlsRemoved += ReadContextCount(finalProgress.Context, "count");
                            totals.FilesDeleted += ReadContextCount(finalProgress.Context, "files");
                            totals.LogLinesRemoved += ReadContextCount(finalProgress.Context, "logLines");
                            totals.DownloadsDeleted += ReadContextCount(finalProgress.Context, "downloads");
                            totals.LogEntriesDeleted += ReadContextCount(finalProgress.Context, "logEntries");
                        }
                    }

                    if (result.Success)
                    {
                        _logger.LogInformation("[CorruptionRemoval] Completed for service {Service} on datasource '{Datasource}'",
                            service, datasource.Name);
                    }
                    else
                    {
                        _logger.LogError("[CorruptionRemoval] Failed for service {Service} on datasource '{Datasource}': {Error}",
                            service, datasource.Name, result.Error);
                        allSucceeded = false;
                        lastError = result.Error;
                    }
                }
                finally
                {
                    await _rustProcessHelper.DeleteTempFileAsync(progressFilePath);
                    await _rustProcessHelper.DeleteTempFileAsync(evidenceFilePath);
                }
            }

            if (allSucceeded)
            {
                _logger.LogInformation("Corruption removal completed for service: {Service} across all datasources", service);

                // Signal nginx to reopen log files (prevents monolithic container from losing log access)
                await _nginxLogRotationService.ReopenNginxLogsAsync();

                // Invalidate service count cache since corruption removal affects counts
                await _cacheService.InvalidateServiceCountsAsync();

                // Only a fully successful service run can prune persisted evidence.
                // Any partial/permission/process failure leaves the authoritative scope intact.
                await _corruptionDetectionService.ApplyRemovalSuccessAsync(
                    selection.ScanId,
                    selection.CandidateIds,
                    cts.Token);

                // Terminal SignalR emit is centralized in the onTerminalEmit closure
                // registered with RegisterOperation (fires exactly once from CompleteOperation).
                _operationTracker.CompleteOperation(operationId, success: true);

                if (bulk != null)
                {
                    bulk.SucceededServices++;
                    bulk.Totals.Add(totals);
                }

                return true;
            }

            _logger.LogError("Corruption removal failed for service {Service}: {Error}", service, lastError);
            _operationTracker.CompleteOperation(operationId, success: false, error: lastError);
            if (bulk != null)
            {
                bulk.FailedServices++;
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Corruption removal cancelled for service: {Service}", service);
            _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
            // Rethrow so the all-services loop can stop processing further services;
            // the single-service caller swallows this (its operation is already completed).
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during corruption removal for service: {Service}", service);
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
            if (bulk != null)
            {
                bulk.FailedServices++;
            }

            return false;
        }
    }

    /// <summary>
    /// GET /api/cache/services/{service}/corruption/status - Get corruption removal status
    /// Used for restoring progress on page refresh
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("services/{service}/corruption/status")]
    public IActionResult GetCorruptionRemovalStatus(string service)
    {
        var operation = _operationTracker.GetOperationByEntityKey(OperationType.CorruptionRemoval, service.ToLowerInvariant());
        if (operation == null)
        {
            return Ok(new RemovalStatusResponse { IsProcessing = false });
        }

        var metrics = operation.Metadata as RemovalMetrics;
        return Ok(new RemovalStatusResponse
        {
            // Include all non-terminal statuses (running, removing, etc.)
            IsProcessing = operation.Status != OperationStatus.Completed && operation.Status != OperationStatus.Failed,
            Status = operation.Status,
            Message = operation.Message,
            OperationId = operation.Id,
            StartedAt = operation.StartedAt,
            Error = operation.Status == OperationStatus.Failed ? operation.Message : null
        });
    }

    /// <summary>
    /// GET /api/cache/corruption/removals/active - Get all active corruption removal operations
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("corruption/removals/active")]
    public IActionResult GetActiveCorruptionRemovals()
    {
        var operations = _operationTracker.GetActiveOperations(OperationType.CorruptionRemoval);
        return Ok(new ActiveCorruptionRemovalsResponse
        {
            IsProcessing = operations.Any(),
            Operations = operations.Select(op =>
            {
                var metrics = op.Metadata as RemovalMetrics;
                return new CorruptionRemovalInfo
                {
                    Service = metrics?.EntityName ?? op.Name,
                    OperationId = op.Id,
                    Status = op.Status,
                    Message = op.Message,
                    StartedAt = op.StartedAt
                };
            })
        });
    }

    /// <summary>
    /// Checks cache and logs directory write permissions (mirrors GamesController's helper).
    /// Returns a BadRequest IActionResult with the PUID/PGID error message if either directory
    /// is read-only, or null when both are writable. Logs a warning with the given context.
    /// </summary>
    private BadRequestObjectResult? EnsureDirectoriesWritable(string operationDescription, string logContext)
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

        _logger.LogWarning("{Context} Permission check failed: {Error}", logContext, errorMessage);
        return BadRequest(new ErrorResponse { Error = errorMessage });
    }

    /// <summary>
    /// DELETE /api/cache/services/{name} - Remove specific service from cache
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("services/{name}")]
    public async Task<IActionResult> ClearServiceCacheAsync(string name, CancellationToken requestCt)
    {
        // CRITICAL: Check write permissions BEFORE starting the operation
        // This prevents operations from failing partway through due to permission issues
        var permissionError = EnsureDirectoriesWritable(
            "remove service from cache", $"[ClearServiceCache] service '{name}':");
        if (permissionError != null)
        {
            return permissionError;
        }

        // The whole start path lives in this local function so the wait-queue can run it
        // verbatim at promotion time.
        async Task<Guid?> StartServiceRemovalAsync()
        {
        _cacheService.InvalidateCachedScan();

        _logger.LogInformation("Starting background service removal for: {Service}", name);

        var metadata = new RemovalMetrics { EntityKey = name.ToLowerInvariant(), EntityName = name };
        return await TrackedRemovalOperationRunner.StartAsync(
            _operationTracker,
            _notifications,
            new TrackedRemovalOperationRunner.RemovalOperationConfig<CacheManagementService.ServiceCacheRemovalReport>(
                OperationType: OperationType.ServiceRemoval,
                OperationLabel: $"Service removal: {name}",
                Metadata: metadata,
                StartedEventName: SignalREvents.ServiceRemovalStarted,
                BuildStartedPayload: id => new ServiceRemovalStarted(
                    name,
                    id,
                    "signalr.serviceRemove.starting.byName",
                    DateTime.UtcNow,
                    new Dictionary<string, object?> { ["name"] = name }),
                ProgressEventName: SignalREvents.ServiceRemovalProgress,
                InitialStageKey: "signalr.serviceRemove.starting.byName",
                BuildInitialProgressPayload: id => new ServiceRemovalProgress(
                    name,
                    id,
                    "signalr.serviceRemove.starting.byName",
                    0,
                    Context: new Dictionary<string, object?> { ["name"] = name }),
                BuildProgressPayload: (id, update) => new ServiceRemovalProgress(
                    name,
                    id,
                    update.StageKey,
                    update.PercentComplete,
                    update.FilesDeleted > 0 ? update.FilesDeleted : null,
                    update.BytesFreed > 0 ? update.BytesFreed : null,
                    update.Context),
                CompleteEventName: SignalREvents.ServiceRemovalComplete,
                FinalizingStageKey: "signalr.serviceRemove.finalizing",
                BuildFinalizingProgressPayload: (id, report) => new ServiceRemovalProgress(
                    name,
                    id,
                    "signalr.serviceRemove.finalizing",
                    100.0,
                    report.CacheFilesDeleted,
                    (long)report.TotalBytesFreed),
                BuildSuccessPayload: (id, report) => new ServiceRemovalComplete(
                    true,
                    name,
                    id,
                    "signalr.serviceRemove.success",
                    report.CacheFilesDeleted,
                    (long)report.TotalBytesFreed,
                    report.LogEntriesRemoved,
                    new Dictionary<string, object?> { ["name"] = name }),
                BuildCancelledPayload: id => new ServiceRemovalComplete(
                    false,
                    name,
                    id,
                    "signalr.serviceRemove.cancelled",
                    Context: new Dictionary<string, object?> { ["name"] = name }),
                BuildErrorProgressPayload: (id, ex) => new ServiceRemovalProgress(
                    name,
                    id,
                    "signalr.serviceRemove.error.default",
                    0,
                    Context: new Dictionary<string, object?> { ["name"] = name, ["errorDetail"] = ex.Message }),
                BuildErrorCompletePayload: (id, ex) => new ServiceRemovalComplete(
                    false,
                    name,
                    id,
                    "signalr.serviceRemove.failed.generic",
                    Context: new Dictionary<string, object?> { ["name"] = name },
                    Error: ex.Message),
                ExecuteAsync: (opId, ct, onProgress) => _cacheService.RemoveServiceFromCacheAsync(
                    name,
                    ct,
                    (percentComplete, stageKey, context, filesDeleted, bytesFreed) =>
                        onProgress(new TrackedRemovalOperationRunner.RemovalProgressUpdate(
                            percentComplete,
                            stageKey,
                            context,
                            filesDeleted,
                            bytesFreed)),
                    opId),
                ApplyProgressMetrics: (removalMetrics, update) =>
                {
                    if (update.FilesDeleted > 0)
                    {
                        removalMetrics.FilesDeleted = update.FilesDeleted;
                    }

                    if (update.BytesFreed > 0)
                    {
                        removalMetrics.BytesFreed = update.BytesFreed;
                    }
                },
                ApplyFinalMetrics: (removalMetrics, report) =>
                {
                    removalMetrics.FilesDeleted = report.CacheFilesDeleted;
                    removalMetrics.BytesFreed = (long)report.TotalBytesFreed;
                },
                LogSuccess: (_, report) =>
                {
                    _logger.LogInformation(
                        "Service removal completed for {Service} - Deleted {Files} files, freed {Bytes} bytes",
                        name,
                        report.CacheFilesDeleted,
                        report.TotalBytesFreed);
                },
                LogCancelled: _ =>
                {
                    _logger.LogInformation("Service removal cancelled for: {Service}", name);
                },
                LogFailure: (_, ex) =>
                {
                    _logger.LogError(ex, "Error during service removal for: {Service}", name);
                }));
        }

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        var conflict = await _conflictChecker.CheckAsync(
            OperationType.ServiceRemoval,
            ConflictScope.Service(name),
            requestCt);
        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.ServiceRemoval, ConflictScope.Service(name),
                $"Service Removal ({name})", StartServiceRemovalAsync, requestCt));
        }

        var operationId = await StartServiceRemovalAsync();

        return Accepted(new CacheOperationResponse
        {
            Message = $"Started removal of {name} service from cache",
            OperationId = operationId,
            ServiceName = name,
            Status = OperationStatus.Running
        });
    }

    /// <summary>
    /// GET /api/cache/services/{name}/removal-status - Get service removal status
    /// Used for restoring progress on page refresh
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("services/{name}/removal-status")]
    public IActionResult GetServiceRemovalStatus(string name)
    {
        var operation = _operationTracker.GetOperationByEntityKey(OperationType.ServiceRemoval, name.ToLowerInvariant());
        if (operation == null)
        {
            return Ok(new RemovalStatusResponse { IsProcessing = false });
        }

        var metrics = operation.Metadata as RemovalMetrics;
        return Ok(new RemovalStatusResponse
        {
            // Include all non-terminal statuses (running, removing_cache, removing_database, etc.)
            IsProcessing = operation.Status != OperationStatus.Completed && operation.Status != OperationStatus.Failed,
            Status = operation.Status,
            Message = operation.Message,
            FilesDeleted = metrics?.FilesDeleted ?? 0,
            BytesFreed = metrics?.BytesFreed ?? 0,
            StartedAt = operation.StartedAt,
            Error = operation.Status == OperationStatus.Failed ? operation.Message : null
        });
    }

    /// <summary>
    /// GET /api/cache/services/removals/active - Get all active service removal operations
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("services/removals/active")]
    public IActionResult GetActiveServiceRemovals()
    {
        var operations = _operationTracker.GetActiveOperations(OperationType.ServiceRemoval);
        return Ok(new ActiveServiceRemovalsResponse
        {
            IsProcessing = operations.Any(),
            Operations = operations.Select(op =>
            {
                var metrics = op.Metadata as RemovalMetrics;
                return new ServiceRemovalInfo
                {
                    ServiceName = metrics?.EntityName ?? op.Name,
                    OperationId = op.Id,
                    Status = op.Status,
                    Message = op.Message,
                    FilesDeleted = metrics?.FilesDeleted ?? 0,
                    BytesFreed = metrics?.BytesFreed ?? 0,
                    StartedAt = op.StartedAt
                };
            })
        });
    }

    /// <summary>
    /// GET /api/cache/removals/active - Get all active removal operations (games, services, corruption)
    /// Used for universal recovery on page refresh
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("removals/active")]
    public IActionResult GetAllActiveRemovals()
    {
        var gameOps = _operationTracker.GetActiveOperations(OperationType.GameRemoval);
        var serviceOps = _operationTracker.GetActiveOperations(OperationType.ServiceRemoval);
        var corruptionOps = _operationTracker.GetActiveOperations(OperationType.CorruptionRemoval);
        var historicalEvidenceOps = _operationTracker.GetActiveOperations(OperationType.HistoricalEvidencePurge);
        // Silent removals (automatic Remove-mode auto-cleanup) emit no SignalR events and must
        // stay invisible to recovery too - reporting them here would make recoverEvictionRemovals
        // create a notification card for a deliberately silent operation.
        var evictionOps = _operationTracker.GetActiveOperations(OperationType.EvictionRemoval)
            .Where(op => !_reconciliationService.IsSilentRemovalOperation(op.Id))
            .ToList();

        return Ok(new AllActiveRemovalsResponse
        {
            IsProcessing = gameOps.Any() || serviceOps.Any() || corruptionOps.Any()
                || evictionOps.Any() || historicalEvidenceOps.Any(),
            GameRemovals = gameOps.Select(op =>
            {
                var metrics = op.Metadata as RemovalMetrics;

                // Scope-aware identity: steam populates GameAppId, epic populates EpicAppId.
                // Legacy rows without EntityKind fall back to numeric parse for Steam compat.
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
                    default:
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
            }),
            ServiceRemovals = serviceOps.Select(op =>
            {
                var metrics = op.Metadata as RemovalMetrics;
                return new ServiceRemovalInfo
                {
                    ServiceName = metrics?.EntityName ?? op.Name,
                    OperationId = op.Id,
                    Status = op.Status,
                    Message = op.Message,
                    FilesDeleted = metrics?.FilesDeleted ?? 0,
                    BytesFreed = metrics?.BytesFreed ?? 0,
                    StartedAt = op.StartedAt
                };
            }),
            CorruptionRemovals = corruptionOps.Select(op =>
            {
                var metrics = op.Metadata as RemovalMetrics;
                return new CorruptionRemovalInfo
                {
                    Service = metrics?.EntityName ?? op.Name,
                    OperationId = op.Id,
                    Status = op.Status,
                    Message = op.Message,
                    StartedAt = op.StartedAt
                };
            }),
            EvictionRemovals = evictionOps.Select(op =>
            {
                var meta = op.Metadata as EvictionRemovalMetadata;
                return new EvictionRemovalInfo
                {
                    Scope = meta?.Scope,
                    Key = meta?.Key,
                    GameName = meta?.GameName,
                    OperationId = op.Id,
                    Status = op.Status,
                    Message = op.Message,
                    StartedAt = op.StartedAt
                };
            }),
            HistoricalEvidencePurges = historicalEvidenceOps.Select(op =>
            {
                var metadata = op.Metadata as HistoricalEvidencePurgeMetadata;
                return new HistoricalEvidencePurgeInfo
                {
                    ScanId = metadata?.ScanId ?? Guid.Empty,
                    Scope = metadata?.Scope ?? "all",
                    CandidateCount = metadata?.CandidateCount ?? 0,
                    OperationId = op.Id,
                    Status = op.Status,
                    Message = op.Message,
                    StartedAt = op.StartedAt
                };
            })
        });
    }

    /// <summary>
    /// DELETE /api/cache/evicted
    ///
    /// Removes ALL evicted Downloads, their LogEntries, and the evicted detection rows in a
    /// single batched operation: one access.log rewrite pass covering every evicted entity,
    /// one transaction of DB deletes, and one disk-summary refresh. Replaces the old frontend
    /// loop of silent per-entity removals, which rewrote the logs once per entity and emitted
    /// no SignalR events.
    ///
    /// Progress/cancel/recovery flow through the standard eviction_removal notification
    /// (EvictionRemovalStarted with the bulk stage key, Progress ticks, terminal Complete).
    ///
    /// Returns 202 Accepted with { operationId }.
    /// Returns 409 Conflict if another eviction removal is already in progress.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("evicted")]
    public async Task<IActionResult> RemoveAllEvictedAsync(CancellationToken cancellationToken = default)
    {
        var permissionError = EnsureDirectoriesWritable("remove evicted data", "[EvictedRemoval] bulk:");
        if (permissionError != null)
        {
            return permissionError;
        }

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        // The closure uses CancellationToken.None: at promotion time the originating HTTP
        // request is long gone; the started op runs on its own registered CTS.
        async Task<Guid?> StartBulkEvictedRemovalAsync() =>
            await _reconciliationService.StartBulkEvictionRemovalAsync(CancellationToken.None);

        var conflict = await _conflictChecker.CheckAsync(
            OperationType.EvictionRemoval,
            ConflictScope.Bulk(),
            cancellationToken);
        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.EvictionRemoval, ConflictScope.Bulk(),
                "Evicted Data Removal (all)", StartBulkEvictedRemovalAsync, cancellationToken));
        }

        var operationId = await _reconciliationService.StartBulkEvictionRemovalAsync(cancellationToken);
        return Accepted(new { operationId });
    }

    /// <summary>
    /// DELETE /api/cache/evicted/{scope}?key={value}
    ///
    /// Removes only the evicted Downloads and their LogEntries for a single entity,
    /// leaving any active Downloads for the same entity intact.
    ///
    /// scope: "steam" | "epic" | "service"
    /// key:   Steam gameAppId (long), Epic epicAppId (string), or service name (string)
    ///
    /// Returns 202 Accepted with { operationId, scope, key }.
    /// Returns 409 Conflict if a global eviction removal is already in progress.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("evicted/{scope}")]
    public async Task<IActionResult> RemoveEvictedForEntityAsync(string scope, [FromQuery] string? key, CancellationToken cancellationToken = default)
    {
        // Validate key parameter.
        if (string.IsNullOrWhiteSpace(key))
        {
            return BadRequest(new ErrorResponse { Error = "Query parameter 'key' is required and must not be empty." });
        }

        // Normalise and validate scope.
        var scopeLower = scope.ToLowerInvariant();
        if (scopeLower == "named")
        {
            return BadRequest(new ErrorResponse { Error = "For named (Blizzard/Riot) games use DELETE evicted/named/{service}/{gameName}." });
        }
        if (scopeLower != "steam" && scopeLower != "epic" && scopeLower != "service")
        {
            return BadRequest(new ErrorResponse { Error = $"Invalid scope '{scope}'. Must be 'steam', 'epic', or 'service'." });
        }

        // Steam scope requires key to parse as a positive long.
        long steamAppId = 0;
        if (scopeLower == "steam")
        {
            if (!long.TryParse(key, out steamAppId) || steamAppId <= 0)
            {
                return BadRequest(new ErrorResponse { Error = $"For scope 'steam', key must be a positive integer (GameAppId). Received: '{key}'." });
            }
        }

        // Lowercase service key for consistent matching.
        if (scopeLower == "service")
        {
            key = key.ToLowerInvariant();
        }

        var permissionError = EnsureDirectoriesWritable(
            "remove evicted data", $"[EvictedRemoval] {scope} '{key}':");
        if (permissionError != null)
        {
            return permissionError;
        }

        // Central concurrency check - scope-aware (replaces the global eviction lock bug).
        // Different entities can now run concurrently; bulk/service-wide still blocks entity-level.
        var conflictScope = scopeLower switch
        {
            "steam" => ConflictScope.SteamGame(steamAppId),
            "epic" => ConflictScope.EpicGame(key, key),
            "service" => ConflictScope.Service(key),
            _ => ConflictScope.Bulk()
        };
        var conflict = await _conflictChecker.CheckAsync(
            OperationType.EvictionRemoval,
            conflictScope,
            cancellationToken);

        var evictionScope = scopeLower switch
        {
            "steam" => EvictionScope.Steam,
            "epic" => EvictionScope.Epic,
            "service" => EvictionScope.Service,
            _ => throw new InvalidOperationException($"Unreachable scope: {scopeLower}")
        };

        // For Epic or Steam scope, look up the game name so the frontend can display it in the notification bar.
        // Direct lookup is deliberate: this endpoint only needs notification metadata, not the load/upsert flow GameCacheDetectionDataService owns.
        string? resolvedGameName = null;
        string? resolvedGameAppId = null;
        if (evictionScope == EvictionScope.Epic)
        {
            await using var lookupDb = await _dbContextFactory.CreateDbContextAsync();
            var detection = await lookupDb.CachedGameDetections
                .Where(g => g.EpicAppId == key)
                .Select(g => new { g.GameName, g.GameAppId })
                .FirstOrDefaultAsync();
            if (detection != null)
            {
                resolvedGameName = detection.GameName;
                resolvedGameAppId = detection.GameAppId.ToString();
            }
        }
        else if (evictionScope == EvictionScope.Steam)
        {
            await using var lookupDb = await _dbContextFactory.CreateDbContextAsync();
            var detection = await lookupDb.CachedGameDetections
                .Where(g => g.GameAppId == steamAppId)
                .Select(g => new { g.GameName, g.GameAppId })
                .FirstOrDefaultAsync();
            if (detection != null)
            {
                resolvedGameName = detection.GameName;
                resolvedGameAppId = detection.GameAppId.ToString();
            }
        }

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        // CancellationToken.None in the closure: at promotion the HTTP request is long gone;
        // the started op runs on its own registered CTS.
        async Task<Guid?> StartScopedEvictedRemovalAsync() =>
            await _reconciliationService.StartScopedEvictionRemovalAsync(
                evictionScope,
                key,
                resolvedGameName,
                resolvedGameAppId,
                CancellationToken.None,
                resolvedEpicAppId: evictionScope == EvictionScope.Epic ? key : null);

        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.EvictionRemoval, conflictScope,
                $"Evicted Data Removal ({scopeLower}: {key})", StartScopedEvictedRemovalAsync, cancellationToken));
        }

        var operationId = await StartScopedEvictedRemovalAsync();

        return Accepted(new { operationId, scope = scopeLower, key });
    }

    /// <summary>
    /// DELETE /api/cache/evicted/named/{service}/{gameName}
    ///
    /// Removes only the evicted Downloads, their LogEntries, and the evicted detection row
    /// for a single named (Blizzard/Riot) game, leaving any still-cached Downloads for the
    /// same game intact. Named games have no Steam AppId and no Epic AppId; their identity is
    /// (Service, GameName), so they need a dedicated two-segment route (the generic
    /// <c>evicted/{scope}</c> endpoint cannot carry both halves of the key).
    ///
    /// Returns 202 Accepted with { operationId, scope = "named", service, gameName }.
    /// Returns 202 Accepted with a queued operationId if a conflicting removal is already in progress.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("evicted/named/{service}/{gameName}")]
    public async Task<IActionResult> RemoveEvictedForNamedGameAsync(string service, string gameName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return BadRequest(new ErrorResponse { Error = "Route parameter 'service' is required and must not be empty." });
        }
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return BadRequest(new ErrorResponse { Error = "Route parameter 'gameName' is required and must not be empty." });
        }

        // Service is stored lowercase; the eviction key carries the lowercased service while the
        // game name (case-sensitive, as stored in CachedGameDetection) travels alongside.
        var serviceLower = service.ToLowerInvariant();

        var permissionError = EnsureDirectoriesWritable(
            "remove evicted data", $"[EvictedRemoval] named '{serviceLower}:{gameName}':");
        if (permissionError != null)
        {
            return permissionError;
        }

        // Scope-aware concurrency check - reuse the same NamedGame conflict scope as full removal.
        var conflictScope = ConflictScope.NamedGame(serviceLower, gameName);
        var conflict = await _conflictChecker.CheckAsync(
            OperationType.EvictionRemoval,
            conflictScope,
            cancellationToken);

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        // CancellationToken.None in the closure: at promotion the HTTP request is long gone;
        // the started op runs on its own registered CTS.
        async Task<Guid?> StartScopedEvictedRemovalAsync() =>
            await _reconciliationService.StartScopedEvictionRemovalAsync(
                EvictionScope.Named,
                serviceLower,
                resolvedGameName: gameName,
                resolvedGameAppId: "0",
                CancellationToken.None,
                resolvedEpicAppId: null,
                namedGameName: gameName);

        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.EvictionRemoval, conflictScope,
                $"Evicted Data Removal (named: {serviceLower}:{gameName})", StartScopedEvictedRemovalAsync, cancellationToken));
        }

        var operationId = await StartScopedEvictedRemovalAsync();

        return Accepted(new { operationId, scope = "named", service = serviceLower, gameName });
    }
}
