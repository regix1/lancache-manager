using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;
using LancacheManager.Security;
using LancacheManager.Utilities;
using System.Text.Json;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ManagementController : ControllerBase
{
    private readonly CacheManagementService _cacheService;
    private readonly DatabaseService _dbService;
    private readonly CacheClearingService _cacheClearingService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ManagementController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly StateService _stateService;
    private readonly RustLogProcessorService _rustLogProcessorService;

    public ManagementController(
        CacheManagementService cacheService,
        DatabaseService dbService,
        CacheClearingService cacheClearingService,
        IConfiguration configuration,
        ILogger<ManagementController> logger,
        IPathResolver pathResolver,
        StateService stateService,
        RustLogProcessorService rustLogProcessorService)
    {
        _cacheService = cacheService;
        _dbService = dbService;
        _cacheClearingService = cacheClearingService;
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;
        _stateService = stateService;
        _rustLogProcessorService = rustLogProcessorService;

        var dataDirectory = _pathResolver.GetDataDirectory();
        if (!Directory.Exists(dataDirectory))
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);
                _logger.LogInformation("Created data directory: {DataDirectory}", dataDirectory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create data directory");
            }
        }
    }

    [HttpGet("cache")]
    public IActionResult GetCacheInfo()
    {
        var info = _cacheService.GetCacheInfo();
        return Ok(info);
    }

    [HttpPost("cache/clear-all")]
    [RequireAuth]
    public async Task<IActionResult> ClearAllCache()
    {
        try
        {
            var operationId = await _cacheClearingService.StartCacheClearAsync();
            _logger.LogInformation("Started cache clear operation: {OperationId}", operationId);

            return Ok(new {
                message = "Cache clearing started in background",
                operationId,
                status = "running"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting cache clear operation");
            return StatusCode(500, new { error = "Failed to start cache clearing", details = ex.Message });
        }
    }

    [HttpGet("cache/clear-status/{operationId}")]
    public IActionResult GetClearStatus(string operationId)
    {
        var status = _cacheClearingService.GetOperationStatus(operationId);

        if (status == null)
        {
            return NotFound(new { error = "Operation not found" });
        }

        return Ok(status);
    }

    [HttpPost("cache/clear-cancel/{operationId}")]
    [RequireAuth]
    public IActionResult CancelClearOperation(string operationId)
    {
        var cancelled = _cacheClearingService.CancelOperation(operationId);

        if (!cancelled)
        {
            return NotFound(new { error = "Operation not found or already completed" });
        }

        return Ok(new { message = "Operation cancelled", operationId });
    }

    [HttpDelete("cache")]
    [RequireAuth]
    public async Task<IActionResult> ClearCache([FromQuery] string? service = null)
    {
        try
        {
            if (string.IsNullOrEmpty(service))
            {
                var operationId = await _cacheClearingService.StartCacheClearAsync();
                return Ok(new {
                    message = "Cache clearing started in background",
                    operationId,
                    status = "running"
                });
            }

            await _cacheService.RemoveServiceFromLogs(service);
            return Ok(new { message = $"Removed {service} entries from logs" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in clear cache operation");
            return StatusCode(500, new { error = "Failed to clear cache", details = ex.Message });
        }
    }

    [HttpDelete("database")]
    [RequireAuth]
    public async Task<IActionResult> ResetDatabase()
    {
        try
        {
            await _dbService.ResetDatabase();
            _logger.LogInformation("Database reset completed");

            return Ok(new {
                message = "Database reset successfully",
                status = "completed",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting database");
            return StatusCode(500, new { error = "Failed to reset database", details = ex.Message });
        }
    }


    [HttpPost("reset-logs")]
    [RequireAuth]
    public async Task<IActionResult> ResetLogPosition([FromQuery] bool clearDatabase = false)
    {
        try
        {
            _stateService.SetLogPosition(0);

            if (clearDatabase)
            {
                await _dbService.ResetDatabase();
            }

            _logger.LogInformation("Log position reset");

            return Ok(new {
                message = "Log position reset successfully. Will start monitoring from the current end of the log file.",
                requiresRestart = false,
                databaseCleared = clearDatabase
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting log position");
            return StatusCode(500, new { error = "Failed to reset log position", details = ex.Message });
        }
    }

    [HttpPost("process-all-logs")]
    [RequireAuth]
    public async Task<IActionResult> ProcessAllLogs()
    {
        try
        {
            if (_rustLogProcessorService.IsProcessing)
            {
                return BadRequest(new { error = "Log processing is already running" });
            }

            var logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");
            if (!System.IO.File.Exists(logPath))
            {
                return NotFound(new { error = $"Log file not found at: {logPath}" });
            }

            var fileInfo = new FileInfo(logPath);
            var sizeMB = fileInfo.Length / (1024.0 * 1024.0);
            var startPosition = _stateService.GetLogPosition();

            _logger.LogInformation("Starting Rust log processing from position {Position}", startPosition);

            // Start Rust processor in background
            _ = Task.Run(async () => await _rustLogProcessorService.StartProcessingAsync(logPath, startPosition));

            return Ok(new
            {
                message = "Log processing started with Rust processor",
                logSizeMB = sizeMB,
                startPosition = startPosition,
                status = "started"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting log processor");
            return StatusCode(500, new { error = "Failed to start log processor", details = ex.Message });
        }
    }

    [HttpPost("cancel-processing")]
    [RequireAuth]
    public async Task<IActionResult> CancelProcessing()
    {
        try
        {
            await _rustLogProcessorService.StopProcessingAsync();
            return Ok(new { message = "Log processing cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling processing");
            return StatusCode(500, new { error = "Failed to cancel processing", details = ex.Message });
        }
    }


    [HttpPost("post-process-depot-mappings")]
    [RequireAuth]
    public async Task<IActionResult> PostProcessDepotMappings()
    {
        try
        {
            _logger.LogInformation("Starting depot mapping post-processing");

            var mappingsProcessed = await _dbService.PostProcessDepotMappings();

            return Ok(new {
                message = $"Depot mapping post-processing completed. Processed {mappingsProcessed} downloads.",
                mappingsProcessed,
                status = "completed"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during depot mapping post-processing");
            return StatusCode(500, new { error = "Failed to post-process depot mappings", details = ex.Message });
        }
    }

    [HttpGet("processing-status")]
    public async Task<IActionResult> GetProcessingStatus()
    {
        try
        {
            var dataDirectory = _pathResolver.GetDataDirectory();
            var progressPath = Path.Combine(dataDirectory, "rust_progress.json");
            var logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");

            // Get log file size for MB calculations
            var logFileInfo = new FileInfo(logPath);
            var mbTotal = logFileInfo.Exists ? logFileInfo.Length / (1024.0 * 1024.0) : 0;

            if (!System.IO.File.Exists(progressPath))
            {
                return Ok(new {
                    isProcessing = _rustLogProcessorService.IsProcessing,
                    percentComplete = 0,
                    progress = 0,
                    status = "idle",
                    message = "Not processing",
                    mbProcessed = 0.0,
                    mbTotal = mbTotal,
                    entriesProcessed = 0,
                    entriesQueued = 0,
                    linesProcessed = 0
                });
            }

            var json = await System.IO.File.ReadAllTextAsync(progressPath);
            var rustProgress = System.Text.Json.JsonSerializer.Deserialize<RustLogProcessorService.ProgressData>(json);

            if (rustProgress == null)
            {
                return Ok(new {
                    isProcessing = _rustLogProcessorService.IsProcessing,
                    percentComplete = 0,
                    progress = 0,
                    status = "idle",
                    mbProcessed = 0.0,
                    mbTotal = mbTotal,
                    entriesProcessed = 0,
                    entriesQueued = 0,
                    linesProcessed = 0
                });
            }

            // Calculate MB processed based on percentage
            var mbProcessed = mbTotal * (rustProgress.PercentComplete / 100.0);

            return Ok(new {
                isProcessing = _rustLogProcessorService.IsProcessing,
                // Legacy field names for compatibility
                totalLines = rustProgress.TotalLines,
                linesParsed = rustProgress.LinesParsed,
                entriesSaved = rustProgress.EntriesSaved,
                // New field names that React expects
                percentComplete = rustProgress.PercentComplete,
                progress = rustProgress.PercentComplete,
                status = rustProgress.Status,
                message = rustProgress.Message,
                mbProcessed = Math.Round(mbProcessed, 1),
                mbTotal = Math.Round(mbTotal, 1),
                entriesProcessed = rustProgress.EntriesSaved,
                entriesQueued = rustProgress.EntriesSaved,  // Rust saves directly, no queue
                pendingEntries = 0, // Rust doesn't have a pending queue
                linesProcessed = rustProgress.LinesParsed
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting processing status");
            return Ok(new { isProcessing = _rustLogProcessorService.IsProcessing, error = ex.Message });
        }
    }

    [HttpPost("logs/remove-service")]
    [RequireAuth]
    public async Task<IActionResult> RemoveServiceFromLogs([FromBody] RemoveServiceRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.Service))
            {
                return BadRequest(new { error = "Service name is required" });
            }

            await _cacheService.RemoveServiceFromLogs(request.Service);

            return Ok(new {
                message = $"Successfully removed {request.Service} entries from log file",
                backupFile = $"{Path.Combine(_pathResolver.GetLogsDirectory(), "access.log")}.bak"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing service from logs: {Service}", request.Service);
            return StatusCode(500, new { error = "Failed to remove service from logs", details = ex.Message });
        }
    }

    [HttpGet("logs/service-counts")]
    public async Task<IActionResult> GetServiceLogCounts()
    {
        try
        {
            var counts = await _cacheService.GetServiceLogCounts();

            return Ok(counts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting service log counts");
            return StatusCode(500, new { error = "Failed to get service log counts", details = ex.Message });
        }
    }


    [HttpGet("cache/active-operations")]
    public IActionResult GetActiveClearOperations()
    {
        try
        {
            var operations = _cacheClearingService.GetAllOperations()
                .Where(op => op.Status == "Running" || op.Status == "Preparing")
                .ToList();

            return Ok(new
            {
                hasActive = operations.Any(),
                operations = operations
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active cache clear operations");
            return StatusCode(500, new { error = "Failed to get active cache clear operations", details = ex.Message });
        }
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        try
        {
            var services = await _cacheService.GetServicesFromLogs();
            var cachePath = _cacheService.GetCachePath();

            return Ok(new {
                cachePath,
                logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log"),
                services
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting configuration");
            return Ok(new {
                cachePath = _pathResolver.GetCacheDirectory(),
                logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log"),
                services = new[] { "steam", "epic", "origin", "blizzard", "wsus", "riot" }
            });
        }
    }


}

// Request model for removing service
public class RemoveServiceRequest
{
    public string Service { get; set; } = string.Empty;
}
