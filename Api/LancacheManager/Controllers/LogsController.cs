using System.Text.Json;
using LancacheManager.Application.DTOs;
using LancacheManager.Application.Services;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for log processing and management
/// Handles log position updates, processing operations, and service log removal
/// </summary>
[ApiController]
[Route("api/logs")]
public class LogsController : ControllerBase
{
    private readonly RustLogProcessorService _rustLogProcessorService;
    private readonly RustLogRemovalService _rustLogRemovalService;
    private readonly ILogger<LogsController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly DatasourceService _datasourceService;
    private readonly StateRepository _stateRepository;

    public LogsController(
        RustLogProcessorService rustLogProcessorService,
        RustLogRemovalService rustLogRemovalService,
        ILogger<LogsController> logger,
        IPathResolver pathResolver,
        RustProcessHelper rustProcessHelper,
        DatasourceService datasourceService,
        StateRepository stateRepository)
    {
        _rustLogProcessorService = rustLogProcessorService;
        _rustLogRemovalService = rustLogRemovalService;
        _logger = logger;
        _pathResolver = pathResolver;
        _rustProcessHelper = rustProcessHelper;
        _datasourceService = datasourceService;
        _stateRepository = stateRepository;
    }

    /// <summary>
    /// GET /api/logs - Get log information
    /// </summary>
    [HttpGet]
    public IActionResult GetLogInfo()
    {
        var logsPath = _pathResolver.GetLogsDirectory();
        return Ok(new LogInfoResponse
        {
            Path = logsPath,
            Exists = Directory.Exists(logsPath)
        });
    }

    /// <summary>
    /// GET /api/logs/service-counts - Get log entry counts by service (aggregated from all datasources)
    /// </summary>
    [HttpGet("service-counts")]
    public async Task<IActionResult> GetServiceCounts()
    {
        var datasources = _datasourceService.GetDatasources();
        var aggregatedCounts = new Dictionary<string, ulong>();

        foreach (var ds in datasources)
        {
            if (!Directory.Exists(ds.LogPath))
            {
                _logger.LogWarning("Log directory not found for datasource '{Name}': {Path}", ds.Name, ds.LogPath);
                continue;
            }

            var result = await _rustProcessHelper.RunLogManagerAsync(
                "count",
                ds.LogPath,
                progressFile: null
            );

            if (!result.Success)
            {
                _logger.LogWarning("Failed to count logs for datasource '{Name}': {Error}", ds.Name, result.Error);
                continue;
            }

            // Extract service_counts from the result
            if (result.Data is JsonElement jsonElement &&
                jsonElement.TryGetProperty("service_counts", out var serviceCountsElement))
            {
                var serviceCounts = JsonSerializer.Deserialize<Dictionary<string, ulong>>(serviceCountsElement.GetRawText());
                if (serviceCounts != null)
                {
                    foreach (var kvp in serviceCounts)
                    {
                        if (aggregatedCounts.ContainsKey(kvp.Key))
                            aggregatedCounts[kvp.Key] += kvp.Value;
                        else
                            aggregatedCounts[kvp.Key] = kvp.Value;
                    }
                }
            }
        }

        return Ok(aggregatedCounts);
    }

