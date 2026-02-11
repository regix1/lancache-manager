using System.Text.Json.Serialization;
using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Models;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Security;
using LancacheManager.Hubs;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for cache management operations
/// Handles cache information, clearing, corruption detection, and service/game cache management
/// </summary>
[ApiController]
[Route("api/cache")]
[RequireGuestSession]
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
        DatasourceService datasourceService)
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
    }

    /// <summary>
    /// GET /api/cache - Get cache information (size, path, etc.)
    /// </summary>
    [HttpGet]
    public IActionResult GetCacheInfo()
    {
        var info = _cacheService.GetCacheInfo();
        return Ok(info);
    }

    /// <summary>
    /// GET /api/cache/size - Calculate cache size with deletion time estimates
    /// </summary>
    [HttpGet("size")]
    public async Task<IActionResult> GetCacheSize([FromQuery] string? datasource = null)
    {
        var rustBinaryPath = _pathResolver.GetRustCacheSizePath();

            if (!System.IO.File.Exists(rustBinaryPath))
            {
                return StatusCode(500, new ErrorResponse
                {
                    Error = "Cache size calculator not available",
                    Details = $"Rust binary not found at {rustBinaryPath}"
                });
            }

            // Determine cache path
            string cachePath;
            if (!string.IsNullOrEmpty(datasource))
            {
                // Get specific datasource's cache path
                var datasourceService = HttpContext.RequestServices.GetRequiredService<DatasourceService>();
                var ds = datasourceService.GetDatasources()
                    .FirstOrDefault(d => d.Name.Equals(datasource, StringComparison.OrdinalIgnoreCase));

                if (ds == null)
                {
                    return NotFound(new NotFoundResponse { Error = $"Datasource '{datasource}' not found" });
                }

                cachePath = ds.CachePath;
            }
            else
            {
                cachePath = _pathResolver.GetCacheDirectory();
            }

            if (!Directory.Exists(cachePath))
            {
                return Ok(new CacheSizeResponse
                {
                    TotalBytes = 0,
                    TotalFiles = 0,
                    TotalDirectories = 0,
                    HexDirectories = 0,
                    ScanDurationMs = 0,
                    FormattedSize = "0 bytes",
                    Timestamp = DateTime.UtcNow,
                    EstimatedDeletionTimes = new EstimatedDeletionTimes
                    {
                        PreserveSeconds = 0,
                        FullSeconds = 0,
                        RsyncSeconds = 0,
                        PreserveFormatted = "< 1 second",
                        FullFormatted = "< 1 second",
                        RsyncFormatted = "< 1 second"
                    }
                });
            }

            var operationsDir = _pathResolver.GetOperationsDirectory();
            var outputFile = Path.Combine(operationsDir, $"cache_size_{Guid.NewGuid()}.json");

            var startInfo = _rustProcessHelper.CreateProcessStartInfo(rustBinaryPath, $"\"{cachePath}\" \"{outputFile}\"");

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                return StatusCode(500, new ErrorResponse { Error = "Failed to start cache size calculation" });
            }

            // Read stdout and stderr while process runs
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            // Log the Rust binary output (includes filesystem detection and calibration info)
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogInformation("Cache size calculation output:\n{Output}", stderr);
            }
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                _logger.LogInformation("Cache size result JSON:\n{Json}", stdout);
            }

            if (process.ExitCode != 0)
            {
                _logger.LogError("Cache size calculation failed with exit code {ExitCode}: {Error}", process.ExitCode, stderr);
                return StatusCode(500, new ErrorResponse { Error = "Cache size calculation failed", Details = stderr });
            }

            // Read result
            var result = await _rustProcessHelper.ReadProgressFileAsync<CacheSizeResult>(outputFile);

            // Clean up temp file
            await _rustProcessHelper.DeleteTemporaryFileAsync(outputFile);

            if (result == null)
            {
                return StatusCode(500, new ErrorResponse { Error = "Failed to read cache size result" });
            }

            return Ok(new CacheSizeResponse
            {
                TotalBytes = (long)result.TotalBytes,
                TotalFiles = (long)result.TotalFiles,
                TotalDirectories = (long)result.TotalDirectories,
                HexDirectories = result.HexDirectories,
                ScanDurationMs = (long)result.ScanDurationMs,
                FormattedSize = result.FormattedSize,
                Timestamp = DateTime.UtcNow,
                EstimatedDeletionTimes = new EstimatedDeletionTimes
                {
                    PreserveSeconds = result.EstimatedDeletionTimes.PreserveSeconds,
                    FullSeconds = result.EstimatedDeletionTimes.FullSeconds,
                    RsyncSeconds = result.EstimatedDeletionTimes.RsyncSeconds,
                    PreserveFormatted = result.EstimatedDeletionTimes.PreserveFormatted,
                    FullFormatted = result.EstimatedDeletionTimes.FullFormatted,
                    RsyncFormatted = result.EstimatedDeletionTimes.RsyncFormatted
                }
            });
    }

    // Helper class for deserializing Rust cache size result
    private class CacheSizeResult
    {
        [JsonPropertyName("totalBytes")]
        public ulong TotalBytes { get; set; }

        [JsonPropertyName("totalFiles")]
        public ulong TotalFiles { get; set; }

        [JsonPropertyName("totalDirectories")]
        public ulong TotalDirectories { get; set; }

        [JsonPropertyName("hexDirectories")]
        public int HexDirectories { get; set; }

        [JsonPropertyName("scanDurationMs")]
        public ulong ScanDurationMs { get; set; }

        [JsonPropertyName("estimatedDeletionTimes")]
        public CacheSizeEstimates EstimatedDeletionTimes { get; set; } = new();

        [JsonPropertyName("formattedSize")]
        public string FormattedSize { get; set; } = string.Empty;
    }

    private class CacheSizeEstimates
    {
        [JsonPropertyName("preserveSeconds")]
        public double PreserveSeconds { get; set; }

        [JsonPropertyName("fullSeconds")]
        public double FullSeconds { get; set; }

        [JsonPropertyName("rsyncSeconds")]
        public double RsyncSeconds { get; set; }

        [JsonPropertyName("preserveFormatted")]
        public string PreserveFormatted { get; set; } = string.Empty;

        [JsonPropertyName("fullFormatted")]
        public string FullFormatted { get; set; } = string.Empty;

        [JsonPropertyName("rsyncFormatted")]
        public string RsyncFormatted { get; set; } = string.Empty;
    }

    /// <summary>
    /// GET /api/cache/permissions - Check cache directory permissions
    /// </summary>
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
    [HttpDelete]
    [RequireAuth]
    public async Task<IActionResult> ClearAllCache()
    {
        // CRITICAL: Check write permissions BEFORE starting the operation
        // This prevents operations from failing partway through due to permission issues
        var cacheWritable = _pathResolver.IsCacheDirectoryWritable();

        if (!cacheWritable)
        {
            var errorMessage = "Cannot clear cache: cache directory is read-only. " +
                "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
                "The lancache container usually runs as UID/GID 33:33 (www-data).";

            _logger.LogWarning("[ClearAllCache] Permission check failed: {Error}", errorMessage);
            return BadRequest(new ErrorResponse { Error = errorMessage });
        }

        var operationId = await _cacheClearingService.StartCacheClearAsync();

        if (operationId == null)
        {
            return Conflict(new ConflictResponse { Error = "Cache clearing is already running" });
        }

        _logger.LogInformation("Started cache clear operation for all datasources: {OperationId}", operationId);

        return Accepted(new CacheOperationResponse
        {
            Message = "Cache clearing started in background for all datasources",
            OperationId = operationId,
            Status = "running"
        });
    }

    /// <summary>
    /// DELETE /api/cache/datasources/{name} - Clear cache for a specific datasource
    /// RESTful: DELETE is proper method for clearing/removing resources
    /// </summary>
    [HttpDelete("datasources/{name}")]
    [RequireAuth]
    public async Task<IActionResult> ClearDatasourceCache(string name)
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
                "The lancache container usually runs as UID/GID 33:33 (www-data).";

            _logger.LogWarning("[ClearDatasourceCache] Permission check failed for {Datasource}: {Error}", name, errorMessage);
            return BadRequest(new ErrorResponse { Error = errorMessage });
        }

        var operationId = await _cacheClearingService.StartCacheClearAsync(name);

        if (operationId == null)
        {
            return Conflict(new ConflictResponse { Error = "Cache clearing is already running" });
        }

        _logger.LogInformation("Started cache clear operation for datasource {Datasource}: {OperationId}", name, operationId);

        return Accepted(new CacheOperationResponse
        {
            Message = $"Cache clearing started for datasource: {name}",
            OperationId = operationId,
            Status = "running"
        });
    }

    /// <summary>
    /// GET /api/cache/operations - List all active cache operations
    /// </summary>
    [HttpGet("operations")]
    public IActionResult GetActiveOperations()
    {
        var operations = _cacheClearingService.GetActiveOperations();
        var isProcessing = operations.Any(op => op.Status != "completed" && op.Status != "failed" && op.Status != "cancelled");
        return Ok(new ActiveOperationsResponse { IsProcessing = isProcessing, Operations = operations });
    }

    /// <summary>
    /// GET /api/cache/operations/{id}/status - Get status of specific cache clear operation
    /// </summary>
    [HttpGet("operations/{id}/status")]
    public IActionResult GetCacheClearStatus(string id)
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
    [HttpDelete("operations/{id}")]
    [RequireAuth]
    public IActionResult CancelCacheClear(string id)
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
    [HttpPost("operations/{id}/kill")]
    [RequireAuth]
    public async Task<IActionResult> ForceKillCacheClear(string id)
    {
        var result = await _cacheClearingService.ForceKillOperation(id);

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
    [HttpGet("corruption/cached")]
    public async Task<IActionResult> GetCachedCorruptionDetection()
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
    [HttpPost("corruption/detect")]
    [RequireAuth]
    public async Task<IActionResult> StartCorruptionDetection([FromQuery] int threshold = 3, [FromQuery] bool compareToCacheLogs = true)
    {
        var operationId = await _corruptionDetectionService.StartDetectionAsync(threshold, compareToCacheLogs);
        return Accepted(new { operationId, message = "Corruption detection started", status = "running" });
    }

    /// <summary>
    /// GET /api/cache/corruption/detect/status - Get the status of the active corruption detection operation
    /// </summary>
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
    [HttpGet("services/{service}/corruption")]
    public async Task<IActionResult> GetCorruptionDetails(string service, [FromQuery] bool forceRefresh = false, [FromQuery] int threshold = 3, [FromQuery] bool compareToCacheLogs = true)
    {
        var details = await _cacheService.GetCorruptionDetails(service, forceRefresh, threshold, compareToCacheLogs);
        return Ok(details);
    }

    /// <summary>
    /// DELETE /api/cache/services/{name}/corruption - Remove corrupted chunks for specific service
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [HttpDelete("services/{service}/corruption")]
    [RequireAuth]
    public IActionResult RemoveCorruptedChunks(string service, [FromQuery] int threshold = 3, [FromQuery] bool compareToCacheLogs = true)
    {
        // Check if ANY removal operation is already in progress (they share a lock)
        var activeGameOps = _operationTracker.GetActiveOperations(OperationType.GameRemoval);
        var activeServiceOps = _operationTracker.GetActiveOperations(OperationType.ServiceRemoval);
        var activeCorruptionOps = _operationTracker.GetActiveOperations(OperationType.CorruptionRemoval);
        if (activeGameOps.Any() || activeServiceOps.Any() || activeCorruptionOps.Any())
        {
            var activeType = activeGameOps.Any() ? "game" :
                             activeServiceOps.Any() ? "service" :
                             activeCorruptionOps.Any() ? "corruption" : "unknown";
            _logger.LogWarning("[CorruptionRemoval] Blocked - another {Type} removal is already in progress", activeType);
            return Conflict(new ErrorResponse { Error = $"Another removal operation ({activeType}) is already in progress. Please wait for it to complete." });
        }

        var datasources = _datasourceService.GetDatasources();
        var dbPath = _pathResolver.GetDatabasePath();

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
                    "The lancache container usually runs as UID/GID 33:33 (www-data).";

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
            new CorruptionRemovalStarted(service, operationId, $"Starting corruption removal for {service}...", DateTime.UtcNow));

        _ = Task.Run(async () =>
        {
            try
            {
                // Pause LiveLogMonitorService to prevent file locking issues
                await LiveLogMonitorService.PauseAsync();
                _logger.LogInformation("Paused LiveLogMonitorService for corruption removal");

                // Update tracking
                _operationTracker.UpdateProgress(operationId, 0, $"Removing corrupted chunks for {service}...");

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
                                        _operationTracker.UpdateProgress(operationId, progress.PercentComplete, progress.Message);
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
                                                progress.Message,
                                                DateTime.UtcNow,
                                                progress.FilesProcessed,
                                                progress.TotalFiles,
                                                progress.PercentComplete));
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
                                databasePath: dbPath,
                                cancellationToken: cts.Token,
                                threshold: threshold,
                                compareToCacheLogs: compareToCacheLogs
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
                            try { if (System.IO.File.Exists(progressFilePath)) System.IO.File.Delete(progressFilePath); } catch { }
                        }
                    }

                    if (allSucceeded)
                    {
                        _logger.LogInformation("Corruption removal completed for service: {Service} across all datasources", service);

                        // Signal nginx to reopen log files (prevents monolithic container from losing log access)
                        await _nginxLogRotationService.ReopenNginxLogsAsync();

                        // Clear cached detection result so page reload doesn't show stale data
                        await _corruptionDetectionService.RemoveCachedServiceAsync(service);

                        // Invalidate service count cache since corruption removal affects counts
                        await _cacheService.InvalidateServiceCountsCache();

                        _operationTracker.CompleteOperation(operationId, success: true);
                        await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                            new CorruptionRemovalComplete(true, service, operationId, $"Successfully removed corrupted chunks for {service}"));
                    }
                    else
                    {
                        _logger.LogError("Corruption removal failed for service {Service}: {Error}", service, lastError);
                        _operationTracker.CompleteOperation(operationId, success: false, error: lastError);
                        await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                            new CorruptionRemovalComplete(false, service, operationId, Error: lastError));
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
                    new CorruptionRemovalComplete(false, service, operationId, Error: "Operation was cancelled."));

                // Resume LiveLogMonitorService on cancellation
                await LiveLogMonitorService.ResumeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during corruption removal for service: {Service}", service);
                _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
                await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                    new CorruptionRemovalComplete(false, service, operationId, Error: "Operation failed. Check server logs for details."));
            }
        });

        return Accepted(new CacheOperationResponse
        {
            Message = $"Started corruption removal for service: {service}",
            Service = service,
            OperationId = operationId,
            Status = "running"
        });
    }

    /// <summary>
    /// GET /api/cache/services/{service}/corruption/status - Get corruption removal status
    /// Used for restoring progress on page refresh
    /// </summary>
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
    [HttpDelete("services/{name}")]
    [RequireAuth]
    public IActionResult ClearServiceCache(string name)
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
                "The lancache container usually runs as UID/GID 33:33 (www-data).";

            _logger.LogWarning("[ClearServiceCache] Permission check failed for service {Service}: {Error}", name, errorMessage);
            return BadRequest(new ErrorResponse { Error = errorMessage });
        }

        _logger.LogInformation("Starting background service removal for: {Service}", name);

        // Create CancellationTokenSource and register with unified operation tracker for cancel support
        var cts = new CancellationTokenSource();
        var metadata = new RemovalMetrics { EntityKey = name.ToLowerInvariant(), EntityName = name };
        var operationId = _operationTracker.RegisterOperation(
            OperationType.ServiceRemoval,
            $"Service removal: {name}",
            cts,
            metadata);

        // Send start notification via SignalR
        _notifications.NotifyAllFireAndForget(SignalREvents.ServiceRemovalStarted,
            new ServiceRemovalStarted(name, operationId, $"Starting removal of {name}...", DateTime.UtcNow));

        // Fire-and-forget background removal with SignalR notification
        _ = Task.Run(async () =>
        {
            try
            {
                // Send starting notification
                await _notifications.NotifyAllAsync(SignalREvents.ServiceRemovalProgress,
                    new ServiceRemovalProgress(name, operationId, "starting", $"Starting removal of {name}..."));
                _operationTracker.UpdateProgress(operationId, 0, $"Starting removal of {name}...");

                // Use CacheManagementService which actually deletes files via Rust binary
                var report = await _cacheService.RemoveServiceFromCache(name, cts.Token,
                    async (percentComplete, message, filesDeleted, bytesFreed) =>
                    {
                        await _notifications.NotifyAllAsync(SignalREvents.ServiceRemovalProgress,
                            new ServiceRemovalProgress(name, operationId, "removing_cache", message, percentComplete,
                                filesDeleted > 0 ? filesDeleted : null, bytesFreed > 0 ? bytesFreed : null));
                        _operationTracker.UpdateProgress(operationId, percentComplete, message);
                        _operationTracker.UpdateMetadata(operationId, (object meta) =>
                        {
                            var m = (RemovalMetrics)meta;
                            if (filesDeleted > 0) m.FilesDeleted = filesDeleted;
                            if (bytesFreed > 0) m.BytesFreed = bytesFreed;
                        });
                    });

                // Send progress update
                await _notifications.NotifyAllAsync(SignalREvents.ServiceRemovalProgress,
                    new ServiceRemovalProgress(name, operationId, "complete", "Finalizing removal...", 100.0, report.CacheFilesDeleted, (long)report.TotalBytesFreed));
                _operationTracker.UpdateProgress(operationId, 100.0, "Finalizing removal...");
                _operationTracker.UpdateMetadata(operationId, (object meta) =>
                {
                    var m = (RemovalMetrics)meta;
                    m.FilesDeleted = report.CacheFilesDeleted;
                    m.BytesFreed = (long)report.TotalBytesFreed;
                });

                // Also remove from detection cache so it doesn't show in UI
                await _gameCacheDetectionService.RemoveServiceFromCacheAsync(name);

                _logger.LogInformation("Service removal completed for: {Service} - Deleted {Files} files, freed {Bytes} bytes",
                    name, report.CacheFilesDeleted, report.TotalBytesFreed);

                // Complete tracking
                _operationTracker.CompleteOperation(operationId, success: true);

                // Send SignalR notification on success
                await _notifications.NotifyAllAsync(SignalREvents.ServiceRemovalComplete,
                    new ServiceRemovalComplete(true, name, operationId, $"Successfully removed {name} service from cache", report.CacheFilesDeleted, (long)report.TotalBytesFreed, report.LogEntriesRemoved));
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Service removal cancelled for: {Service}", name);
                _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");

                await _notifications.NotifyAllAsync(SignalREvents.ServiceRemovalComplete,
                    new ServiceRemovalComplete(false, name, operationId, $"Service removal for {name} was cancelled."));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during service removal for: {Service}", name);

                // Send error status notification
                await _notifications.NotifyAllAsync(SignalREvents.ServiceRemovalProgress,
                    new ServiceRemovalProgress(name, operationId, "error", $"Error removing {name}: {ex.Message}", 0));

                // Complete tracking with error
                _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);

                // Send SignalR notification on failure
                await _notifications.NotifyAllAsync(SignalREvents.ServiceRemovalComplete,
                    new ServiceRemovalComplete(false, name, operationId, $"Failed to remove {name} service. Check server logs for details."));
            }
        });

        return Accepted(new CacheOperationResponse
        {
            Message = $"Started removal of {name} service from cache",
            OperationId = operationId,
            ServiceName = name,
            Status = "running"
        });
    }

    /// <summary>
    /// GET /api/cache/services/{name}/removal-status - Get service removal status
    /// Used for restoring progress on page refresh
    /// </summary>
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
    [HttpGet("removals/active")]
    public IActionResult GetAllActiveRemovals()
    {
        var gameOps = _operationTracker.GetActiveOperations(OperationType.GameRemoval);
        var serviceOps = _operationTracker.GetActiveOperations(OperationType.ServiceRemoval);
        var corruptionOps = _operationTracker.GetActiveOperations(OperationType.CorruptionRemoval);

        return Ok(new AllActiveRemovalsResponse
        {
            IsProcessing = gameOps.Any() || serviceOps.Any() || corruptionOps.Any(),
            GameRemovals = gameOps.Select(op =>
            {
                var metrics = op.Metadata as RemovalMetrics;
                return new GameRemovalInfo
                {
                    GameAppId = int.TryParse(metrics?.EntityKey, out var appId) ? appId : 0,
                    GameName = metrics?.EntityName ?? op.Name,
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
            })
        });
    }
}
