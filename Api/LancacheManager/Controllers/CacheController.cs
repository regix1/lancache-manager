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
    private readonly CacheManagementService _cacheService;
    private readonly CacheClearingService _cacheClearingService;
    private readonly GameCacheDetectionService _gameCacheDetectionService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CacheController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly StateService _stateService;
    private readonly ISignalRNotificationService _notifications;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly NginxLogRotationService _nginxLogRotationService;
    private readonly CorruptionDetectionService _corruptionDetectionService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly DatasourceService _datasourceService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly CacheReconciliationService _reconciliationService;
    private readonly IOperationConflictChecker _conflictChecker;

    public CacheController(
        CacheManagementService cacheService,
        CacheClearingService cacheClearingService,
        GameCacheDetectionService gameCacheDetectionService,
        CorruptionDetectionService corruptionDetectionService,
        IConfiguration configuration,
        ILogger<CacheController> logger,
        IPathResolver pathResolver,
        StateService stateService,
        ISignalRNotificationService notifications,
        RustProcessHelper rustProcessHelper,
        NginxLogRotationService nginxLogRotationService,
        IUnifiedOperationTracker operationTracker,
        DatasourceService datasourceService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        CacheReconciliationService reconciliationService,
        IOperationConflictChecker conflictChecker)
    {
        _cacheService = cacheService;
        _cacheClearingService = cacheClearingService;
        _gameCacheDetectionService = gameCacheDetectionService;
        _corruptionDetectionService = corruptionDetectionService;
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;
        _stateService = stateService;
        _notifications = notifications;
        _rustProcessHelper = rustProcessHelper;
        _nginxLogRotationService = nginxLogRotationService;
        _operationTracker = operationTracker;
        _datasourceService = datasourceService;
        _dbContextFactory = dbContextFactory;
        _reconciliationService = reconciliationService;
        _conflictChecker = conflictChecker;
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
    /// GET /api/cache/size - Calculate cache size with deletion time estimates
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("size")]
    [Authorize]
    public async Task<IActionResult> GetCacheSizeAsync([FromQuery] string? datasource = null, [FromQuery] bool force = false)
    {
        var result = await _cacheService.GetCachedCacheSizeAsync(force, datasource);
        if (result == null)
        {
            return StatusCode(500, new ErrorResponse { Error = "Failed to calculate cache size" });
        }
        return Ok(result);
    }

    /// <summary>
    /// GET /api/cache/permissions - Check cache directory permissions
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("permissions")]
    public IActionResult GetDirectoryPermissions()
    {
        var cachePath = _pathResolver.GetCacheDirectory();
        var cacheWritable = _pathResolver.IsCacheDirectoryWritable();

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
        // CRITICAL: Check write permissions BEFORE starting the operation
        // This prevents operations from failing partway through due to permission issues
        var cacheWritable = _pathResolver.IsCacheDirectoryWritable();

        if (!cacheWritable)
        {
            var errorMessage = "Cannot clear cache: cache directory is read-only. " +
                "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
                $"The lancache container is configured to run as UID/GID {ContainerEnvironment.UidGid} (configured via PUID/PGID environment variables).";

            _logger.LogWarning("[ClearAllCache] Permission check failed: {Error}", errorMessage);
            return BadRequest(new ErrorResponse { Error = errorMessage });
        }

        // Central concurrency check — CacheClearing is global, blocks everything.
        var conflict = await _conflictChecker.CheckAsync(
            OperationType.CacheClearing,
            ConflictScope.Bulk(),
            cancellationToken);
        if (conflict != null)
        {
            return Conflict(conflict);
        }

        _cacheService.InvalidateCachedScan();

        var operationId = await _cacheClearingService.StartCacheClearAsync();

        if (operationId == null)
        {
            // Race: clearing began between our check and StartCacheClearAsync.
            var raceConflict = await _conflictChecker.CheckAsync(
                OperationType.CacheClearing,
                ConflictScope.Bulk(),
                cancellationToken);
            if (raceConflict != null)
            {
                return Conflict(raceConflict);
            }
            return Conflict(new OperationConflictResponse
            {
                Code = "OPERATION_CONFLICT",
                StageKey = "errors.conflict.duplicate",
                Error = "Cache clearing is already running"
            });
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
        // Get the datasource to check its specific permissions
        var datasourceService = HttpContext.RequestServices.GetRequiredService<DatasourceService>();
        var datasource = datasourceService.GetDatasources()
            .FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (datasource == null)
        {
            return NotFound(new NotFoundResponse { Error = $"Datasource '{name}' not found" });
        }

        // CRITICAL: Check write permissions BEFORE starting the operation
        // Use fresh check for the specific datasource's cache directory
        var cacheWritable = _pathResolver.IsDirectoryWritable(datasource.CachePath);

        if (!cacheWritable)
        {
            var errorMessage = $"Cannot clear cache for datasource '{name}': cache directory is read-only. " +
                "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
                $"The lancache container is configured to run as UID/GID {ContainerEnvironment.UidGid} (configured via PUID/PGID environment variables).";

            _logger.LogWarning("[ClearDatasourceCache] Permission check failed for {Datasource}: {Error}", name, errorMessage);
            return BadRequest(new ErrorResponse { Error = errorMessage });
        }

        // Central concurrency check — CacheClearing is global, blocks everything.
        var conflict = await _conflictChecker.CheckAsync(
            OperationType.CacheClearing,
            ConflictScope.Bulk(),
            cancellationToken);
        if (conflict != null)
        {
            return Conflict(conflict);
        }

        _cacheService.InvalidateCachedScan();

        var operationId = await _cacheClearingService.StartCacheClearAsync(name);

        if (operationId == null)
        {
            // Race: clearing began between our check and StartCacheClearAsync.
            var raceConflict = await _conflictChecker.CheckAsync(
                OperationType.CacheClearing,
                ConflictScope.Bulk(),
                cancellationToken);
            if (raceConflict != null)
            {
                return Conflict(raceConflict);
            }
            return Conflict(new OperationConflictResponse
            {
                Code = "OPERATION_CONFLICT",
                StageKey = "errors.conflict.duplicate",
                Error = "Cache clearing is already running"
            });
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
    /// DELETE /api/cache/operations/{id} - Cancel a running cache clear operation
    /// RESTful: DELETE is proper method for cancelling/removing operations
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("operations/{id}")]
    public IActionResult CancelCacheClear(Guid id)
    {
        var result = _cacheClearingService.CancelCacheClear(id);

        if (!result)
        {
            return NotFound(new NotFoundResponse { Error = "Cache clear operation not found or already completed", OperationId = id });
        }

        return Ok(new CacheOperationResponse { Message = "Cache clear operation cancelled successfully", OperationId = id });
    }

    /// <summary>
    /// POST /api/cache/operations/{id}/kill - Force kill a cache clear operation's process
    /// Used as fallback when graceful cancellation fails
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpPost("operations/{id}/kill")]
    public async Task<IActionResult> ForceKillCacheClearAsync(Guid id)
    {
        var result = await _cacheClearingService.ForceKillOperationAsync(id);

        if (!result)
        {
            return NotFound(new NotFoundResponse { Error = "Cache clear operation not found or no process to kill", OperationId = id });
        }

        return Ok(new CacheOperationResponse { Message = "Cache clear operation force killed successfully", OperationId = id });
    }

    /// <summary>
    /// GET /api/cache/corruption/cached - Get cached corruption detection results
    /// Returns immediately with cached results (if available) without running a new scan.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("corruption/cached")]
    public async Task<IActionResult> GetCachedCorruptionDetectionAsync()
    {
        var cachedResults = await _corruptionDetectionService.GetCachedDetectionAsync();

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
    public async Task<IActionResult> StartCorruptionDetectionAsync([FromQuery] int threshold = 3, [FromQuery] bool compareToCacheLogs = true, [FromQuery] string detectionMode = "miss_count")
    {
        var operationId = await _corruptionDetectionService.StartDetectionAsync(threshold, compareToCacheLogs, detectionMode);
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
    public async Task<IActionResult> GetCorruptionDetailsAsync(string service, [FromQuery] bool forceRefresh = false, [FromQuery] int threshold = 3, [FromQuery] bool compareToCacheLogs = true, [FromQuery] string detectionMode = "miss_count")
    {
        var detectRedownloads = detectionMode == "redownload";
        var details = await _cacheService.GetCorruptionDetailsAsync(service, forceRefresh, threshold, compareToCacheLogs, detectRedownloads: detectRedownloads);
        return Ok(details);
    }

    /// <summary>
    /// DELETE /api/cache/services/{name}/corruption - Remove corrupted chunks for specific service
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("services/{service}/corruption")]
    public async Task<IActionResult> RemoveCorruptedChunksAsync(string service, CancellationToken cancellationToken, [FromQuery] int threshold = 3, [FromQuery] bool compareToCacheLogs = true, [FromQuery] string detectionMode = "miss_count")
    {
        // Central concurrency check — service-scoped corruption removal.
        // Replaces the over-broad lock that blocked unrelated services.
        var conflict = await _conflictChecker.CheckAsync(
            OperationType.CorruptionRemoval,
            ConflictScope.Service(service),
            cancellationToken);
        if (conflict != null)
        {
            return Conflict(conflict);
        }

        _cacheService.InvalidateCachedScan();

        var datasources = _datasourceService.GetDatasources();

        // CRITICAL: Check write permissions BEFORE starting the operation for ALL datasources
        // This prevents the DB/filesystem state mismatch when PUID/PGID is wrong
        foreach (ResolvedDatasource datasource in datasources)
        {
            var cacheWritable = _pathResolver.IsDirectoryWritable(datasource.CachePath);
            var logsWritable = _pathResolver.IsDirectoryWritable(datasource.LogPath);

            if (!cacheWritable || !logsWritable)
            {
                var errors = new List<string>();
                if (!cacheWritable) errors.Add($"cache directory is read-only ({datasource.CachePath})");
                if (!logsWritable) errors.Add($"logs directory is read-only ({datasource.LogPath})");

                var errorMessage = $"Cannot remove corrupted chunks for datasource '{datasource.Name}': {string.Join(" and ", errors)}. " +
                    "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
                    $"The lancache container is configured to run as UID/GID {ContainerEnvironment.UidGid} (configured via PUID/PGID environment variables).";

                _logger.LogWarning("[CorruptionRemoval] Permission check failed for service {Service} on datasource {Datasource}: {Error}",
                    service, datasource.Name, errorMessage);

                return BadRequest(new ErrorResponse { Error = errorMessage });
            }
        }

        // Create CancellationTokenSource and register with unified operation tracker for cancel support
        var cts = new CancellationTokenSource();
        var metadata = new RemovalMetrics { EntityKey = service.ToLowerInvariant(), EntityName = service };
        var operationId = _operationTracker.RegisterOperation(
            OperationType.CorruptionRemoval,
            $"Corruption removal: {service}",
            cts,
            metadata);

        // Send start notification via SignalR
        _notifications.NotifyAllFireAndForget(SignalREvents.CorruptionRemovalStarted,
            new CorruptionRemovalStarted(
                service,
                operationId,
                "signalr.corruptionRemove.starting",
                DateTime.UtcNow,
                new Dictionary<string, object?> { ["service"] = service }));

        // Optimistically delete the cached detection row immediately so app restarts don't resurface stale data
        await _corruptionDetectionService.RemoveCachedServiceAsync(service);

        _ = Task.Run(async () =>
        {
            try
            {
                // Pause LiveLogMonitorService to prevent file locking issues
                await LiveLogMonitorService.PauseAsync();
                _logger.LogInformation("Paused LiveLogMonitorService for corruption removal");

                // Update tracking
                _operationTracker.UpdateProgress(operationId, 0, "signalr.corruptionRemove.starting");

                _logger.LogInformation("[CorruptionRemoval] Processing {Count} datasource(s) for service {Service}",
                    datasources.Count, service);

                try
                {
                    bool allSucceeded = true;
                    string? lastError = null;

                    foreach (ResolvedDatasource datasource in datasources)
                    {
                        cts.Token.ThrowIfCancellationRequested();

                        var logsPath = datasource.LogPath;
                        var cachePath = datasource.CachePath;
                        var progressFilePath = Path.Combine(_pathResolver.GetOperationsDirectory(),
                            $"corruption_removal_{operationId}_{datasource.Name}.json");

                        _logger.LogInformation("[CorruptionRemoval] Processing datasource '{Datasource}' (logs: {LogsPath}, cache: {CachePath})",
                            datasource.Name, logsPath, cachePath);

                        // Start progress monitoring task for this datasource
                        using var dsProgressCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                        var dsProgressToken = dsProgressCts.Token;

                        var progressMonitorTask = Task.Run(async () =>
                        {
                            try
                            {
                                while (!dsProgressToken.IsCancellationRequested)
                                {
                                    await Task.Delay(500, dsProgressToken);

                                    var progress = await _rustProcessHelper.ReadProgressFileAsync<CorruptionRemovalProgressData>(progressFilePath);
                                    if (progress != null)
                                    {
                                        _operationTracker.UpdateProgress(operationId, progress.PercentComplete, progress.StageKey ?? "");
                                        _operationTracker.UpdateMetadata(operationId, (object meta) =>
                                        {
                                            var m = (RemovalMetrics)meta;
                                            m.FilesProcessed = progress.FilesProcessed;
                                            m.TotalFiles = progress.TotalFiles;
                                        });

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
                                                progress.PercentComplete,
                                                progress.Context));
                                    }
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                // Expected when datasource processing completes or is cancelled
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Progress monitoring ended for corruption removal: {Service} datasource: {Datasource}",
                                    service, datasource.Name);
                            }
                        }, dsProgressToken);

                        try
                        {
                            var result = await _rustProcessHelper.RunCorruptionManagerAsync(
                                "remove",
                                logsPath,
                                cachePath,
                                service: service,
                                progressFile: progressFilePath,
                                cancellationToken: cts.Token,
                                threshold: threshold,
                                compareToCacheLogs: compareToCacheLogs,
                                detectRedownloads: detectionMode == "redownload"
                            );

                            // Stop progress monitoring for this datasource
                            await dsProgressCts.CancelAsync();
                            try { await progressMonitorTask; } catch { /* ignore cancellation */ }

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
                        await _cacheService.InvalidateServiceCountsCacheAsync();

                        _operationTracker.CompleteOperation(operationId, success: true);
                        await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                            new CorruptionRemovalComplete(true, service,
                                StageKey: "signalr.corruptionRemove.success",
                                OperationId: operationId,
                                Context: new Dictionary<string, object?> { ["service"] = service }));
                    }
                    else
                    {
                        _logger.LogError("Corruption removal failed for service {Service}: {Error}", service, lastError);
                        _operationTracker.CompleteOperation(operationId, success: false, error: lastError);
                        await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                            new CorruptionRemovalComplete(false, service,
                                StageKey: "signalr.corruptionRemove.failed.generic",
                                OperationId: operationId,
                                Error: lastError));
                    }
                }
                finally
                {
                    // Always resume LiveLogMonitorService
                    await LiveLogMonitorService.ResumeAsync();
                    _logger.LogInformation("Resumed LiveLogMonitorService after corruption removal");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Corruption removal cancelled for service: {Service}", service);
                _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
                await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                    new CorruptionRemovalComplete(false, service,
                        StageKey: "signalr.corruptionRemove.cancelled",
                        OperationId: operationId));

                // Resume LiveLogMonitorService on cancellation
                await LiveLogMonitorService.ResumeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during corruption removal for service: {Service}", service);
                _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
                await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                    new CorruptionRemovalComplete(false, service,
                        StageKey: "signalr.corruptionRemove.failed.generic",
                        OperationId: operationId));
            }
        });

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
    public async Task<IActionResult> RemoveAllCorruptedChunksAsync(CancellationToken cancellationToken, [FromQuery] int threshold = 3, [FromQuery] bool compareToCacheLogs = true, [FromQuery] string detectionMode = "miss_count")
    {
        // Query which services currently have corruption data
        var cachedDetection = await _corruptionDetectionService.GetCachedDetectionAsync();
        if (cachedDetection == null || cachedDetection.CorruptionCounts == null || cachedDetection.CorruptionCounts.Count == 0)
        {
            return Ok(new { Message = "No corruption data found. Run a corruption detection scan first." });
        }

        var servicesWithCorruption = cachedDetection.CorruptionCounts.Keys.ToList();

        // Per-service conflict check: if any service is blocked, return 409 with the blocking op.
        foreach (var svc in servicesWithCorruption)
        {
            var conflict = await _conflictChecker.CheckAsync(
                OperationType.CorruptionRemoval,
                ConflictScope.Service(svc),
                cancellationToken);
            if (conflict != null)
            {
                _logger.LogWarning("[CorruptionRemoval] All-services removal blocked: service '{Service}' conflicts with active {ActiveType}",
                    svc, conflict.ActiveOperationType);
                return Conflict(conflict);
            }
        }

        _logger.LogInformation("[CorruptionRemoval] Starting all-services corruption removal for {Count} service(s): {Services}",
            servicesWithCorruption.Count, string.Join(", ", servicesWithCorruption));

        _cacheService.InvalidateCachedScan();

        var datasources = _datasourceService.GetDatasources();

        // CRITICAL: Check write permissions BEFORE starting the operation for ALL datasources
        foreach (ResolvedDatasource datasource in datasources)
        {
            var cacheWritable = _pathResolver.IsDirectoryWritable(datasource.CachePath);
            var logsWritable = _pathResolver.IsDirectoryWritable(datasource.LogPath);

            if (!cacheWritable || !logsWritable)
            {
                var errors = new List<string>();
                if (!cacheWritable) errors.Add($"cache directory is read-only ({datasource.CachePath})");
                if (!logsWritable) errors.Add($"logs directory is read-only ({datasource.LogPath})");

                var errorMessage = $"Cannot remove corrupted chunks for datasource '{datasource.Name}': {string.Join(" and ", errors)}. " +
                    "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
                    $"The lancache container is configured to run as UID/GID {ContainerEnvironment.UidGid} (configured via PUID/PGID environment variables).";

                _logger.LogWarning("[CorruptionRemoval] Permission check failed on datasource {Datasource}: {Error}",
                    datasource.Name, errorMessage);

                return BadRequest(new ErrorResponse { Error = errorMessage });
            }
        }

        // Delete cached detection rows per-service and activate grace period to prevent immediate reappearance
        foreach (var service in servicesWithCorruption)
        {
            await _corruptionDetectionService.RemoveCachedServiceAsync(service);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Pause LiveLogMonitorService to prevent file locking issues across all services
                await LiveLogMonitorService.PauseAsync();
                _logger.LogInformation("Paused LiveLogMonitorService for all-services corruption removal");

                try
                {
                    foreach (var service in servicesWithCorruption)
                    {
                        // Register a fresh operation per service
                        var cts = new CancellationTokenSource();
                        var metadata = new RemovalMetrics { EntityKey = service.ToLowerInvariant(), EntityName = service };
                        var operationId = _operationTracker.RegisterOperation(
                            OperationType.CorruptionRemoval,
                            $"Corruption removal: {service}",
                            cts,
                            metadata);

                        // Send start notification via SignalR
                        _notifications.NotifyAllFireAndForget(SignalREvents.CorruptionRemovalStarted,
                            new CorruptionRemovalStarted(
                                service,
                                operationId,
                                "signalr.corruptionRemove.starting",
                                DateTime.UtcNow,
                                new Dictionary<string, object?> { ["service"] = service }));

                        _operationTracker.UpdateProgress(operationId, 0, "signalr.corruptionRemove.starting");

                        _logger.LogInformation("[CorruptionRemoval] Processing {Count} datasource(s) for service {Service}",
                            datasources.Count, service);

                        try
                        {
                            bool allSucceeded = true;
                            string? lastError = null;

                            foreach (ResolvedDatasource datasource in datasources)
                            {
                                cts.Token.ThrowIfCancellationRequested();

                                var logsPath = datasource.LogPath;
                                var cachePath = datasource.CachePath;
                                var progressFilePath = Path.Combine(_pathResolver.GetOperationsDirectory(),
                                    $"corruption_removal_{operationId}_{datasource.Name}.json");

                                _logger.LogInformation("[CorruptionRemoval] Processing datasource '{Datasource}' (logs: {LogsPath}, cache: {CachePath})",
                                    datasource.Name, logsPath, cachePath);

                                using var dsProgressCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                                var dsProgressToken = dsProgressCts.Token;

                                var progressMonitorTask = Task.Run(async () =>
                                {
                                    try
                                    {
                                        while (!dsProgressToken.IsCancellationRequested)
                                        {
                                            await Task.Delay(500, dsProgressToken);

                                            var progress = await _rustProcessHelper.ReadProgressFileAsync<CorruptionRemovalProgressData>(progressFilePath);
                                            if (progress != null)
                                            {
                                                _operationTracker.UpdateProgress(operationId, progress.PercentComplete, progress.StageKey ?? "");
                                                _operationTracker.UpdateMetadata(operationId, (object meta) =>
                                                {
                                                    var m = (RemovalMetrics)meta;
                                                    m.FilesProcessed = progress.FilesProcessed;
                                                    m.TotalFiles = progress.TotalFiles;
                                                });

                                                await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalProgress,
                                                    new CorruptionRemovalProgress(
                                                        service,
                                                        operationId,
                                                        progress.Status,
                                                        progress.StageKey ?? string.Empty,
                                                        DateTime.UtcNow,
                                                        progress.FilesProcessed,
                                                        progress.TotalFiles,
                                                        progress.PercentComplete,
                                                        progress.Context));
                                            }
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        // Expected when datasource processing completes or is cancelled
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogDebug(ex, "Progress monitoring ended for corruption removal: {Service} datasource: {Datasource}",
                                            service, datasource.Name);
                                    }
                                }, dsProgressToken);

                                try
                                {
                                    var result = await _rustProcessHelper.RunCorruptionManagerAsync(
                                        "remove",
                                        logsPath,
                                        cachePath,
                                        service: service,
                                        progressFile: progressFilePath,
                                        cancellationToken: cts.Token,
                                        threshold: threshold,
                                        compareToCacheLogs: compareToCacheLogs,
                                        detectRedownloads: detectionMode == "redownload"
                                    );

                                    await dsProgressCts.CancelAsync();
                                    try { await progressMonitorTask; } catch { /* ignore cancellation */ }

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
                                    try { if (System.IO.File.Exists(progressFilePath)) System.IO.File.Delete(progressFilePath); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to clean up progress file"); }
                                }
                            }

                            if (allSucceeded)
                            {
                                _logger.LogInformation("Corruption removal completed for service: {Service} across all datasources", service);

                                await _nginxLogRotationService.ReopenNginxLogsAsync();
                                await _cacheService.InvalidateServiceCountsCacheAsync();

                                _operationTracker.CompleteOperation(operationId, success: true);
                                await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                                    new CorruptionRemovalComplete(true, service,
                                        StageKey: "signalr.corruptionRemove.success",
                                        OperationId: operationId,
                                        Context: new Dictionary<string, object?> { ["service"] = service }));
                            }
                            else
                            {
                                _logger.LogError("Corruption removal failed for service {Service}: {Error}", service, lastError);
                                _operationTracker.CompleteOperation(operationId, success: false, error: lastError);
                                await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                                    new CorruptionRemovalComplete(false, service,
                                        StageKey: "signalr.corruptionRemove.failed.generic",
                                        OperationId: operationId,
                                        Error: lastError));
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogInformation("Corruption removal cancelled for service: {Service}", service);
                            _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
                            await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                                new CorruptionRemovalComplete(false, service,
                                    StageKey: "signalr.corruptionRemove.cancelled",
                                    OperationId: operationId));
                            // Stop processing further services on cancellation
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error during corruption removal for service: {Service}", service);
                            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
                            await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                                new CorruptionRemovalComplete(false, service,
                                    StageKey: "signalr.corruptionRemove.failed.generic",
                                    OperationId: operationId));
                        }
                    }
                }
                finally
                {
                    // Always resume LiveLogMonitorService
                    await LiveLogMonitorService.ResumeAsync();
                    _logger.LogInformation("Resumed LiveLogMonitorService after all-services corruption removal");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during all-services corruption removal");
                await LiveLogMonitorService.ResumeAsync();
            }
        });

        return Accepted(new { Message = "Corruption removal started for all services" });
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
    /// DELETE /api/cache/services/{name} - Remove specific service from cache
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("services/{name}")]
    public async Task<IActionResult> ClearServiceCacheAsync(string name, CancellationToken requestCt)
    {
        // CRITICAL: Check write permissions BEFORE starting the operation
        // This prevents operations from failing partway through due to permission issues
        var cacheWritable = _pathResolver.IsCacheDirectoryWritable();
        var logsWritable = _pathResolver.IsLogsDirectoryWritable();

        if (!cacheWritable || !logsWritable)
        {
            var errors = new List<string>();
            if (!cacheWritable) errors.Add("cache directory is read-only");
            if (!logsWritable) errors.Add("logs directory is read-only");

            var errorMessage = $"Cannot remove service from cache: {string.Join(" and ", errors)}. " +
                "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
                $"The lancache container is configured to run as UID/GID {ContainerEnvironment.UidGid} (configured via PUID/PGID environment variables).";

            _logger.LogWarning("[ClearServiceCache] Permission check failed for service {Service}: {Error}", name, errorMessage);
            return BadRequest(new ErrorResponse { Error = errorMessage });
        }

        // Central concurrency check.
        var conflict = await _conflictChecker.CheckAsync(
            OperationType.ServiceRemoval,
            ConflictScope.Service(name),
            requestCt);
        if (conflict != null)
        {
            return Conflict(conflict);
        }

        _cacheService.InvalidateCachedScan();

        _logger.LogInformation("Starting background service removal for: {Service}", name);

        var metadata = new RemovalMetrics { EntityKey = name.ToLowerInvariant(), EntityName = name };
        var operationId = await TrackedRemovalOperationRunner.StartAsync(
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
                BuildErrorCompletePayload: (id, _) => new ServiceRemovalComplete(
                    false,
                    name,
                    id,
                    "signalr.serviceRemove.failed.generic",
                    Context: new Dictionary<string, object?> { ["name"] = name }),
                ExecuteAsync: (ct, onProgress) => _cacheService.RemoveServiceFromCacheAsync(
                    name,
                    ct,
                    (percentComplete, stageKey, context, filesDeleted, bytesFreed) =>
                        onProgress(new TrackedRemovalOperationRunner.RemovalProgressUpdate(
                            percentComplete,
                            stageKey,
                            context,
                            filesDeleted,
                            bytesFreed))),
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
        var evictionOps = _operationTracker.GetActiveOperations(OperationType.EvictionRemoval);

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
    public async Task<IActionResult> RemoveEvictedForEntityAsync(string scope, [FromQuery] string? key, CancellationToken cancellationToken)
    {
        // Validate key parameter.
        if (string.IsNullOrWhiteSpace(key))
        {
            return BadRequest(new ErrorResponse { Error = "Query parameter 'key' is required and must not be empty." });
        }

        // Normalise and validate scope.
        var scopeLower = scope.ToLowerInvariant();
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

        // Permission check — mirror ClearServiceCacheAsync.
        var cacheWritable = _pathResolver.IsCacheDirectoryWritable();
        var logsWritable = _pathResolver.IsLogsDirectoryWritable();

        if (!cacheWritable || !logsWritable)
        {
            var errors = new List<string>();
            if (!cacheWritable) errors.Add("cache directory is read-only");
            if (!logsWritable) errors.Add("logs directory is read-only");

            var errorMessage = $"Cannot remove evicted data: {string.Join(" and ", errors)}. " +
                "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
                $"The lancache container is configured to run as UID/GID {ContainerEnvironment.UidGid} (configured via PUID/PGID environment variables).";

            _logger.LogWarning("[EvictedRemoval] Permission check failed for {Scope} '{Key}': {Error}", scope, key, errorMessage);
            return BadRequest(new ErrorResponse { Error = errorMessage });
        }

        // Central concurrency check — scope-aware (replaces the global eviction lock bug).
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
        if (conflict != null)
        {
            return Conflict(conflict);
        }

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

        var operationId = await _reconciliationService.StartScopedEvictionRemovalAsync(
            evictionScope,
            key,
            resolvedGameName,
            resolvedGameAppId,
            cancellationToken,
            resolvedEpicAppId: evictionScope == EvictionScope.Epic ? key : null);

        return Accepted(new { operationId, scope = scopeLower, key });
    }
}
