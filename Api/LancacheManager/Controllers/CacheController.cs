using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Hubs;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
        IOperationQueue operationQueue)
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

        var lastDetectionTimeUtc = cachedResults.LastDetectionTime.AsUtc();

        return Ok(new CachedCorruptionResponse
        {
            HasCachedResults = true,
            CorruptionCounts = cachedResults.CorruptionCounts,
            TotalServicesWithCorruption = cachedResults.TotalServicesWithCorruption,
            TotalCorruptedChunks = cachedResults.TotalCorruptedChunks,
            LastDetectionTime = lastDetectionTimeUtc.ToString("o")
        });
    }

    /// <summary>
    /// POST /api/cache/corruption/detect - Start a background corruption detection scan
    /// Returns immediately with an operation ID. Results sent via SignalR when complete.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpPost("corruption/detect")]
    public async Task<IActionResult> StartCorruptionDetectionAsync(
        [FromQuery] int threshold = 3,
        [FromQuery] bool compareToCacheLogs = true,
        [FromQuery] string detectionMode = "miss_count",
        CancellationToken cancellationToken = default)
    {
        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        // The bulk corruption scan is a heavy data op (OperationConflictChecker section 1a), so
        // it queues behind any other active heavy operation instead of running alongside it.
        // Deliberately no request token in the delegate: it may run at queue promotion, long
        // after this HTTP request completed (the operation owns its own CTS via the tracker).
        async Task<Guid?> StartDetectionAsync() =>
            await _corruptionDetectionService.StartDetectionAsync(threshold, compareToCacheLogs, detectionMode);

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
    /// Returns array of corrupted chunks with URLs, miss counts, and cache file paths
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("services/{service}/corruption")]
    public async Task<IActionResult> GetCorruptionDetailsAsync(
        string service,
        CancellationToken cancellationToken,
        [FromQuery] bool forceRefresh = false,
        [FromQuery] int threshold = 3,
        [FromQuery] bool compareToCacheLogs = true,
        [FromQuery] string detectionMode = "miss_count")
    {
        if (!_serviceNameRegex.IsMatch(service))
        {
            return BadRequest(new ErrorResponse { Error = "Invalid service name" });
        }

        var conflict = await _conflictChecker.CheckAsync(
            OperationType.CorruptionDetection,
            ConflictScope.Service(service),
            cancellationToken);
        if (conflict != null)
        {
            return Conflict(conflict);
        }

        var detectRedownloads = detectionMode == "redownload";
        var cts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

        var operationId = _operationTracker.RegisterOperation(
            OperationType.CorruptionDetection,
            $"Corruption Details ({service})",
            cts,
            new CorruptionDetectionMetrics { ServiceName = service });

        try
        {
            var details = await _cacheService.GetCorruptionDetailsAsync(
                service,
                forceRefresh,
                threshold,
                compareToCacheLogs,
                operationId,
                linkedCts.Token,
                detectRedownloads);

            _operationTracker.CompleteOperation(operationId, success: true);
            return Ok(details);
        }
        catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
        {
            _logger.LogInformation("Corruption details request cancelled for service: {Service}", service);
            _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled");
            throw; // -> GlobalExceptionMiddleware -> 499, no body
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving corruption details for service: {Service}", service);
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
            throw;
        }
    }

    /// <summary>
    /// DELETE /api/cache/services/{name}/corruption - Remove corrupted chunks for specific service
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("services/{service}/corruption")]
    public async Task<IActionResult> RemoveCorruptedChunksAsync(string service, CancellationToken cancellationToken, [FromQuery] int threshold = 3, [FromQuery] bool compareToCacheLogs = true, [FromQuery] string detectionMode = "miss_count")
    {
        if (!_serviceNameRegex.IsMatch(service))
        {
            return BadRequest(new ErrorResponse { Error = "Invalid service name" });
        }

        var datasources = _datasourceService.GetDatasources();

        // CRITICAL: Check write permissions BEFORE starting (or queueing) the operation
        // This prevents the DB/filesystem state mismatch when PUID/PGID is wrong
        var permissionError = CheckDatasourcesWritable(datasources, service);
        if (permissionError != null)
        {
            return BadRequest(new ErrorResponse { Error = permissionError });
        }

        // The whole start path lives in this local function so the wait-queue can run it
        // verbatim at promotion time (captures only singleton services + this controller).
        async Task<Guid?> StartCorruptionRemovalAsync()
        {
        _cacheService.InvalidateCachedScan();

        // Optimistically delete the cached detection row immediately so app restarts don't resurface stale data
        await _corruptionDetectionService.ClearServiceCacheAsync(service);

        // Register synchronously (inside the core) but surface the operationId to this
        // request thread so it can be returned in the HTTP response.
        var operationIdReady = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            try
            {
                // Pause LiveLogMonitorService to prevent file locking issues.
                // Pause MUST be inside this try so the finally below always resumes,
                // even if a cancel/throw happens between Pause and the work loop.
                await LiveLogMonitorService.PauseAsync();
                _logger.LogInformation("Paused LiveLogMonitorService for corruption removal");

                try
                {
                    await RunCorruptionRemovalCoreAsync(
                        service, threshold, compareToCacheLogs, detectionMode, datasources,
                        onRegistered: id => operationIdReady.TrySetResult(id));
                }
                catch (OperationCanceledException)
                {
                    // The core already completed the operation as cancelled.
                }
            }
            finally
            {
                // Always resume LiveLogMonitorService
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
    public async Task<IActionResult> RemoveAllCorruptedChunksAsync(CancellationToken cancellationToken, [FromQuery] int threshold = 3, [FromQuery] bool compareToCacheLogs = true, [FromQuery] string detectionMode = "miss_count", [FromQuery] string? services = null)
    {
        // Query which services currently have corruption data
        var cachedDetection = await _corruptionDetectionService.GetDetectionAsync();
        if (cachedDetection == null || cachedDetection.CorruptionCounts == null || cachedDetection.CorruptionCounts.Count == 0)
        {
            return Ok(new { Message = "No corruption data found. Run a corruption detection scan first." });
        }

        var servicesWithCorruption = cachedDetection.CorruptionCounts.Keys.ToList();

        // Optional subset filter: when 'services' is provided (comma-separated), restrict the
        // removal to only those services. Absent/empty preserves the all-services behavior
        // byte-for-byte. Unknown names (no corruption data in the current summary) are skipped
        // with a warning rather than failing, since the summary may have changed since the UI
        // loaded. The existing bulk machinery below still emits a single Service="all" terminal.
        if (!string.IsNullOrWhiteSpace(services))
        {
            var requested = services
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            var available = new HashSet<string>(servicesWithCorruption, StringComparer.OrdinalIgnoreCase);
            foreach (var name in requested.Where(name => !available.Contains(name)))
            {
                _logger.LogWarning("[CorruptionRemoval] Requested service '{Service}' has no corruption data and was skipped", name);
            }

            var requestedSet = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
            servicesWithCorruption = servicesWithCorruption.Where(s => requestedSet.Contains(s)).ToList();

            if (servicesWithCorruption.Count == 0)
            {
                return Ok(new { Message = "No matching services with corruption data to remove." });
            }
        }

        var datasources = _datasourceService.GetDatasources();

        // CRITICAL: Check write permissions BEFORE starting (or queueing) the operation
        var permissionError = CheckDatasourcesWritable(datasources, service: null);
        if (permissionError != null)
        {
            return BadRequest(new ErrorResponse { Error = permissionError });
        }

        // The whole start path lives in this local function so the wait-queue can run it
        // verbatim at promotion time. Returns a synthetic handle (the per-service cores
        // register their own per-service operations once running).
        async Task<Guid?> StartAllCorruptionRemovalAsync()
        {
        _logger.LogInformation("[CorruptionRemoval] Starting all-services corruption removal for {Count} service(s): {Services}",
            servicesWithCorruption.Count, string.Join(", ", servicesWithCorruption));

        _cacheService.InvalidateCachedScan();

        // Delete cached detection rows per-service and activate grace period to prevent immediate reappearance
        foreach (var service in servicesWithCorruption)
        {
            await _corruptionDetectionService.ClearServiceCacheAsync(service);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Pause LiveLogMonitorService ONCE to prevent file locking issues across all services.
                // Pause MUST be inside this try so the finally below always resumes,
                // even if a cancel/throw happens between Pause and the work loop.
                await LiveLogMonitorService.PauseAsync();
                _logger.LogInformation("Paused LiveLogMonitorService for all-services corruption removal");

                // One shared state for the whole run: per-service cores suppress their terminal
                // emits and report in here, so the notification card advances service by service
                // and completes exactly once below with aggregated totals.
                var bulkState = new BulkCorruptionRemovalState { ServiceCount = servicesWithCorruption.Count };
                var cancelled = false;

                for (var serviceIndex = 0; serviceIndex < servicesWithCorruption.Count; serviceIndex++)
                {
                    var service = servicesWithCorruption[serviceIndex];
                    bulkState.ServiceIndex = serviceIndex + 1;
                    try
                    {
                        await RunCorruptionRemovalCoreAsync(
                            service, threshold, compareToCacheLogs, detectionMode, datasources,
                            bulk: bulkState);
                    }
                    catch (OperationCanceledException)
                    {
                        // The core already completed this service's operation as cancelled.
                        // Stop processing further services on cancellation.
                        cancelled = true;
                        break;
                    }
                }

                // The run's single terminal emit.
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
                // Always resume LiveLogMonitorService
                await LiveLogMonitorService.ResumeAsync();
                _logger.LogInformation("Resumed LiveLogMonitorService after all-services corruption removal");
            }
        });

        return Guid.NewGuid();
        }

        // Wait-queue model: if any service is blocked, park the whole all-services run behind
        // the FIRST conflicting service's scope (promotion re-checks that scope; see report -
        // other services' conflicts are handled per-service by the cores once running).
        foreach (var svc in servicesWithCorruption)
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
    /// conflict/permission checks, the optimistic <c>RemoveCachedServiceAsync</c> row delete,
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
        string service,
        int threshold,
        bool compareToCacheLogs,
        string detectionMode,
        IReadOnlyList<ResolvedDatasource> datasources,
        Action<Guid>? onRegistered = null,
        BulkCorruptionRemovalState? bulk = null)
    {
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

                _logger.LogInformation("[CorruptionRemoval] Processing datasource '{Datasource}' (logs: {LogsPath}, cache: {CachePath})",
                    datasource.Name, logsPath, cachePath);

                try
                {
                    // Hybrid transport (mirrors CacheClearingService): the stdout progress event
                    // from corruption_manager is a zero-latency wake-up that triggers exactly one
                    // read of the (Rust-side-unchanged) progress file, replacing the previous
                    // standalone Task.Run poll-every-500ms loop.
                    var result = await _rustProcessHelper.RunCorruptionManagerAsync(
                        "remove",
                        logsPath,
                        cachePath,
                        service: service,
                        progressFile: progressFilePath,
                        cancellationToken: cts.Token,
                        threshold: threshold,
                        compareToCacheLogs: compareToCacheLogs,
                        detectRedownloads: detectionMode == "redownload",
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
                    // Clean up progress file for this datasource
                    try { if (System.IO.File.Exists(progressFilePath)) System.IO.File.Delete(progressFilePath); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to clean up progress file"); }
                }
            }

            if (allSucceeded)
            {
                _logger.LogInformation("Corruption removal completed for service: {Service} across all datasources", service);

                // Signal nginx to reopen log files (prevents monolithic container from losing log access)
                await _nginxLogRotationService.ReopenNginxLogsAsync();

                // Invalidate service count cache since corruption removal affects counts
                await _cacheService.InvalidateServiceCountsAsync();

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
        // Silent removals (automatic Remove-mode auto-cleanup) emit no SignalR events and must
        // stay invisible to recovery too - reporting them here would make recoverEvictionRemovals
        // create a notification card for a deliberately silent operation.
        var evictionOps = _operationTracker.GetActiveOperations(OperationType.EvictionRemoval)
            .Where(op => !_reconciliationService.IsSilentRemovalOperation(op.Id))
            .ToList();

        return Ok(new AllActiveRemovalsResponse
        {
            IsProcessing = gameOps.Any() || serviceOps.Any() || corruptionOps.Any() || evictionOps.Any(),
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