    /// <summary>
    /// GET /api/logs/service-counts/by-datasource - Get log entry counts by service, grouped by datasource
    /// </summary>
    [HttpGet("service-counts/by-datasource")]
    public async Task<IActionResult> GetServiceCountsByDatasource()
    {
        var datasources = _datasourceService.GetDatasources();
        var result = new List<object>();

        foreach (var ds in datasources)
        {
            var dsEntry = new
            {
                datasource = ds.Name,
                logsPath = ds.LogPath,
                logsWritable = ds.LogsWritable,
                enabled = ds.Enabled,
                serviceCounts = new Dictionary<string, ulong>()
            };

            if (!Directory.Exists(ds.LogPath))
            {
                _logger.LogWarning("Log directory not found for datasource '{Name}': {Path}", ds.Name, ds.LogPath);
                result.Add(dsEntry);
                continue;
            }

            var countResult = await _rustProcessHelper.RunLogManagerAsync(
                "count",
                ds.LogPath,
                progressFile: null
            );

            if (!countResult.Success)
            {
                _logger.LogWarning("Failed to count logs for datasource '{Name}': {Error}", ds.Name, countResult.Error);
                result.Add(dsEntry);
                continue;
            }

            // Extract service_counts from the result
            if (countResult.Data is JsonElement jsonElement &&
                jsonElement.TryGetProperty("service_counts", out var serviceCountsElement))
            {
                var serviceCounts = JsonSerializer.Deserialize<Dictionary<string, ulong>>(serviceCountsElement.GetRawText());
                result.Add(new
                {
                    datasource = ds.Name,
                    logsPath = ds.LogPath,
                    logsWritable = ds.LogsWritable,
                    enabled = ds.Enabled,
                    serviceCounts = serviceCounts ?? new Dictionary<string, ulong>()
                });
            }
            else
            {
                result.Add(dsEntry);
            }
        }

        return Ok(result);
    }

    /// <summary>
    /// GET /api/logs/entries-count - Get total count of log entries in database
    /// </summary>
    [HttpGet("entries-count")]
    public IActionResult GetEntriesCount()
    {
        // Delegate to DatabaseController's endpoint for consistency
        return Ok(new MessageResponse { Message = "Use GET /api/database/log-entries-count instead" });
    }

    /// <summary>
    /// PATCH /api/logs/position - Update log position (reset to beginning or end)
    /// RESTful: PATCH is proper method for partial updates
    /// Request body: { "position": 0 } to reset to beginning, { "position": null } to reset to end
    /// </summary>
    [HttpPatch("position")]
    [RequireAuth]
    public IActionResult ResetLogPosition([FromBody] UpdateLogPositionRequest? request)
    {
        var datasources = _datasourceService.GetDatasources();

        // If position is explicitly 0, reset to beginning
        if (request?.Position == 0)
        {
            _rustLogProcessorService.ResetLogPosition();
            _logger.LogInformation("Log position reset to beginning for all datasources");

            return Ok(new LogPositionResponse
            {
                Message = "Log position reset to beginning",
                Position = 0
            });
        }

        // Otherwise (position is null or not specified), reset to end of file
        // Count lines in each datasource's log file and set position to that
        long totalLines = 0;
        foreach (var ds in datasources)
        {
            var logFile = Path.Combine(ds.LogPath, "access.log");
            if (System.IO.File.Exists(logFile))
            {
                var lineCount = CountLinesInFile(logFile);
                _stateRepository.SetLogPosition(ds.Name, lineCount);
                totalLines += lineCount;
                _logger.LogInformation("Datasource '{Name}': Log position set to end (line {LineCount})", ds.Name, lineCount);
            }
            else
            {
                _stateRepository.SetLogPosition(ds.Name, 0);
                _logger.LogInformation("Datasource '{Name}': Log file not found, position set to 0", ds.Name);
            }
        }

        _logger.LogInformation("Log position reset to end for all datasources (total lines: {TotalLines})", totalLines);

        return Ok(new LogPositionResponse
        {
            Message = "Log position reset to end of file",
            Position = totalLines
        });
    }

