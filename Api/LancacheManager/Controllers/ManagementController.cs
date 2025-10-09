using LancacheManager.Security;
using LancacheManager.Services;
using Microsoft.AspNetCore.Mvc;

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
    private readonly RustDatabaseResetService _rustDatabaseResetService;

    public ManagementController(
        CacheManagementService cacheService,
        DatabaseService dbService,
        CacheClearingService cacheClearingService,
        IConfiguration configuration,
        ILogger<ManagementController> logger,
        IPathResolver pathResolver,
        StateService stateService,
        RustLogProcessorService rustLogProcessorService,
        RustDatabaseResetService rustDatabaseResetService)
    {
        _cacheService = cacheService;
        _dbService = dbService;
        _cacheClearingService = cacheClearingService;
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;
        _stateService = stateService;
        _rustLogProcessorService = rustLogProcessorService;
        _rustDatabaseResetService = rustDatabaseResetService;

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
    public async Task<IActionResult> ResetDatabase([FromQuery] bool useRust = true)
    {
        try
        {
            if (useRust)
            {
                if (_rustDatabaseResetService.IsProcessing)
                {
                    return BadRequest(new { error = "Database reset is already running" });
                }

                _logger.LogInformation("Starting rust database reset");

                // Start rust reset in background
                _ = Task.Run(async () => await _rustDatabaseResetService.StartResetAsync());

                return Ok(new {
                    message = "Database reset started with rust service",
                    status = "started",
                    timestamp = DateTime.UtcNow
                });
            }
            else
            {
                // Use C# implementation with SignalR updates
                await _dbService.ResetDatabase();
                _logger.LogInformation("Database reset completed");

                return Ok(new {
                    message = "Database reset successfully",
                    status = "completed",
                    timestamp = DateTime.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting database");
            return StatusCode(500, new { error = "Failed to reset database", details = ex.Message });
        }
    }

    [HttpGet("database/reset-status")]
    public IActionResult GetResetStatus()
    {
        try
        {
            var dataDirectory = _pathResolver.GetDataDirectory();
            var progressPath = Path.Combine(dataDirectory, "reset_progress.json");

            if (!System.IO.File.Exists(progressPath))
            {
                return Ok(new {
                    isProcessing = _rustDatabaseResetService.IsProcessing,
                    percentComplete = 0.0,
                    status = "idle",
                    message = "Not processing"
                });
            }

            var json = System.IO.File.ReadAllText(progressPath);
            var progress = System.Text.Json.JsonSerializer.Deserialize<RustDatabaseResetService.ProgressData>(json);

            return Ok(progress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting reset status");
            return Ok(new {
                isProcessing = _rustDatabaseResetService.IsProcessing,
                error = ex.Message
            });
        }
    }


    [HttpPost("reset-logs")]
    [RequireAuth]
    public async Task<IActionResult> ResetLogPosition([FromQuery] string position = "bottom", [FromQuery] bool clearDatabase = false)
    {
        try
        {
            var logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");
            long newPosition = 0;

            if (position.Equals("top", StringComparison.OrdinalIgnoreCase))
            {
                // Start from beginning (position 0)
                newPosition = 0;
                _logger.LogInformation("Log position reset to beginning of file");
            }
            else // "bottom" or default
            {
                // Start from end of file
                if (System.IO.File.Exists(logPath))
                {
                    var fileInfo = new FileInfo(logPath);
                    // Count total lines to set position at end
                    newPosition = System.IO.File.ReadLines(logPath).LongCount();
                    _logger.LogInformation("Log position reset to end of file (line {Position})", newPosition);
                }
                else
                {
                    _logger.LogWarning("Log file not found, setting position to 0");
                }
            }

            _stateService.SetLogPosition(newPosition);

            if (clearDatabase)
            {
                await _dbService.ResetDatabase();
            }

            var message = position.Equals("top", StringComparison.OrdinalIgnoreCase)
                ? "Log position reset to beginning. The rust service will process from the start with duplicate detection."
                : "Log position reset to end. Will monitor only new downloads going forward.";

            return Ok(new {
                message,
                position = newPosition,
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

            // If starting from position 0, always use 0 (beginning)
            // If starting from any other position, use that position but rust will start from 0 with duplicate detection
            if (startPosition == 0)
            {
                _logger.LogInformation("Starting rust log processing from beginning of file");
                _ = Task.Run(async () => await _rustLogProcessorService.StartProcessingAsync(logPath, 0));

                return Ok(new
                {
                    message = "Log processing started with rust service from beginning of file",
                    logSizeMB = sizeMB,
                    startPosition = 0,
                    status = "started"
                });
            }
            else
            {
                // User set position to end - start rust from 0 but it will only process new entries via duplicate detection
                _logger.LogInformation("Starting rust log processing (stored position: {Position}, rust will process from beginning with duplicate detection)", startPosition);
                _ = Task.Run(async () => await _rustLogProcessorService.StartProcessingAsync(logPath, 0));

                return Ok(new
                {
                    message = "Log processing started with rust service (will skip existing entries)",
                    logSizeMB = sizeMB,
                    startPosition = 0,
                    status = "started"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting log processor");
            return StatusCode(500, new { error = "Failed to start log processor", details = ex.Message });
        }
    }

    // REMOVED: cancel-processing endpoint moved to Program.cs as Minimal API
    // to avoid database locking issues when Rust process holds database lock



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

            // Use FileStream with FileShare.ReadWrite to allow other processes to access the file
            string json;
            using (var fileStream = new System.IO.FileStream(progressPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
            using (var reader = new System.IO.StreamReader(fileStream))
            {
                json = await reader.ReadToEndAsync();
            }

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

    [HttpGet("cache/thread-count")]
    public IActionResult GetCacheThreadCount()
    {
        try
        {
            var threadCount = _cacheClearingService.GetThreadCount();
            return Ok(new { threadCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache clear thread count");
            return StatusCode(500, new { error = "Failed to get thread count", details = ex.Message });
        }
    }

    [HttpPost("cache/thread-count")]
    [RequireAuth]
    public IActionResult SetCacheThreadCount([FromBody] SetThreadCountRequest request)
    {
        try
        {
            if (request.ThreadCount < 1 || request.ThreadCount > 16)
            {
                return BadRequest(new { error = "Thread count must be between 1 and 16" });
            }

            _cacheClearingService.SetThreadCount(request.ThreadCount);
            _logger.LogInformation("Cache clear thread count updated to {ThreadCount}", request.ThreadCount);

            return Ok(new
            {
                message = "Thread count updated successfully",
                threadCount = request.ThreadCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting cache clear thread count");
            return StatusCode(500, new { error = "Failed to set thread count", details = ex.Message });
        }
    }

    [HttpGet("cache/delete-mode")]
    public IActionResult GetCacheDeleteMode()
    {
        try
        {
            var deleteMode = _cacheClearingService.GetDeleteMode();
            return Ok(new { deleteMode });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache clear delete mode");
            return StatusCode(500, new { error = "Failed to get delete mode", details = ex.Message });
        }
    }

    [HttpPost("cache/delete-mode")]
    [RequireAuth]
    public IActionResult SetCacheDeleteMode([FromBody] SetDeleteModeRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.DeleteMode) ||
                (request.DeleteMode != "preserve" && request.DeleteMode != "full"))
            {
                return BadRequest(new { error = "Delete mode must be 'preserve' or 'full'" });
            }

            _cacheClearingService.SetDeleteMode(request.DeleteMode);
            _logger.LogInformation("Cache clear delete mode updated to {DeleteMode}", request.DeleteMode);

            return Ok(new
            {
                message = "Delete mode updated successfully",
                deleteMode = request.DeleteMode
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting cache clear delete mode");
            return StatusCode(500, new { error = "Failed to set delete mode", details = ex.Message });
        }
    }

    [HttpGet("system/cpu-count")]
    public IActionResult GetSystemCpuCount()
    {
        try
        {
            var cpuCount = _cacheClearingService.GetSystemCpuCount();
            return Ok(new { cpuCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system CPU count");
            return StatusCode(500, new { error = "Failed to get CPU count", details = ex.Message });
        }
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        try
        {
            var services = await _cacheService.GetServicesFromLogs();
            var cachePath = _cacheService.GetCachePath();

            // Get timezone from environment variable (docker-compose TZ) or default to UTC
            // Note: Don't use TimeZoneInfo.Local.Id as it returns Windows names like "Central Standard Time"
            // which JavaScript doesn't understand. Use IANA format like "America/Chicago" or "UTC"
            var timezone = Environment.GetEnvironmentVariable("TZ") ?? "UTC";

            return Ok(new {
                cachePath,
                logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log"),
                services,
                timezone
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting configuration");
            var timezone = Environment.GetEnvironmentVariable("TZ") ?? "UTC";

            return Ok(new {
                cachePath = _pathResolver.GetCacheDirectory(),
                logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log"),
                services = new[] { "steam", "epic", "origin", "blizzard", "wsus", "riot" },
                timezone
            });
        }
    }

    /// <summary>
    /// Mark setup as completed - called after successful data processing
    /// This flag persists and indicates the system has been fully initialized
    /// </summary>
    [HttpPost("mark-setup-completed")]
    [RequireAuth]
    public IActionResult MarkSetupCompleted()
    {
        try
        {
            _stateService.SetSetupCompleted(true);
            _logger.LogInformation("Setup marked as completed");

            return Ok(new {
                message = "Setup marked as completed",
                isCompleted = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking setup as completed");
            return StatusCode(500, new { error = "Failed to mark setup as completed", details = ex.Message });
        }
    }

    /// <summary>
    /// Check if setup has been completed
    /// </summary>
    [HttpGet("setup-status")]
    public IActionResult GetSetupStatus()
    {
        try
        {
            var isCompleted = _stateService.GetSetupCompleted();

            return Ok(new {
                isCompleted
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking setup status");
            return StatusCode(500, new { error = "Failed to check setup status", details = ex.Message });
        }
    }

    /// <summary>
    /// Manually trigger database cleanup to fix App 0 and bad image URLs
    /// </summary>
    [HttpPost("cleanup-database")]
    [RequireAuth]
    public async Task<IActionResult> CleanupDatabase()
    {
        try
        {
            _logger.LogInformation("Manual database cleanup triggered");

            var app0Count = 0;
            var imageUrlCount = 0;

            // Fix App 0 entries
            var app0Downloads = await _dbService.GetDownloadsWithApp0();
            app0Count = app0Downloads.Count;
            if (app0Count > 0)
            {
                await _dbService.MarkApp0DownloadsInactive();
                _logger.LogInformation($"Marked {app0Count} 'App 0' downloads as inactive");
            }

            // Fix bad image URLs
            var badImageUrls = await _dbService.GetDownloadsWithBadImageUrls();
            imageUrlCount = badImageUrls.Count;
            if (imageUrlCount > 0)
            {
                var updated = await _dbService.FixBadImageUrls();
                _logger.LogInformation($"Updated {updated} image URLs to working fallback CDNs");
            }

            return Ok(new
            {
                message = "Database cleanup completed",
                app0EntriesFixed = app0Count,
                imageUrlsFixed = imageUrlCount,
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during manual database cleanup");
            return StatusCode(500, new { error = "Failed to cleanup database", details = ex.Message });
        }
    }
}

// Request model for removing service
public class RemoveServiceRequest
{
    public string Service { get; set; } = string.Empty;
}

// Request model for setting thread count
public class SetThreadCountRequest
{
    public int ThreadCount { get; set; }
}

// Request model for setting delete mode
public class SetDeleteModeRequest
{
    public string DeleteMode { get; set; } = string.Empty;
}
