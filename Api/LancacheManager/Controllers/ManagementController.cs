using LancacheManager.Application.Services;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Security;
using LancacheManager.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Controllers;

/// <summary>
/// LEGACY CONTROLLER - Consider using specialized controllers instead:
/// - /api/cache for cache operations
/// - /api/database for database operations
/// - /api/logs for log processing
/// - /api/steam-auth for Steam authentication
/// - /api/depots for depot mapping operations
/// - /api/games for game detection
///
/// This controller is maintained for backward compatibility but contains
/// duplicate functionality that exists in the specialized controllers above.
/// </summary>
[ApiController]
[Route("api/management")]
public class ManagementController : ControllerBase
{
    private readonly CacheManagementService _cacheService;
    private readonly DatabaseRepository _dbService;
    private readonly CacheClearingService _cacheClearingService;
    private readonly GameCacheDetectionService _gameCacheDetectionService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ManagementController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly StateRepository _stateService;
    private readonly RustLogProcessorService _rustLogProcessorService;
    private readonly RustDatabaseResetService _rustDatabaseResetService;
    private readonly RustLogRemovalService _rustLogRemovalService;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly SteamAuthRepository _steamAuthStorage;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly LancacheManager.Services.SessionMigrationService _sessionMigrationService;

    public ManagementController(
        CacheManagementService cacheService,
        DatabaseRepository dbService,
        CacheClearingService cacheClearingService,
        GameCacheDetectionService gameCacheDetectionService,
        IConfiguration configuration,
        ILogger<ManagementController> logger,
        IPathResolver pathResolver,
        StateRepository stateService,
        RustLogProcessorService rustLogProcessorService,
        RustDatabaseResetService rustDatabaseResetService,
        RustLogRemovalService rustLogRemovalService,
        SteamKit2Service steamKit2Service,
        SteamAuthRepository steamAuthStorage,
        IServiceScopeFactory serviceScopeFactory,
        IHubContext<DownloadHub> hubContext,
        RustProcessHelper rustProcessHelper,
        LancacheManager.Services.SessionMigrationService sessionMigrationService)
    {
        _cacheService = cacheService;
        _dbService = dbService;
        _cacheClearingService = cacheClearingService;
        _gameCacheDetectionService = gameCacheDetectionService;
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;
        _stateService = stateService;
        _rustLogProcessorService = rustLogProcessorService;
        _rustDatabaseResetService = rustDatabaseResetService;
        _rustLogRemovalService = rustLogRemovalService;
        _steamKit2Service = steamKit2Service;
        _steamAuthStorage = steamAuthStorage;
        _serviceScopeFactory = serviceScopeFactory;
        _hubContext = hubContext;
        _rustProcessHelper = rustProcessHelper;
        _sessionMigrationService = sessionMigrationService;

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

    [HttpGet("directory-permissions")]
    public IActionResult GetDirectoryPermissions()
    {
        try
        {
            var cachePath = _pathResolver.GetCacheDirectory();
            var logPath = _pathResolver.GetLogsDirectory();

            var cacheWritable = _pathResolver.IsCacheDirectoryWritable();
            var logsWritable = _pathResolver.IsLogsDirectoryWritable();

            return Ok(new
            {
                cache = new
                {
                    path = cachePath,
                    writable = cacheWritable,
                    readOnly = !cacheWritable
                },
                logs = new
                {
                    path = logPath,
                    writable = logsWritable,
                    readOnly = !logsWritable
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking directory permissions");
            return StatusCode(500, new { error = "Failed to check directory permissions", details = ex.Message });
        }
    }

    [HttpPost("cache/clear-all")]
    [RequireAuth]
    public async Task<IActionResult> ClearAllCache()
    {
        try
        {
            var operationId = await _cacheClearingService.StartCacheClearAsync();
            _logger.LogInformation("Started cache clear operation: {OperationId}", operationId);

            return Ok(new
            {
                message = "Cache clearing started in background",
                operationId,
                status = "running"
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            // This is an expected error (read-only directories), already logged as warning in service layer
            return StatusCode(403, new { error = ex.Message });
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

    [HttpGet("cache/active-operations")]
    public IActionResult GetActiveCacheOperations()
    {
        try
        {
            // Return list of active cache clearing operations for recovery on page reload
            var activeOperations = _cacheClearingService.GetAllOperations()
                .Where(op => op.Status == "Preparing" || op.Status == "Running")
                .ToList();

            return Ok(new
            {
                hasActiveOperations = activeOperations.Any(),
                operations = activeOperations
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active cache operations");
            return Ok(new { hasActiveOperations = false, operations = new List<object>() });
        }
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
                return Ok(new
                {
                    message = "Cache clearing started in background",
                    operationId,
                    status = "running"
                });
            }

            await _cacheService.RemoveServiceFromLogs(service);
            return Ok(new { message = $"Removed {service} entries from logs" });
        }
        catch (UnauthorizedAccessException ex)
        {
            // This is an expected error (read-only directories), already logged as warning in service layer
            return StatusCode(403, new { error = ex.Message });
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

                return Ok(new
                {
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

                return Ok(new
                {
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


    [HttpPost("database/reset-selected")]
    [RequireAuth]
    public IActionResult ResetSelectedTables([FromBody] List<string> tableNames)
    {
        try
        {
            if (tableNames == null || !tableNames.Any())
            {
                return BadRequest(new { error = "No tables selected" });
            }

            _logger.LogInformation($"Starting background selective database reset for tables: {string.Join(", ", tableNames)}");

            // Start background reset operation and return immediately
            var operationId = _dbService.StartResetSelectedTablesAsync(tableNames);

            // Return 202 Accepted - operation is running in background, progress sent via SignalR
            return Accepted(new
            {
                message = $"Database reset started for {tableNames.Count} table(s)",
                tables = tableNames,
                operationId,
                status = "started",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting selected tables");
            return StatusCode(500, new { error = "Failed to reset selected tables", details = ex.Message });
        }
    }

    [HttpGet("database/reset-status")]
    public IActionResult GetDatabaseResetStatus()
    {
        try
        {
            // Return database reset status for recovery on page reload
            var isProcessing = _rustDatabaseResetService.IsProcessing;

            return Ok(new
            {
                isProcessing,
                status = isProcessing ? "processing" : "idle"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting database reset status");
            return Ok(new { isProcessing = false, status = "idle" });
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

            return Ok(new
            {
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
    public Task<IActionResult> ProcessAllLogs()
    {
        try
        {
            if (_rustLogProcessorService.IsProcessing)
            {
                return Task.FromResult<IActionResult>(BadRequest(new { error = "Log processing is already running" }));
            }

            var logDir = _pathResolver.GetLogsDirectory();
            var logPath = Path.Combine(logDir, "access.log");

            // Check if log directory exists and has any log files
            if (!Directory.Exists(logDir))
            {
                return Task.FromResult<IActionResult>(NotFound(new { error = $"Log directory not found at: {logDir}" }));
            }

            // Check for any log files (access.log, .1, .2, .gz, etc.)
            var hasLogFiles = Directory.GetFiles(logDir, "access.log*").Any();
            if (!hasLogFiles)
            {
                return Task.FromResult<IActionResult>(NotFound(new {
                    error = $"No log files found in {logDir}. Please ensure your lancache is configured to write access logs."
                }));
            }

            // Calculate total size of all log files for display
            var allLogFiles = Directory.GetFiles(logDir, "access.log*");
            var totalBytes = allLogFiles.Sum(f => new FileInfo(f).Length);
            var sizeMB = totalBytes / (1024.0 * 1024.0);
            var startPosition = _stateService.GetLogPosition();

            // If starting from position 0, always use 0 (beginning)
            // If starting from any other position, use that position but rust will start from 0 with duplicate detection
            if (startPosition == 0)
            {
                _logger.LogInformation("Starting rust log processing from beginning of all log files");
                _ = Task.Run(async () => await _rustLogProcessorService.StartProcessingAsync(logDir, 0));

                return Task.FromResult<IActionResult>(Ok(new
                {
                    message = "Log processing started with rust service from beginning of file",
                    logSizeMB = sizeMB,
                    startPosition = 0,
                    status = "started"
                }));
            }
            else
            {
                // User set position to end - start rust from 0 but it will only process new entries via duplicate detection
                _logger.LogInformation("Starting rust log processing (stored position: {Position}, rust will process from beginning with duplicate detection)", startPosition);
                _ = Task.Run(async () => await _rustLogProcessorService.StartProcessingAsync(logDir, 0));

                return Task.FromResult<IActionResult>(Ok(new
                {
                    message = "Log processing started with rust service (will skip existing entries)",
                    logSizeMB = sizeMB,
                    startPosition = 0,
                    status = "started"
                }));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting log processor");
            return Task.FromResult<IActionResult>(StatusCode(500, new { error = "Failed to start log processor", details = ex.Message }));
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
                return Ok(new
                {
                    isProcessing = _rustLogProcessorService.IsProcessing,
                    percentComplete = 0,
                    progress = 0,
                    status = "idle",
                    message = "Not processing",
                    mbProcessed = 0.0,
                    mbTotal,
                    entriesProcessed = 0,
                    entriesQueued = 0,
                    linesProcessed = 0
                });
            }

            // Read progress file using helper (handles FileShare.ReadWrite and deserialization)
            var rustProgress = await _rustProcessHelper.ReadProgressFileAsync<RustLogProcessorService.ProgressData>(progressPath);

            if (rustProgress == null)
            {
                return Ok(new
                {
                    isProcessing = _rustLogProcessorService.IsProcessing,
                    percentComplete = 0,
                    progress = 0,
                    status = "idle",
                    mbProcessed = 0.0,
                    mbTotal,
                    entriesProcessed = 0,
                    entriesQueued = 0,
                    linesProcessed = 0
                });
            }

            // Calculate MB processed based on percentage
            var mbProcessed = mbTotal * (rustProgress.PercentComplete / 100.0);

            return Ok(new
            {
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
    public Task<IActionResult> RemoveServiceFromLogs([FromBody] RemoveServiceRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.Service))
            {
                return Task.FromResult<IActionResult>(BadRequest(new { error = "Service name is required" }));
            }

            if (_rustLogRemovalService.IsProcessing)
            {
                return Task.FromResult<IActionResult>(BadRequest(new { error = $"Log removal is already in progress for service: {_rustLogRemovalService.CurrentService}" }));
            }

            // Start background removal process
            _logger.LogInformation("Starting log removal for service: {Service}", request.Service);
            _ = Task.Run(async () => await _rustLogRemovalService.StartRemovalAsync(request.Service));

            return Task.FromResult<IActionResult>(Ok(new
            {
                message = $"Log removal started for {request.Service}",
                service = request.Service,
                status = "started"
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting log removal for service: {Service}", request.Service);
            return Task.FromResult<IActionResult>(StatusCode(500, new { error = "Failed to start log removal", details = ex.Message }));
        }
    }

    [HttpGet("logs/remove-status")]
    public async Task<IActionResult> GetLogRemovalStatus()
    {
        try
        {
            var progress = await _rustLogRemovalService.GetProgressAsync();

            if (!_rustLogRemovalService.IsProcessing && progress == null)
            {
                return Ok(new
                {
                    isProcessing = false,
                    message = "No log removal in progress"
                });
            }

            return Ok(new
            {
                isProcessing = _rustLogRemovalService.IsProcessing,
                service = _rustLogRemovalService.CurrentService,
                filesProcessed = progress?.FilesProcessed ?? 0,
                linesProcessed = progress?.LinesProcessed ?? 0,
                linesRemoved = progress?.LinesRemoved ?? 0,
                percentComplete = progress?.PercentComplete ?? 0.0,
                status = progress?.Status ?? "idle",
                message = progress?.Message ?? ""
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting log removal status");
            return Ok(new { isProcessing = _rustLogRemovalService.IsProcessing, error = ex.Message });
        }
    }

    [HttpGet("logs/service-counts")]
    public async Task<IActionResult> GetServiceLogCounts([FromQuery] bool forceRefresh = false)
    {
        try
        {
            var counts = await _cacheService.GetServiceLogCounts(forceRefresh);

            return Ok(counts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting service log counts");
            return StatusCode(500, new { error = "Failed to get service log counts", details = ex.Message });
        }
    }

    [HttpGet("database/log-entries-count")]
    public async Task<IActionResult> GetDatabaseLogEntriesCount()
    {
        try
        {
            var count = await _dbService.GetLogEntriesCount();
            return Ok(new { count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting database log entries count");
            return StatusCode(500, new { error = "Failed to get log entries count", details = ex.Message });
        }
    }

    [HttpGet("corruption/summary")]
    public async Task<IActionResult> GetCorruptionSummary([FromQuery] bool forceRefresh = false)
    {
        try
        {
            var summary = await _cacheService.GetCorruptionSummary(forceRefresh);
            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting corruption summary");
            return StatusCode(500, new { error = "Failed to get corruption summary", details = ex.Message });
        }
    }

    [HttpPost("corruption/remove")]
    [RequireAuth]
    public async Task<IActionResult> RemoveCorruptedChunks([FromBody] RemoveCorruptionRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.Service))
            {
                return BadRequest(new { error = "Service name is required" });
            }

            await _cacheService.RemoveCorruptedChunks(request.Service);

            return Ok(new
            {
                message = $"Successfully removed corrupted chunks for {request.Service}",
                service = request.Service
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            // This is an expected error (read-only directories), already logged as warning in service layer
            return StatusCode(403, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing corrupted chunks: {Service}", request.Service);
            return StatusCode(500, new { error = "Failed to remove corrupted chunks", details = ex.Message });
        }
    }

    [HttpGet("corruption/details/{service}")]
    public async Task<IActionResult> GetCorruptionDetails(string service, [FromQuery] bool forceRefresh = false)
    {
        try
        {
            var details = await _cacheService.GetCorruptionDetails(service, forceRefresh);
            return Ok(details);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting corruption details for service {Service}", service);
            return StatusCode(500, new { error = "Failed to get corruption details", details = ex.Message });
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
                (request.DeleteMode != "preserve" && request.DeleteMode != "full" && request.DeleteMode != "rsync"))
            {
                return BadRequest(new { error = "Delete mode must be 'preserve', 'full', or 'rsync'" });
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

    [HttpGet("system/rsync-available")]
    public IActionResult GetRsyncAvailable()
    {
        try
        {
            var available = _cacheClearingService.IsRsyncAvailable();
            return Ok(new { available });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking rsync availability");
            return StatusCode(500, new { error = "Failed to check rsync availability", details = ex.Message });
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

            return Ok(new
            {
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

            return Ok(new
            {
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

            return Ok(new
            {
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
            var hasProcessedLogs = _stateService.GetHasProcessedLogs();

            return Ok(new
            {
                isCompleted,
                hasProcessedLogs
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

    /// <summary>
    /// Get current Steam authentication status
    /// </summary>
    [HttpGet("steam-auth-status")]
    public IActionResult GetSteamAuthStatus()
    {
        try
        {
            var mode = _stateService.GetSteamAuthMode() ?? "anonymous";
            var username = _stateService.GetSteamUsername();
            var hasRefreshToken = false;

            try
            {
                hasRefreshToken = _stateService.HasSteamRefreshToken();
            }
            catch (Exception ex)
            {
                // If checking refresh token fails, assume no token and log warning
                _logger.LogWarning(ex, "[Steam Auth Status] Failed to check refresh token - credentials may not be readable");
                hasRefreshToken = false;
            }

            return Ok(new
            {
                mode,
                username = username ?? "",
                isAuthenticated = hasRefreshToken
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Steam auth status");
            // Always return valid JSON, even on error
            return Ok(new
            {
                mode = "anonymous",
                username = "",
                isAuthenticated = false
            });
        }
    }

    /// <summary>
    /// Login to Steam with credentials
    /// </summary>
    [HttpPost("steam-auth/login")]
    [RequireAuth]
    public async Task<IActionResult> SteamLogin([FromBody] SteamLoginRequest request)
    {
        try
        {
            _logger.LogInformation("[Steam Login] AutoStartPicsRebuild setting: {AutoStart}", request.AutoStartPicsRebuild);

            if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
            {
                return BadRequest(new { error = "Username and password are required" });
            }

            var result = await _steamKit2Service.AuthenticateAsync(
                request.Username,
                request.Password,
                request.TwoFactorCode,
                request.EmailCode,
                request.AllowMobileConfirmation
            );

            if (result.Success)
            {
                // Store auth state (mode and username)
                _stateService.SetSteamAuthMode("authenticated");
                _stateService.SetSteamUsername(request.Username);

                // Refresh token is saved by SteamKit2Service during authentication
                // Verify it was saved correctly (log warning if missing)
                var hasToken = _stateService.HasSteamRefreshToken();
                if (!hasToken)
                {
                    _logger.LogWarning("[Steam Login] Refresh token was not saved! Steam auth may not persist for user: {Username}", request.Username);
                }

                // Conditionally trigger full PICS rebuild based on user preference
                if (request.AutoStartPicsRebuild)
                {
                    _logger.LogInformation("[AUTO-START] request.AutoStartPicsRebuild = TRUE - checking if full PICS rebuild is possible");

                    // Check if Web API is available for full scan
                    var isWebApiAvailable = _steamKit2Service.IsWebApiAvailable();
                    if (!isWebApiAvailable)
                    {
                        _logger.LogWarning("[AUTO-START] Cannot auto-start full PICS rebuild - Steam Web API is unavailable");
                        return Ok(new
                        {
                            success = true,
                            message = "Authentication successful, but cannot start full PICS rebuild because Steam Web API V2 is unavailable and no V1 API key is configured. Please configure a Steam Web API key or download pre-created depot mappings from GitHub.",
                            autoStarted = false,
                            webApiUnavailable = true
                        });
                    }

                    _logger.LogInformation("[AUTO-START] Web API is available - triggering automatic full PICS rebuild");
                    var started = _steamKit2Service.TryStartRebuild(default, incrementalOnly: false);
                    _logger.LogInformation("[AUTO-START] TryStartRebuild returned: {Started}", started);

                    return Ok(new
                    {
                        success = true,
                        message = "Authentication successful. Starting PICS rebuild...",
                        autoStarted = true
                    });
                }
                else
                {
                    _logger.LogInformation("[AUTO-START] request.AutoStartPicsRebuild = FALSE - SKIPPING automatic PICS rebuild (manual mode)");
                    _logger.LogInformation("[AUTO-START] User will need to manually trigger PICS rebuild from UI");

                    return Ok(new
                    {
                        success = true,
                        message = "Authentication successful. You can manually trigger PICS rebuild from Depot Mapping section.",
                        autoStarted = false
                    });
                }
            }
            else if (result.RequiresMobileConfirmation)
            {
                return Ok(new { requiresMobileConfirmation = true, message = "Check your Steam Mobile App to confirm this login, or enter your 2FA code manually" });
            }
            else if (result.RequiresTwoFactor)
            {
                return Ok(new { requiresTwoFactor = true, message = "Two-factor authentication required" });
            }
            else if (result.RequiresEmailCode)
            {
                return Ok(new { requiresEmailCode = true, message = "Email verification code required" });
            }
            else
            {
                return BadRequest(new { error = result.Message ?? "Authentication failed" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Steam login");
            return StatusCode(500, new { error = "Failed to authenticate with Steam", details = ex.Message });
        }
    }

    /// <summary>
    /// Logout from Steam and switch to anonymous mode
    /// </summary>
    [HttpPost("steam-auth/logout")]
    [RequireAuth]
    public async Task<IActionResult> SteamLogout()
    {
        try
        {
            await _steamKit2Service.LogoutAsync();

            // Clear auth state from both state service and auth storage
            _stateService.SetSteamAuthMode("anonymous");
            _stateService.SetSteamUsername(null);
            _steamAuthStorage.ClearSteamAuthData();

            // Don't rebuild PICS data - preserve existing depot mappings
            _logger.LogInformation("Switched to anonymous Steam mode, keeping existing depot mappings");

            return Ok(new
            {
                success = true,
                message = "Switched to anonymous mode. Depot mappings preserved."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Steam logout");
            return StatusCode(500, new { error = "Failed to logout from Steam", details = ex.Message });
        }
    }

    /// <summary>
    /// Start game cache detection as a background operation
    /// </summary>
    [HttpPost("cache/detect-games")]
    public IActionResult StartGameCacheDetection([FromQuery] bool forceRefresh = false)
    {
        try
        {
            _logger.LogInformation("Starting game cache detection (background, forceRefresh={ForceRefresh})", forceRefresh);

            // If forceRefresh is true, disable incremental scanning (scan everything)
            // If forceRefresh is false, enable incremental scanning (skip already-detected games)
            var operationId = _gameCacheDetectionService.StartDetectionAsync(incremental: !forceRefresh);

            return Ok(new { operationId, cached = false });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting game cache detection");
            return StatusCode(500, new { error = "Failed to start game cache detection", details = ex.Message });
        }
    }

    /// <summary>
    /// Get the status of a game cache detection operation
    /// </summary>
    [HttpGet("cache/detect-games/{operationId}")]
    public IActionResult GetGameDetectionStatus(string operationId)
    {
        var status = _gameCacheDetectionService.GetOperationStatus(operationId);

        if (status == null)
        {
            return NotFound(new { error = "Operation not found" });
        }

        return Ok(status);
    }

    /// <summary>
    /// Get the active game cache detection operation (if any)
    /// </summary>
    [HttpGet("cache/detect-games-active")]
    public IActionResult GetActiveGameDetection()
    {
        var activeOperation = _gameCacheDetectionService.GetActiveOperation();

        if (activeOperation == null)
        {
            return Ok(new { hasActiveOperation = false });
        }

        return Ok(new { hasActiveOperation = true, operation = activeOperation });
    }

    /// <summary>
    /// Get cached game detection results (if available)
    /// </summary>
    [HttpGet("cache/detect-games-cached")]
    public async Task<IActionResult> GetCachedGameDetection()
    {
        var cachedResult = await _gameCacheDetectionService.GetCachedDetectionAsync();

        if (cachedResult == null || cachedResult.Status != "complete")
        {
            return Ok(new { hasCachedResults = false });
        }

        return Ok(new {
            hasCachedResults = true,
            games = cachedResult.Games,
            services = cachedResult.Services,
            totalGamesDetected = cachedResult.TotalGamesDetected,
            totalServicesDetected = cachedResult.TotalServicesDetected,
            lastDetectionTime = DateTime.SpecifyKind(cachedResult.StartTime, DateTimeKind.Utc)
        });
    }

    /// <summary>
    /// Remove all cache files for a specific game (fire-and-forget background operation)
    /// </summary>
    [HttpDelete("cache/game/{gameAppId}")]
    [RequireAuth]
    public IActionResult RemoveGameFromCache(uint gameAppId)
    {
        try
        {
            _logger.LogInformation("Starting background game removal for AppID {AppId}", gameAppId);

            // Fire-and-forget: run in background using IServiceScopeFactory
            _ = Task.Run(async () =>
            {
                try
                {
                    // Create a new scope for the background task
                    await using var scope = _serviceScopeFactory.CreateAsyncScope();
                    var cacheService = scope.ServiceProvider.GetRequiredService<CacheManagementService>();
                    var gameCacheDetectionService = scope.ServiceProvider.GetRequiredService<GameCacheDetectionService>();
                    var logger = scope.ServiceProvider.GetRequiredService<ILogger<ManagementController>>();

                    logger.LogInformation("[Background] Removing game {AppId} from cache", gameAppId);

                    var report = await cacheService.RemoveGameFromCache(gameAppId);

                    // Remove this specific game from the detection cache
                    await gameCacheDetectionService.RemoveGameFromCacheAsync(gameAppId);
                    logger.LogInformation("[Background] Successfully removed {GameName} ({AppId}) - {Files} files, {Bytes} bytes",
                        report.GameName, gameAppId, report.CacheFilesDeleted, report.TotalBytesFreed);

                    // Send completion notification via SignalR
                    await _hubContext.Clients.All.SendAsync("GameRemovalComplete", new
                    {
                        success = true,
                        gameAppId = report.GameAppId,
                        gameName = report.GameName,
                        filesDeleted = report.CacheFilesDeleted,
                        bytesFreed = report.TotalBytesFreed,
                        logEntriesRemoved = report.LogEntriesRemoved,
                        message = $"Successfully removed {report.GameName} from cache"
                    });
                }
                catch (Exception ex)
                {
                    // Log errors and send failure notification
                    _logger.LogError(ex, "[Background] Error removing game {AppId} from cache", gameAppId);

                    // Send failure notification via SignalR
                    try
                    {
                        await _hubContext.Clients.All.SendAsync("GameRemovalComplete", new
                        {
                            success = false,
                            gameAppId,
                            message = $"Failed to remove game {gameAppId}: {ex.Message}"
                        });
                    }
                    catch (Exception signalREx)
                    {
                        _logger.LogError(signalREx, "[Background] Failed to send SignalR notification for game {AppId}", gameAppId);
                    }
                }
            });

            // Return 202 Accepted immediately
            return Accepted(new
            {
                message = $"Game removal started for AppID {gameAppId}",
                gameAppId,
                status = "processing"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting game removal for AppID {AppId}", gameAppId);
            return StatusCode(500, new { error = $"Failed to start game removal for {gameAppId}", details = ex.Message });
        }
    }

    [HttpDelete("cache/service/{serviceName}")]
    [RequireAuth]
    public IActionResult RemoveServiceFromCache(string serviceName)
    {
        try
        {
            _logger.LogInformation("Starting background service removal for '{Service}'", serviceName);

            // Fire-and-forget: run in background using IServiceScopeFactory
            _ = Task.Run(async () =>
            {
                try
                {
                    // Create a new scope for the background task
                    await using var scope = _serviceScopeFactory.CreateAsyncScope();
                    var cacheService = scope.ServiceProvider.GetRequiredService<CacheManagementService>();
                    var gameCacheDetectionService = scope.ServiceProvider.GetRequiredService<GameCacheDetectionService>();
                    var logger = scope.ServiceProvider.GetRequiredService<ILogger<ManagementController>>();

                    logger.LogInformation("[Background] Removing service '{Service}' from cache", serviceName);

                    var report = await cacheService.RemoveServiceFromCache(serviceName);

                    // Remove this service from the detection cache
                    await gameCacheDetectionService.RemoveServiceFromCacheAsync(serviceName);
                    logger.LogInformation("[Background] Successfully removed service '{Service}' - {Files} files, {Bytes} bytes",
                        serviceName, report.CacheFilesDeleted, report.TotalBytesFreed);

                    // Send completion notification via SignalR
                    await _hubContext.Clients.All.SendAsync("ServiceRemovalComplete", new
                    {
                        success = true,
                        serviceName = report.ServiceName,
                        filesDeleted = report.CacheFilesDeleted,
                        bytesFreed = report.TotalBytesFreed,
                        logEntriesRemoved = report.LogEntriesRemoved,
                        databaseEntriesDeleted = report.DatabaseEntriesDeleted,
                        message = $"Successfully removed service '{report.ServiceName}' from cache"
                    });
                }
                catch (Exception ex)
                {
                    // Log errors and send failure notification
                    _logger.LogError(ex, "[Background] Error removing service '{Service}' from cache", serviceName);

                    // Send failure notification via SignalR
                    try
                    {
                        await _hubContext.Clients.All.SendAsync("ServiceRemovalComplete", new
                        {
                            success = false,
                            serviceName,
                            message = $"Failed to remove service '{serviceName}': {ex.Message}"
                        });
                    }
                    catch (Exception signalREx)
                    {
                        _logger.LogError(signalREx, "[Background] Failed to send SignalR notification for service '{Service}'", serviceName);
                    }
                }
            });

            // Return 202 Accepted immediately
            return Accepted(new
            {
                message = $"Service removal started for '{serviceName}'",
                serviceName,
                status = "processing"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting service removal for '{Service}'", serviceName);
            return StatusCode(500, new { error = $"Failed to start service removal for '{serviceName}'", details = ex.Message });
        }
    }

    /// <summary>
    /// Get the current application state including viability check cache
    /// </summary>
    [HttpGet("state")]
    public IActionResult GetAppState()
    {
        try
        {
            var state = _stateService.GetState();

            return Ok(new
            {
                requiresFullScan = state.RequiresFullScan,
                lastViabilityCheck = state.LastViabilityCheck,
                lastViabilityCheckChangeNumber = state.LastViabilityCheckChangeNumber,
                viabilityChangeGap = state.ViabilityChangeGap,
                setupCompleted = state.SetupCompleted,
                hasDataLoaded = state.HasDataLoaded,
                hasProcessedLogs = state.HasProcessedLogs
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting application state");
            return StatusCode(500, new { error = "Failed to get application state" });
        }
    }

    /// <summary>
    /// Migrate old JSON-based sessions to database (one-time migration)
    /// </summary>
    [HttpPost("migrate-sessions")]
    public async Task<IActionResult> MigrateSessions()
    {
        try
        {
            var (devicesImported, guestSessionsImported, filesDeleted) =
                await _sessionMigrationService.MigrateOldSessionsToDatabase();

            return Ok(new
            {
                message = "Migration completed successfully",
                devicesImported,
                guestSessionsImported,
                filesDeleted
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during session migration");
            return StatusCode(500, new { error = "Migration failed", details = ex.Message });
        }
    }
}

// Request model for removing service
public class RemoveServiceRequest
{
    public string Service { get; set; } = string.Empty;
}

// Request model for setting delete mode
public class SetDeleteModeRequest
{
    public string DeleteMode { get; set; } = string.Empty;
}

// Request model for Steam login
public class SteamLoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? TwoFactorCode { get; set; }
    public string? EmailCode { get; set; }
    public bool AllowMobileConfirmation { get; set; }
    public bool AutoStartPicsRebuild { get; set; } = true; // Default to true for backward compatibility
}

// Request model for corruption removal
public class RemoveCorruptionRequest
{
    public string Service { get; set; } = string.Empty;
}