    /// <summary>
    /// Count lines in a file efficiently
    /// </summary>
    private static long CountLinesInFile(string filePath)
    {
        long lineCount = 0;
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() != null)
        {
            lineCount++;
        }
        return lineCount;
    }

    /// <summary>
    /// GET /api/logs/positions - Get log positions for all datasources
    /// </summary>
    [HttpGet("positions")]
    public IActionResult GetLogPositions()
    {
        var datasources = _datasourceService.GetDatasources();
        var positions = new List<object>();

        foreach (var ds in datasources)
        {
            var position = _stateRepository.GetLogPosition(ds.Name);
            var logFile = Path.Combine(ds.LogPath, "access.log");
            long totalLines = 0;

            if (System.IO.File.Exists(logFile))
            {
                totalLines = CountLinesInFile(logFile);
            }

            positions.Add(new
            {
                datasource = ds.Name,
                position = position,
                totalLines = totalLines,
                logPath = ds.LogPath,
                enabled = ds.Enabled
            });
        }

        return Ok(positions);
    }

    /// <summary>
    /// PATCH /api/logs/position/{datasourceName} - Reset position for a specific datasource
    /// </summary>
    [HttpPatch("position/{datasourceName}")]
    [RequireAuth]
    public IActionResult ResetDatasourceLogPosition(string datasourceName, [FromBody] UpdateLogPositionRequest? request)
    {
        var datasource = _datasourceService.GetDatasource(datasourceName);
        if (datasource == null)
        {
            return NotFound(new NotFoundResponse { Error = $"Datasource '{datasourceName}' not found" });
        }

        // If position is explicitly 0, reset to beginning
        if (request?.Position == 0)
        {
            _rustLogProcessorService.ResetLogPosition(datasourceName);
            _logger.LogInformation("Datasource '{Name}': Log position reset to beginning", datasourceName);

            return Ok(new LogPositionResponse
            {
                Message = $"Log position reset to beginning for '{datasourceName}'",
                Position = 0
            });
        }

        // Otherwise reset to end of file
        var logFile = Path.Combine(datasource.LogPath, "access.log");
        long lineCount = 0;

        if (System.IO.File.Exists(logFile))
        {
            lineCount = CountLinesInFile(logFile);
        }

        _stateRepository.SetLogPosition(datasourceName, lineCount);
        _logger.LogInformation("Datasource '{Name}': Log position set to end (line {LineCount})", datasourceName, lineCount);

        return Ok(new LogPositionResponse
        {
            Message = $"Log position reset to end of file for '{datasourceName}'",
            Position = lineCount
        });
    }

    /// <summary>
    /// POST /api/logs/process - Start processing logs from current position (all datasources)
    /// Note: POST is acceptable here as this starts an asynchronous operation
    /// Uses the position set by PUT /api/logs/position endpoint (top or bottom)
    /// </summary>
    [HttpPost("process")]
    [RequireAuth]
    public IActionResult ProcessAllLogs()
    {
        try
        {
            _ = _rustLogProcessorService.StartProcessing();
            _logger.LogInformation("Started log processing for all datasources");

            return Accepted(new OperationResponse
            {
                Message = "Log processing started for all datasources",
                Status = "running"
            });
        }
        catch (InvalidOperationException ex)
        {
            // Specific handling for "already running" case - return 409 Conflict
            _logger.LogWarning(ex, "Cannot start log processing - already running");
            return Conflict(new ConflictResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/logs/process/{datasourceName} - Start processing logs for a specific datasource
    /// </summary>
    [HttpPost("process/{datasourceName}")]
    [RequireAuth]
    public async Task<IActionResult> ProcessDatasourceLogs(string datasourceName)
    {
        var datasource = _datasourceService.GetDatasource(datasourceName);
        if (datasource == null)
        {
            return NotFound(new NotFoundResponse { Error = $"Datasource '{datasourceName}' not found" });
        }

        if (_rustLogProcessorService.IsProcessing)
        {
            return Conflict(new ConflictResponse { Error = "Log processing is already running" });
        }

        try
        {
            var position = _stateRepository.GetLogPosition(datasourceName);
            var success = await _rustLogProcessorService.StartProcessingAsync(
                datasource.LogPath,
                position,
                silentMode: false,
                datasourceName: datasourceName);

            if (success)
            {
                _logger.LogInformation("Started log processing for datasource '{Name}'", datasourceName);
                return Accepted(new OperationResponse
                {
                    Message = $"Log processing started for '{datasourceName}'",
                    Status = "running"
                });
            }
            else
            {
                return Conflict(new ConflictResponse { Error = "Failed to start log processing" });
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Cannot start log processing for datasource '{Name}'", datasourceName);
            return Conflict(new ConflictResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/logs/process/status - Get log processing status
    /// </summary>
    [HttpGet("process/status")]
    public IActionResult GetProcessingStatus()
    {
        var status = _rustLogProcessorService.GetStatus();
        return Ok(status);
    }

    /// <summary>
    /// DELETE /api/logs/services/{service} - Remove logs for specific service (from all datasources)
    /// RESTful: DELETE is proper method for removing resources, service name in path
    /// </summary>
    [HttpDelete("services/{service}")]
    [RequireAuth]
    public async Task<IActionResult> RemoveServiceLogs(string service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return BadRequest(new ConflictResponse { Error = "Service name is required" });
        }

        // StartServiceRemovalAsync returns bool but runs async - operation started if true
        var started = await _rustLogRemovalService.StartServiceRemovalAsync(service);

        if (!started)
        {
            return Conflict(new ConflictResponse { Error = "Service removal already in progress" });
        }

        _logger.LogInformation("Started log removal for service: {Service}", service);

        return Accepted(new LogRemovalStartResponse
        {
            Message = $"Started log removal for service: {service}",
            Service = service,
            Status = "started"
        });
    }

    /// <summary>
    /// DELETE /api/logs/datasources/{datasourceName}/services/{service} - Remove logs for specific service from a specific datasource
    /// </summary>
    [HttpDelete("datasources/{datasourceName}/services/{service}")]
    [RequireAuth]
    public async Task<IActionResult> RemoveServiceLogsFromDatasource(string datasourceName, string service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return BadRequest(new ConflictResponse { Error = "Service name is required" });
        }

        var datasource = _datasourceService.GetDatasource(datasourceName);
        if (datasource == null)
        {
            return NotFound(new NotFoundResponse { Error = $"Datasource '{datasourceName}' not found" });
        }

        if (!datasource.LogsWritable)
        {
            return BadRequest(new ConflictResponse { Error = $"Logs directory is read-only for datasource '{datasourceName}'" });
        }

        var started = await _rustLogRemovalService.StartServiceRemovalForDatasourceAsync(service, datasourceName);

        if (!started)
        {
            return Conflict(new ConflictResponse { Error = "Service removal already in progress" });
        }

        _logger.LogInformation("Started log removal for service: {Service} in datasource: {Datasource}", service, datasourceName);

        return Accepted(new LogRemovalStartResponse
        {
            Message = $"Started log removal for service: {service} from datasource: {datasourceName}",
            Service = service,
            Status = "started"
        });
    }

    /// <summary>
    /// GET /api/logs/remove/status - Get status of log removal operation
    /// </summary>
    [HttpGet("remove/status")]
    public IActionResult GetRemovalStatus()
    {
        var status = _rustLogRemovalService.GetRemovalStatus();
        return Ok(status);
    }

    /// <summary>
    /// DELETE /api/logs/remove/cancel - Cancel current service removal operation
    /// RESTful: DELETE for cancelling/removing the operation
    /// </summary>
    [HttpDelete("remove/cancel")]
    [RequireAuth]
    public IActionResult CancelServiceRemoval()
    {
        var result = _rustLogRemovalService.CancelOperation();

        if (!result)
        {
            return NotFound(new NotFoundResponse { Error = "No service removal operation running" });
        }

        return Ok(new CancellationResponse { Message = "Service removal cancellation requested", Cancelled = true });
    }

    /// <summary>
    /// POST /api/logs/remove/kill - Force kill service removal operation
    /// Used as fallback when graceful cancellation fails
    /// </summary>
    [HttpPost("remove/kill")]
    [RequireAuth]
    public async Task<IActionResult> ForceKillServiceRemoval()
    {
        var result = await _rustLogRemovalService.ForceKillOperation();

        if (!result)
        {
            return NotFound(new NotFoundResponse { Error = "No service removal operation running or no process to kill" });
        }

        return Ok(new MessageResponse { Message = "Service removal force killed successfully" });
    }

    public class UpdateLogPositionRequest
    {
        public long? Position { get; set; }
        public bool? Reset { get; set; }
    }
}
