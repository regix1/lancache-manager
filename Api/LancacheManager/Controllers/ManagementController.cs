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

    [HttpGet("data-availability")]
    public IActionResult GetDataAvailability()
    {
        try
        {
            var hasData = _stateService.HasDataLoaded();
            var state = _stateService.GetState();

            return Ok(new
            {
                hasDataLoaded = hasData,
                lastDataLoadTime = state.LastDataLoadTime,
                lastDataMappingCount = state.LastDataMappingCount,
                setupCompleted = state.SetupCompleted
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking data availability");
            return StatusCode(500, new { error = "Failed to check data availability", details = ex.Message });
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

    [HttpGet("processing-progress")]
    public async Task<IActionResult> GetProcessingProgress()
    {
        try
        {
            var dataDirectory = _pathResolver.GetDataDirectory();
            var progressPath = Path.Combine(dataDirectory, "rust_progress.json");

            if (!System.IO.File.Exists(progressPath))
            {
                return Ok(new { isProcessing = _rustLogProcessorService.IsProcessing, progress = (object?)null });
            }

            var json = await System.IO.File.ReadAllTextAsync(progressPath);
            var progress = System.Text.Json.JsonSerializer.Deserialize<object>(json);

            return Ok(new { isProcessing = _rustLogProcessorService.IsProcessing, progress });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting processing progress");
            return Ok(new { isProcessing = _rustLogProcessorService.IsProcessing, error = ex.Message });
        }
    }

    private async Task StopServiceWithTimeout(IHostedService service, string serviceName, TimeSpan timeout)
    {
        try
        {
            _logger.LogInformation("Stopping service {ServiceName} with {Timeout}s timeout", serviceName, timeout.TotalSeconds);

            using var cts = new CancellationTokenSource(timeout);
            await service.StopAsync(cts.Token);

            _logger.LogInformation("Service {ServiceName} stopped successfully", serviceName);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Service {ServiceName} stop operation timed out after {Timeout}s - forcing shutdown", serviceName, timeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping service {ServiceName}", serviceName);
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

    [HttpGet("cache/clear-operations")]
    public IActionResult GetAllClearOperations()
    {
        try
        {
            var operations = _cacheClearingService.GetAllOperations();

            return Ok(operations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache clear operations");
            return StatusCode(500, new { error = "Failed to get cache clear operations", details = ex.Message });
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

    [HttpGet("debug/permissions")]
    public IActionResult CheckPermissions()
    {
        var results = new Dictionary<string, object>();

        try
        {
            var logsDir = _pathResolver.GetLogsDirectory();
            results["logsDirectory"] = logsDir;
            results["logsExists"] = Directory.Exists(logsDir);

            if (Directory.Exists(logsDir))
            {
                var testFile = Path.Combine(logsDir, ".write_test");
                try
                {
                    System.IO.File.WriteAllText(testFile, "test");
                    System.IO.File.Delete(testFile);
                    results["logsWritable"] = true;
                }
                catch (Exception ex)
                {
                    results["logsWritable"] = false;
                    results["logsError"] = ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            results["logsCheckError"] = ex.Message;
        }

        try
        {
            var cachePath = _cacheService.GetCachePath();
            results["cacheDirectory"] = cachePath;
            results["cacheExists"] = Directory.Exists(cachePath);

            if (Directory.Exists(cachePath))
            {
                var testFile = Path.Combine(cachePath, ".write_test");
                try
                {
                    System.IO.File.WriteAllText(testFile, "test");
                    System.IO.File.Delete(testFile);
                    results["cacheWritable"] = true;
                }
                catch (Exception ex)
                {
                    results["cacheWritable"] = false;
                    results["cacheError"] = ex.Message;
                }

                try
                {
                    var dirs = Directory.GetDirectories(cachePath)
                        .Where(d => {
                            var name = Path.GetFileName(d);
                            return name.Length == 2 && IsHex(name);
                        })
                        .ToList();

                    results["cacheDirectoryCount"] = dirs.Count;

                    if (dirs.Count > 0)
                    {
                        long totalFiles = 0;
                        long totalSize = 0;
                        int sampleCount = Math.Min(3, dirs.Count);

                        for (int i = 0; i < sampleCount; i++)
                        {
                            var dir = dirs[i];
                            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                            totalFiles += files.Length;

                            foreach (var file in files.Take(100))
                            {
                                try
                                {
                                    var fi = new FileInfo(file);
                                    totalSize += fi.Length;
                                }
                                catch { }
                            }
                        }

                        if (sampleCount > 0)
                        {
                            var avgFilesPerDir = totalFiles / sampleCount;
                            var estimatedTotalFiles = avgFilesPerDir * dirs.Count;
                            results["estimatedCacheFiles"] = estimatedTotalFiles;

                            if (totalFiles > 0)
                            {
                                var avgFileSize = totalSize / Math.Min(totalFiles, 100 * sampleCount);
                                var estimatedTotalSize = avgFileSize * estimatedTotalFiles;
                                results["estimatedCacheSizeGB"] = estimatedTotalSize / (1024.0 * 1024.0 * 1024.0);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    results["cacheSizeError"] = ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            results["cacheCheckError"] = ex.Message;
        }

        try
        {
            var dataDirectory = _pathResolver.GetDataDirectory();
            results["dataDirectory"] = dataDirectory;
            results["dataExists"] = Directory.Exists(dataDirectory);

            if (Directory.Exists(dataDirectory))
            {
                var testFile = Path.Combine(dataDirectory, ".write_test");
                try
                {
                    System.IO.File.WriteAllText(testFile, "test");
                    System.IO.File.Delete(testFile);
                    results["dataWritable"] = true;
                }
                catch (Exception ex)
                {
                    results["dataWritable"] = false;
                    results["dataError"] = ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            results["dataCheckError"] = ex.Message;
        }

        try
        {
            var logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");
            results["logFile"] = logPath;
            results["logFileExists"] = System.IO.File.Exists(logPath);

            if (System.IO.File.Exists(logPath))
            {
                var fileInfo = new FileInfo(logPath);
                results["logFileSize"] = fileInfo.Length;
                results["logFileReadOnly"] = fileInfo.IsReadOnly;
                results["logFileLastModified"] = fileInfo.LastWriteTimeUtc;
            }
        }
        catch (Exception ex)
        {
            results["logFileError"] = ex.Message;
        }

        results["user"] = Environment.UserName;
        results["userId"] = Environment.GetEnvironmentVariable("USER") ?? "unknown";
        results["workingDirectory"] = Environment.CurrentDirectory;

        return Ok(results);
    }

    [HttpGet("setup-status")]
    public IActionResult GetSetupStatus()
    {
        try
        {
            var isSetupCompleted = _stateService.GetSetupCompleted();

            return Ok(new { isSetupCompleted });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking setup status");
            return Ok(new { isSetupCompleted = false });
        }
    }

    [HttpPost("mark-setup-completed")]
    public IActionResult MarkSetupCompleted()
    {
        try
        {
            _stateService.SetSetupCompleted(true);
            _logger.LogInformation("Setup marked as completed");

            return Ok(new { success = true, message = "Setup marked as completed" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking setup as completed");
            return StatusCode(500, new { error = "Failed to mark setup as completed", details = ex.Message });
        }
    }

    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        try
        {
            var settingsFile = Path.Combine(_pathResolver.GetDataDirectory(), "settings.json");

            if (!System.IO.File.Exists(settingsFile))
            {
                var defaultSettings = CreateDefaultSettings();
                System.IO.File.WriteAllText(settingsFile, System.Text.Json.JsonSerializer.Serialize(defaultSettings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return Ok(defaultSettings);
            }

            var settingsJson = System.IO.File.ReadAllText(settingsFile);
            var settings = System.Text.Json.JsonSerializer.Deserialize<object>(settingsJson);

            return Ok(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading settings");
            return StatusCode(500, new { error = "Failed to read settings", details = ex.Message });
        }
    }

    [HttpPost("settings")]
    public IActionResult UpdateSettings([FromBody] object settings)
    {
        try
        {
            var settingsFile = Path.Combine(_pathResolver.GetDataDirectory(), "settings.json");
            var settingsJson = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(settingsFile, settingsJson);
            _logger.LogInformation("Settings updated successfully");

            return Ok(new { success = true, message = "Settings updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating settings");
            return StatusCode(500, new { error = "Failed to update settings", details = ex.Message });
        }
    }

    private object CreateDefaultSettings()
    {
        return new
        {
            app = new
            {
                name = "Lancache Manager",
                version = "1.0.0",
                theme = "dark",
                language = "en",
                autoRefresh = true,
                refreshInterval = 30000,
                showNotifications = true
            },
            dashboard = new
            {
                defaultTimeRange = "24h",
                showClientStats = true,
                showServiceStats = true,
                showDownloads = true,
                maxRecentDownloads = 100,
                autoUpdateCharts = true
            },
            cache = new
            {
                clearConfirmation = true,
                showOperationProgress = true,
                defaultClearType = "selective"
            },
            logs = new
            {
                defaultLogLevel = "info",
                maxLogLines = 1000,
                autoScroll = true,
                showTimestamps = true,
                bulkProcessingBatchSize = 10000
            },
            depot = new
            {
                autoUpdateMappings = true,
                preferCloudData = true,
                cacheTimeout = 3600000
            },
            ui = new
            {
                compactMode = false,
                showTooltips = true,
                animationsEnabled = true,
                sidebarCollapsed = false
            }
        };
    }

    private bool IsHex(string value)
    {
        return value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }

    // Conversion methods moved to LancacheManager.Utilities.JsonConverters
    private static int ConvertToInt32Safe(object? obj) => JsonConverters.ToInt32Safe(obj);
    private static long ConvertToInt64Safe(object? obj) => JsonConverters.ToInt64Safe(obj);
    private static double ConvertToDoubleSafe(object? obj) => JsonConverters.ToDoubleSafe(obj);
}

// Request model for removing service
public class RemoveServiceRequest
{
    public string Service { get; set; } = string.Empty;
}
