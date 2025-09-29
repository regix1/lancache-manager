using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;
using LancacheManager.Security;
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
    private readonly LogWatcherService _logWatcherService;
    private readonly LogProcessingService _logProcessingService;
    private static CancellationTokenSource? _processingCancellation;
    private static DateTime? _processingStartTime;

    public ManagementController(
        CacheManagementService cacheService,
        DatabaseService dbService,
        CacheClearingService cacheClearingService,
        IConfiguration configuration,
        ILogger<ManagementController> logger,
        IPathResolver pathResolver,
        StateService stateService,
        LogWatcherService logWatcherService,
        LogProcessingService logProcessingService)
    {
        _cacheService = cacheService;
        _dbService = dbService;
        _cacheClearingService = cacheClearingService;
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;
        _stateService = stateService;
        _logWatcherService = logWatcherService;
        _logProcessingService = logProcessingService;

        // Ensure data directory exists
        var dataDirectory = _pathResolver.GetDataDirectory();
        if (!Directory.Exists(dataDirectory))
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);
                _logger.LogInformation($"Created data directory: {dataDirectory}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create data directory: {dataDirectory}");
            }
        }
    }

    [HttpGet("cache")]
    public IActionResult GetCacheInfo()
    {
        var info = _cacheService.GetCacheInfo();
        return Ok(info);
    }

    // New async endpoint for clearing all cache
    [HttpPost("cache/clear-all")]
    [RequireAuth]
    public async Task<IActionResult> ClearAllCache()
    {
        try
        {
            // Start the background operation
            var operationId = await _cacheClearingService.StartCacheClearAsync();
            
            _logger.LogInformation($"Started cache clear operation: {operationId}");
            
            return Ok(new { 
                message = "Cache clearing started in background",
                operationId = operationId,
                status = "running"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting cache clear operation");
            return StatusCode(500, new { error = "Failed to start cache clearing", details = ex.Message });
        }
    }

    // Get status of cache clearing operation
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

    // Cancel cache clearing operation
    [HttpPost("cache/clear-cancel/{operationId}")]
    [RequireAuth]
    public IActionResult CancelClearOperation(string operationId)
    {
        var cancelled = _cacheClearingService.CancelOperation(operationId);
        
        if (!cancelled)
        {
            return NotFound(new { error = "Operation not found or already completed" });
        }
        
        return Ok(new { message = "Operation cancelled", operationId = operationId });
    }

    // Legacy endpoint for compatibility
    [HttpDelete("cache")]
    [RequireAuth]
    public async Task<IActionResult> ClearCache([FromQuery] string? service = null)
    {
        try
        {
            if (string.IsNullOrEmpty(service))
            {
                // Use the new async method for clearing all cache
                var operationId = await _cacheClearingService.StartCacheClearAsync();
                return Ok(new { 
                    message = "Cache clearing started in background",
                    operationId = operationId,
                    status = "running"
                });
            }
            else
            {
                // For specific service, remove from logs instead
                await _cacheService.RemoveServiceFromLogs(service);
                return Ok(new { message = $"Removed {service} entries from logs" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in clear cache operation");
            return StatusCode(500, new { error = "Failed to clear cache", details = ex.Message });
        }
    }

    [HttpDelete("database")]
    [RequireAuth]
    public async Task<IActionResult> ResetDatabase()
    {
        try
        {
            _logger.LogInformation("Database reset requested");
            await _dbService.ResetDatabase();
            _logger.LogInformation("Database reset completed successfully");
            return Ok(new {
                message = "Database reset successfully",
                status = "completed",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting database");
            return StatusCode(500, new {
                error = "Failed to reset database",
                details = ex.Message,
                status = "failed"
            });
        }
    }

    [HttpPost("reset-logs")]
    [RequireAuth]
    public async Task<IActionResult> ResetLogPosition([FromQuery] bool clearDatabase = false)
    {
        try
        {
            // Clear position to start from end
            _stateService.SetLogPosition(0);
            
            // Only reset database if explicitly requested
            if (clearDatabase)
            {
                await _dbService.ResetDatabase();
            }
            
            _logger.LogInformation("Log position reset - will start from end of log");
            
            return Ok(new { 
                message = "Log position reset successfully. Will start monitoring from the current end of the log file.",
                requiresRestart = false,
                databaseCleared = clearDatabase
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting log position");
            return StatusCode(500, new { error = "Failed to reset log position" });
        }
    }

    [HttpPost("process-all-logs")]
    [RequireAuth]
    public async Task<IActionResult> ProcessAllLogs([FromServices] OperationStateService stateService)
    {
        try
        {
            // Cancel any existing processing
            _processingCancellation?.Cancel();
            await Task.Delay(1000);
            
            _processingCancellation = new CancellationTokenSource();
            
            // Check if log file exists
            var logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");
            if (!System.IO.File.Exists(logPath))
            {
                _logger.LogWarning($"Log file not found at: {logPath}");
                return Ok(new {
                    message = $"Log file not found at: {logPath}",
                    logSizeMB = 0,
                    estimatedTimeMinutes = 0,
                    requiresRestart = false,
                    status = "no_log_file"
                });
            }

            // Get log file size
            var fileInfo = new FileInfo(logPath);
            var sizeMB = fileInfo.Length / (1024.0 * 1024.0);
            var resumePosition = _stateService.GetLogPosition();
            bool resumeFromSavedPosition = resumePosition > 0 && resumePosition < fileInfo.Length;
            
            // CHECK IF FILE IS EMPTY
            if (fileInfo.Length == 0)
            {
                _logger.LogWarning($"Log file is empty (0 bytes): {logPath}");
                return Ok(new { 
                    message = "Log file is empty. No data to process.",
                    logSizeMB = 0,
                    estimatedTimeMinutes = 0,
                    requiresRestart = false,
                    status = "empty_file"
                });
            }
            
            // CHECK IF FILE IS TOO SMALL (less than 100 bytes probably means no real data)
            if (fileInfo.Length < 100)
            {
                _logger.LogWarning($"Log file is very small ({fileInfo.Length} bytes): {logPath}");
                return Ok(new {
                    message = $"Log file only contains {fileInfo.Length} bytes. Likely no game data yet.",
                    logSizeMB = sizeMB,
                    estimatedTimeMinutes = 0,
                    requiresRestart = false,
                    status = "insufficient_data"
                });
            }

            // CHECK FOR DUPLICATE PROCESSING - Count lines in log file vs database entries
            try
            {
                _logger.LogInformation("Checking for duplicate processing by comparing log file lines to database entries");

                // Count lines in log file
                long logFileLineCount = 0;
                using (var reader = new StreamReader(logPath))
                {
                    while (!reader.EndOfStream)
                    {
                        await reader.ReadLineAsync();
                        logFileLineCount++;
                    }
                }

                // Count existing database entries
                var databaseEntryCount = await _dbService.GetLogEntryCountAsync();

                _logger.LogInformation($"Log file contains {logFileLineCount:N0} lines, database contains {databaseEntryCount:N0} entries");

                // If database has same or more entries than log file, skip processing
                if (databaseEntryCount >= logFileLineCount && logFileLineCount > 0)
                {
                    _logger.LogInformation("Skipping log processing - database already contains all log entries");
                    return Ok(new {
                        message = $"Processing skipped - database already contains {databaseEntryCount:N0} entries which matches or exceeds the {logFileLineCount:N0} lines in the log file. No new data to process.",
                        logSizeMB = sizeMB,
                        logFileLines = logFileLineCount,
                        databaseEntries = databaseEntryCount,
                        estimatedTimeMinutes = 0,
                        requiresRestart = false,
                        status = "already_processed",
                        skipReason = "database_up_to_date"
                    });
                }

                // If database has significantly fewer entries, log the delta
                if (databaseEntryCount < logFileLineCount)
                {
                    var newEntriesToProcess = logFileLineCount - databaseEntryCount;
                    _logger.LogInformation($"Database is behind by {newEntriesToProcess:N0} entries - proceeding with processing");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check for duplicate processing - proceeding with normal processing");
            }
            
            _logger.LogInformation($"Starting full log processing: {sizeMB:F1} MB");
            if (resumeFromSavedPosition)
            {
                _logger.LogInformation($"Resuming from saved position {resumePosition:N0} ({resumePosition / (1024.0 * 1024.0):F1} MB already processed)");
            }
            
            // Delete old marker first
            var processingMarker = Path.Combine(_pathResolver.GetDataDirectory(), "processing.marker");
            if (System.IO.File.Exists(processingMarker))
            {
                System.IO.File.Delete(processingMarker);
                await Task.Delay(100);
            }

            if (!resumeFromSavedPosition)
            {
                _stateService.SetLogPosition(0);
            }
            else
            {
                _logger.LogInformation($"Preserving saved log position {resumePosition:N0} for resume");
            }

            // Create marker with file size info
            var markerData = new
            {
                startTime = DateTime.UtcNow,
                startPosition = resumeFromSavedPosition ? resumePosition : 0L,
                fileSize = fileInfo.Length,
                triggerType = resumeFromSavedPosition ? "manual_resume" : "manual",
                resume = resumeFromSavedPosition,
                requestId = Guid.NewGuid().ToString()
            };

            await System.IO.File.WriteAllTextAsync(processingMarker,
                System.Text.Json.JsonSerializer.Serialize(markerData));

            _processingStartTime = DateTime.UtcNow;

            // Start the log processing services for manual processing
            // (They are no longer auto-started, only run on manual trigger)
            try
            {
                _logger.LogInformation("Starting LogProcessingService for manual log processing");
                await _logProcessingService.StartAsync(_processingCancellation.Token);

                _logger.LogInformation("Starting LogWatcherService for manual log processing");
                await _logWatcherService.StartAsync(_processingCancellation.Token);
            }
            catch (Exception serviceEx)
            {
                _logger.LogWarning(serviceEx, "Error starting log processing services, they may already be running");
            }

            // Create operation state
            var remainingBytes = resumeFromSavedPosition ? fileInfo.Length - resumePosition : fileInfo.Length;
            var remainingMB = remainingBytes / (1024.0 * 1024.0);

            var operationState = new OperationState
            {
                Key = "activeLogProcessing",
                Type = "log_processing",
                Status = "processing",
                Message = resumeFromSavedPosition ? $"Resuming log processing from {resumePosition:N0}" : "Processing entire log file from beginning",
                Data = new Dictionary<string, object>
                {
                    { "startTime", DateTime.UtcNow },
                    { "startPosition", resumeFromSavedPosition ? resumePosition : 0L },
                    { "fileSize", fileInfo.Length },
                    { "percentComplete", resumeFromSavedPosition && fileInfo.Length > 0 ? (resumePosition * 100.0) / fileInfo.Length : 0 },
                    { "status", "processing" },
                    { "resume", resumeFromSavedPosition },
                    { "resumePosition", resumePosition },
                    { "remainingBytes", remainingBytes },
                    { "remainingMB", remainingMB },
                    { "requestId", markerData.requestId }
                },
                ExpiresAt = DateTime.UtcNow.AddHours(24)
            };
            stateService.SaveState("activeLogProcessing", operationState);
            
            var estimatedMinutes = Math.Max(1, Math.Ceiling((remainingMB > 0 ? remainingMB : sizeMB) / 100));

            return Ok(new { 
                message = resumeFromSavedPosition
                    ? $"Resuming log processing. {remainingMB:F1} MB remaining..."
                    : $"Processing entire log file ({sizeMB:F1} MB) from the beginning...",
                logSizeMB = sizeMB,
                resume = resumeFromSavedPosition,
                resumePosition,
                remainingMB,
                estimatedTimeMinutes = estimatedMinutes,
                requiresRestart = false,
                status = "processing",
                requestId = markerData.requestId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up full log processing");
            return StatusCode(500, new { error = $"Failed to setup full log processing: {ex.Message}" });
        }
    }

    [HttpPost("cancel-processing")]
    [RequireAuth]
    public async Task<IActionResult> CancelProcessing()
    {
        try
        {
            _logger.LogInformation("Initiating processing cancellation");

            // Signal cancellation
            _processingCancellation?.Cancel();

            // Stop the log processing services with timeouts to prevent hanging
            _logger.LogInformation("Stopping log processing services with timeouts");

            var stopTimeout = TimeSpan.FromSeconds(15);
            using var timeoutCts = new CancellationTokenSource(stopTimeout);

            var stopTasks = new[]
            {
                StopServiceWithTimeout(_logWatcherService, "LogWatcherService", stopTimeout),
                StopServiceWithTimeout(_logProcessingService, "LogProcessingService", stopTimeout)
            };

            // Wait for all services to stop or timeout
            await Task.WhenAll(stopTasks);

            // Give services a moment to save their state
            await Task.Delay(500);

            // Get the saved position after services have stopped
            var currentPosition = _stateService.GetLogPosition();
            _logger.LogInformation($"Processing cancelled at position {currentPosition:N0}, position preserved for resume");

            // Remove processing marker AFTER services have stopped and saved position
            var processingMarker = Path.Combine(_pathResolver.GetDataDirectory(), "processing.marker");
            if (System.IO.File.Exists(processingMarker))
            {
                System.IO.File.Delete(processingMarker);
                _logger.LogInformation("Removed processing marker");
            }

            return Ok(new {
                message = $"Processing cancelled at position {currentPosition:N0}. Position saved for resume.",
                currentPosition = currentPosition,
                requiresRestart = false
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling processing");
            return StatusCode(500, new { error = "Failed to cancel processing" });
        }
    }

    private async Task StopServiceWithTimeout(IHostedService service, string serviceName, TimeSpan timeout)
    {
        try
        {
            _logger.LogInformation($"Stopping {serviceName} with {timeout.TotalSeconds}s timeout");

            using var cts = new CancellationTokenSource(timeout);
            await service.StopAsync(cts.Token);

            _logger.LogInformation($"{serviceName} stopped successfully");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning($"{serviceName} stop operation timed out after {timeout.TotalSeconds}s - forcing shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error stopping {serviceName}: {ex.Message}");
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
            return StatusCode(500, new { error = $"Failed to post-process depot mappings: {ex.Message}" });
        }
    }

    [HttpGet("processing-status")]
    public async Task<IActionResult> GetProcessingStatus([FromServices] OperationStateService operationStateService)
    {
        try
        {
            // Check operation state first for accurate status
            var activeOperation = operationStateService.GetState("activeLogProcessing");
            if (activeOperation != null && activeOperation.Data.TryGetValue("isProcessing", out var staleProcessingObj) &&
                staleProcessingObj is bool staleProcessing && !staleProcessing)
            {
                // Fully complete state is already available in the OperationStateService cache
                return Ok(new {
                    isProcessing = false,
                    message = "Processing complete",
                    percentComplete = 100,
                    mbProcessed = activeOperation.Data.TryGetValue("mbProcessed", out var completedMbObj) ? ConvertToDoubleSafe(completedMbObj) : 0,
                    mbTotal = activeOperation.Data.TryGetValue("mbTotal", out var totalMbObj) ? ConvertToDoubleSafe(totalMbObj) : 0,
                    status = "complete",
                    entriesProcessed = activeOperation.Data.TryGetValue("entriesProcessed", out var entriesObj) ? ConvertToInt32Safe(entriesObj) : 0,
                    linesProcessed = activeOperation.Data.TryGetValue("linesProcessed", out var linesObj) ? ConvertToInt32Safe(linesObj) : 0,
                    completedAt = activeOperation.Data.TryGetValue("completedAt", out var completedObj) ? completedObj : DateTime.UtcNow
                });
            }

            var operationStates = _stateService.GetOperationStates();
            var operationState = operationStates.FirstOrDefault(o => o.Id == "activeLogProcessing");
            if (operationState?.Data != null)
            {
                // Cast Data to Dictionary or JsonElement
                var dataDict = operationState.Data as Dictionary<string, object>;
                if (dataDict == null && operationState.Data is System.Text.Json.JsonElement jsonElement)
                {
                    // Convert JsonElement to Dictionary
                    dataDict = new Dictionary<string, object>();
                    foreach (var prop in jsonElement.EnumerateObject())
                    {
                        dataDict[prop.Name] = prop.Value.ValueKind switch
                        {
                            System.Text.Json.JsonValueKind.String => prop.Value.GetString(),
                            System.Text.Json.JsonValueKind.Number => prop.Value.GetDouble(),
                            System.Text.Json.JsonValueKind.True => true,
                            System.Text.Json.JsonValueKind.False => false,
                            _ => prop.Value.ToString()
                        };
                    }
                }

                if (dataDict != null)
                {
                    // Get status from operation state
                    bool isProcessing = dataDict.TryGetValue("isProcessing", out var processingObj)
                        && processingObj is bool processing && processing;

                    string status = dataDict.TryGetValue("status", out var statusObj)
                        ? statusObj?.ToString() ?? "processing"
                        : "processing";

                    double percentComplete = 0;
                    if (dataDict.TryGetValue("percentComplete", out var percentObj) && percentObj != null)
                    {
                        if (percentObj is JsonElement percentElement && percentElement.ValueKind == JsonValueKind.Number)
                        {
                            percentComplete = percentElement.GetDouble();
                        }
                        else if (double.TryParse(percentObj.ToString(), out var parsedPercent))
                        {
                            percentComplete = parsedPercent;
                        }
                    }

                    double mbProcessed = 0;
                    if (dataDict.TryGetValue("mbProcessed", out var mbProcObj) && mbProcObj != null)
                    {
                        if (mbProcObj is JsonElement mbProcElement && mbProcElement.ValueKind == JsonValueKind.Number)
                        {
                            mbProcessed = mbProcElement.GetDouble();
                        }
                        else if (double.TryParse(mbProcObj.ToString(), out var parsedMbProc))
                        {
                            mbProcessed = parsedMbProc;
                        }
                    }

                    double mbTotal = 0;
                    if (dataDict.TryGetValue("mbTotal", out var mbTotalObj) && mbTotalObj != null)
                    {
                        if (mbTotalObj is JsonElement mbTotalElement && mbTotalElement.ValueKind == JsonValueKind.Number)
                        {
                            mbTotal = mbTotalElement.GetDouble();
                        }
                        else if (double.TryParse(mbTotalObj.ToString(), out var parsedMbTotal))
                        {
                            mbTotal = parsedMbTotal;
                        }
                    }

                    if (!isProcessing && percentComplete >= 99)
                    {
                        try
                        {
                            operationStateService.UpdateState("activeLogProcessing", new Dictionary<string, object>
                            {
                                { "isProcessing", false },
                                { "status", "complete" },
                                { "percentComplete", 100.0 },
                                { "mbProcessed", mbProcessed },
                                { "mbTotal", mbTotal },
                                { "completedAt", DateTime.UtcNow }
                            });
                        }
                        catch (Exception updateEx)
                        {
                            _logger.LogWarning(updateEx, "Failed to normalize near-complete operation state");
                        }

                        return Ok(new {
                            isProcessing = false,
                            message = "Processing complete",
                            percentComplete = 100,
                            mbProcessed,
                            mbTotal,
                            status = "complete",
                            entriesProcessed = dataDict.TryGetValue("entriesProcessed", out var entriesObj)
                                ? ConvertToInt32Safe(entriesObj) : 0,
                            linesProcessed = dataDict.TryGetValue("linesProcessed", out var linesObj)
                                ? ConvertToInt32Safe(linesObj) : 0
                        });
                    }

                    // Detect and repair stuck processing states hovering at ~100%
                    if (isProcessing && mbTotal > 0)
                    {
                        var processedDelta = Math.Abs(mbTotal - mbProcessed);
                        var nearComplete = percentComplete >= 99.9 || processedDelta <= Math.Max(0.5, mbTotal * 0.005);

                        if (nearComplete)
                        {
                            _logger.LogWarning("Detected stuck log processing state at {Percent:F2}% with {Processed:F2}/{Total:F2} MB. Forcing completion.",
                                percentComplete, mbProcessed, mbTotal);

                            try
                            {
                                var forcedData = new Dictionary<string, object>
                                {
                                    { "isProcessing", false },
                                    { "status", "complete" },
                                    { "percentComplete", 100.0 },
                                    { "mbProcessed", mbTotal },
                                    { "mbTotal", mbTotal },
                                    { "entriesProcessed", dataDict.TryGetValue("entriesProcessed", out var entriesObj) ? ConvertToInt32Safe(entriesObj) : 0 },
                                    { "linesProcessed", dataDict.TryGetValue("linesProcessed", out var linesObj) ? ConvertToInt32Safe(linesObj) : 0 },
                                    { "completedAt", DateTime.UtcNow }
                                };

                                var forcedState = new OperationState
                                {
                                    Key = "activeLogProcessing",
                                    Type = "logProcessing",
                                    Status = "complete",
                                    Message = "Processing complete",
                                    CreatedAt = DateTime.UtcNow,
                                    UpdatedAt = DateTime.UtcNow,
                                    ExpiresAt = DateTime.UtcNow.AddHours(24),
                                    Data = forcedData
                                };

                                operationStateService.SaveState("activeLogProcessing", forcedState);
                                dataDict = forcedData;
                                isProcessing = false;
                                status = "complete";
                                percentComplete = 100;
                                mbProcessed = mbTotal;
                            }
                            catch (Exception repairEx)
                            {
                                _logger.LogError(repairEx, "Failed to force-complete stuck log processing state");
                            }

                            try
                            {
                                var logFilePath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");
                                if (System.IO.File.Exists(logFilePath))
                                {
                                    var fileInfo = new FileInfo(logFilePath);
                                    _stateService.SetLogPosition(fileInfo.Length);
                                }
                            }
                            catch (Exception positionEx)
                            {
                                _logger.LogWarning(positionEx, "Failed to update log position while forcing completion");
                            }

                            try
                            {
                                var marker = Path.Combine(_pathResolver.GetDataDirectory(), "processing.marker");
                                if (System.IO.File.Exists(marker))
                                {
                                    System.IO.File.Delete(marker);
                                }
                            }
                            catch (Exception markerEx)
                            {
                                _logger.LogWarning(markerEx, "Failed to remove processing marker during forced completion");
                            }

                            _ = Task.Run(() => StopProcessingServicesAsync("forced completion after stalled progress"));
                        }
                    }

                    // If status is complete, always return not processing
                    if (status == "complete" || (!isProcessing && percentComplete >= 100))
                    {
                        return Ok(new {
                            isProcessing = false,
                            message = "Processing complete",
                            percentComplete = 100,
                            mbProcessed,
                            mbTotal,
                            status = "complete",
                            entriesProcessed = dataDict.TryGetValue("entriesProcessed", out var entriesObj)
                                ? ConvertToInt32Safe(entriesObj) : 0,
                            linesProcessed = dataDict.TryGetValue("linesProcessed", out var linesObj)
                                ? ConvertToInt32Safe(linesObj) : 0
                        });
                    }

                    if (isProcessing)
                    {
                        // Calculate processing rate
                        double processingRate = 0;
                        string estimatedTime = "calculating...";

                        if (_processingStartTime.HasValue)
                        {
                            var elapsed = DateTime.UtcNow - _processingStartTime.Value;
                            if (elapsed.TotalSeconds > 0 && mbProcessed > 0)
                            {
                                processingRate = mbProcessed / elapsed.TotalSeconds;
                                if (processingRate > 0 && mbTotal > mbProcessed)
                                {
                                    var remainingMB = mbTotal - mbProcessed;
                                    var remainingSeconds = remainingMB / processingRate;
                                    var remainingMinutes = Math.Ceiling(remainingSeconds / 60);
                                    estimatedTime = remainingMinutes > 60
                                        ? $"{remainingMinutes / 60:F1} hours"
                                        : $"{remainingMinutes} minutes";
                                }
                            }
                        }

                        return Ok(new {
                            isProcessing = true,
                            currentPosition = dataDict.TryGetValue("currentPosition", out var posObj)
                                ? ConvertToInt64Safe(posObj) : 0,
                            percentComplete,
                            mbProcessed,
                            mbTotal,
                            processingRate,
                            estimatedTime,
                            message = $"Processing log file... {percentComplete:F1}% complete",
                            status = status,
                            entriesProcessed = dataDict.TryGetValue("entriesProcessed", out var entries2Obj)
                                ? ConvertToInt32Safe(entries2Obj) : 0,
                            linesProcessed = dataDict.TryGetValue("linesProcessed", out var lines2Obj)
                                ? ConvertToInt32Safe(lines2Obj) : 0
                        });
                    }
                }
            }

            // Fallback to checking marker and position
            var processingMarker = Path.Combine(_pathResolver.GetDataDirectory(), "processing.marker");
            bool markerExists = System.IO.File.Exists(processingMarker);

            // Then check position file
            long currentPosition = 0;
            long totalSize = 0;

            var logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");
            if (System.IO.File.Exists(logPath))
            {
                var fileInfo = new FileInfo(logPath);
                totalSize = fileInfo.Length;
            }

            currentPosition = _stateService.GetLogPosition();

            // If no marker and position is at 0, we're not processing
            if (!markerExists && currentPosition == 0)
            {
                return Ok(new {
                    isProcessing = false,
                    message = "Not processing",
                    currentPosition = 0,
                    totalSize = totalSize
                });
            }

            // If marker exists or position > 0, we might be processing
            if (markerExists || currentPosition > 0)
            {
                var percentComplete = totalSize > 0 ? (currentPosition * 100.0) / totalSize : 0;

                // Check if we're actually at the end
                if (currentPosition >= totalSize - 1000 && !markerExists)
                {
                    return Ok(new {
                        isProcessing = false,
                        message = "Processing complete",
                        percentComplete = 100,
                        mbProcessed = totalSize / (1024.0 * 1024.0),
                        mbTotal = totalSize / (1024.0 * 1024.0),
                        status = "complete"
                    });
                }

                // Calculate processing rate
                double processingRate = 0;
                string estimatedTime = "calculating...";

                if (_processingStartTime.HasValue && currentPosition > 0)
                {
                    var elapsed = DateTime.UtcNow - _processingStartTime.Value;
                    if (elapsed.TotalSeconds > 0)
                    {
                        processingRate = currentPosition / elapsed.TotalSeconds;
                        if (processingRate > 0)
                        {
                            var remainingBytes = totalSize - currentPosition;
                            var remainingSeconds = remainingBytes / processingRate;
                            var remainingMinutes = Math.Ceiling(remainingSeconds / 60);
                            estimatedTime = remainingMinutes > 60
                                ? $"{remainingMinutes / 60:F1} hours"
                                : $"{remainingMinutes} minutes";
                        }
                    }
                }

                return Ok(new {
                    isProcessing = markerExists, // Only return true if marker actually exists
                    currentPosition,
                    totalSize,
                    percentComplete,
                    mbProcessed = currentPosition / (1024.0 * 1024.0),
                    mbTotal = totalSize / (1024.0 * 1024.0),
                    processingRate = processingRate / (1024.0 * 1024.0), // MB/s
                    estimatedTime,
                    message = markerExists ? $"Processing log file... {percentComplete:F1}% complete" : "Processing complete",
                    status = markerExists ? "processing" : "complete",
                    markerExists
                });
            }

            return Ok(new {
                isProcessing = false,
                message = "Not processing"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting processing status");
            return Ok(new { isProcessing = false, error = ex.Message });
        }
    }

    private async Task StopProcessingServicesAsync(string reason)
    {
        try
        {
            _logger.LogInformation("Stopping log processing services automatically: {Reason}", reason);

            _processingCancellation?.Cancel();

            var stopTimeout = TimeSpan.FromSeconds(15);
            var stopTasks = new[]
            {
                StopServiceWithTimeout(_logWatcherService, "LogWatcherService", stopTimeout),
                StopServiceWithTimeout(_logProcessingService, "LogProcessingService", stopTimeout)
            };

            await Task.WhenAll(stopTasks);

            await Task.Delay(500);

            _processingStartTime = null;
            _processingCancellation?.Dispose();
            _processingCancellation = null;

            var processingMarker = Path.Combine(_pathResolver.GetDataDirectory(), "processing.marker");
            if (System.IO.File.Exists(processingMarker))
            {
                System.IO.File.Delete(processingMarker);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to automatically stop log processing services");
        }
    }

    // New endpoint to remove service entries from log file
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
            _logger.LogError(ex, $"Error removing {request.Service} from logs");
            return StatusCode(500, new { error = $"Failed to remove {request.Service} from logs", details = ex.Message });
        }
    }

    // New endpoint to get service log counts
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
            return StatusCode(500, new { error = "Failed to get service log counts" });
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
            return StatusCode(500, new { error = "Failed to get operations", message = ex.Message });
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
            return StatusCode(500, new { error = "Failed to get active operations", message = ex.Message });
        }
    }

    // Get configuration info
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

    // Debug endpoint to check permissions
    [HttpGet("debug/permissions")]
    public IActionResult CheckPermissions()
    {
        var results = new Dictionary<string, object>();
        
        // Check /logs directory
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
        
        // Check /cache directory
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
                
                // Count cache directories and estimate size
                try
                {
                    var dirs = Directory.GetDirectories(cachePath)
                        .Where(d => {
                            var name = Path.GetFileName(d);
                            return name.Length == 2 && IsHex(name);
                        })
                        .ToList();
                    
                    results["cacheDirectoryCount"] = dirs.Count;
                    
                    // Sample a few directories to estimate total size
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
                            
                            foreach (var file in files.Take(100)) // Sample first 100 files
                            {
                                try
                                {
                                    var fi = new FileInfo(file);
                                    totalSize += fi.Length;
                                }
                                catch { }
                            }
                        }
                        
                        // Extrapolate
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
        
        // Check /data directory
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
        
        // Check log file
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
        
        // Environment info
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
            return StatusCode(500, new { success = false, message = "Failed to mark setup as completed" });
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
                // Create default settings
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
            return StatusCode(500, new { error = "Failed to read settings" });
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
            return StatusCode(500, new { success = false, message = "Failed to update settings" });
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

    private static int ConvertToInt32Safe(object? obj)
    {
        if (obj == null) return 0;

        if (obj is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.Number => jsonElement.GetInt32(),
                JsonValueKind.String => int.TryParse(jsonElement.GetString(), out var intVal) ? intVal : 0,
                _ => 0
            };
        }

        if (obj is int intValue) return intValue;
        if (obj is double doubleValue) return (int)doubleValue;
        if (obj is long longValue) return (int)longValue;

        return int.TryParse(obj.ToString(), out var parsedInt) ? parsedInt : 0;
    }

    private static long ConvertToInt64Safe(object? obj)
    {
        if (obj == null) return 0;

        if (obj is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.Number => jsonElement.GetInt64(),
                JsonValueKind.String => long.TryParse(jsonElement.GetString(), out var longVal) ? longVal : 0,
                _ => 0
            };
        }

        if (obj is long longValue) return longValue;
        if (obj is int intValue) return intValue;
        if (obj is double doubleValue) return (long)doubleValue;

        return long.TryParse(obj.ToString(), out var parsedLong) ? parsedLong : 0;
    }

    private static double ConvertToDoubleSafe(object? obj)
    {
        if (obj == null) return 0;

        if (obj is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.Number => jsonElement.GetDouble(),
                JsonValueKind.String => double.TryParse(jsonElement.GetString(), out var dbl) ? dbl : 0,
                _ => 0
            };
        }

        if (obj is double doubleValue) return doubleValue;
        if (obj is int intValue) return intValue;
        if (obj is long longValue) return longValue;

        return double.TryParse(obj.ToString(), out var parsedDouble) ? parsedDouble : 0;
    }
}

// Request model for removing service
public class RemoveServiceRequest
{
    public string Service { get; set; } = string.Empty;
}
