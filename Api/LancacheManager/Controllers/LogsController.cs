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
    /// GET /api/logs/service-counts - Get log entry counts by service
    /// </summary>
    [HttpGet("service-counts")]
    public async Task<IActionResult> GetServiceCounts()
    {
        var logsPath = _pathResolver.GetLogsDirectory();
        var result = await _rustProcessHelper.RunLogManagerAsync(
            "count",
            logsPath,
            progressFile: null
        );

        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error ?? "Failed to count log entries");
        }

        // Extract service_counts from the result
        if (result.Data is JsonElement jsonElement &&
            jsonElement.TryGetProperty("service_counts", out var serviceCountsElement))
        {
            var serviceCounts = JsonSerializer.Deserialize<Dictionary<string, ulong>>(serviceCountsElement.GetRawText());
            return Ok(serviceCounts);
        }

        _logger.LogWarning("RunLogManagerAsync returned data without service_counts property");
        return Ok(new Dictionary<string, ulong>());
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
    /// POST /api/logs/process - Start processing logs from current position
    /// Note: POST is acceptable here as this starts an asynchronous operation
    /// Uses the position set by PUT /api/logs/position endpoint (top or bottom)
    /// </summary>
    [HttpPost("process")]
    [RequireAuth]
    public IActionResult ProcessAllLogs()
    {
        try
        {
            _rustLogProcessorService.StartProcessing();
            _logger.LogInformation("Started log processing");

            return Accepted(new OperationResponse
            {
                Message = "Log processing started",
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
    /// GET /api/logs/process/status - Get log processing status
    /// </summary>
    [HttpGet("process/status")]
    public IActionResult GetProcessingStatus()
    {
        var status = _rustLogProcessorService.GetStatus();
        return Ok(status);
    }

    /// <summary>
    /// DELETE /api/logs/services/{service} - Remove logs for specific service
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
