using System.Text.Json;
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

    public LogsController(
        RustLogProcessorService rustLogProcessorService,
        RustLogRemovalService rustLogRemovalService,
        ILogger<LogsController> logger,
        IPathResolver pathResolver,
        RustProcessHelper rustProcessHelper)
    {
        _rustLogProcessorService = rustLogProcessorService;
        _rustLogRemovalService = rustLogRemovalService;
        _logger = logger;
        _pathResolver = pathResolver;
        _rustProcessHelper = rustProcessHelper;
    }

    /// <summary>
    /// GET /api/logs - Get log information
    /// </summary>
    [HttpGet]
    public IActionResult GetLogInfo()
    {
        try
        {
            var logsPath = _pathResolver.GetLogsDirectory();
            return Ok(new
            {
                path = logsPath,
                exists = Directory.Exists(logsPath)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting log information");
            return StatusCode(500, new { error = "Failed to get log information", details = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/logs/service-counts - Get log entry counts by service
    /// </summary>
    [HttpGet("service-counts")]
    public async Task<IActionResult> GetServiceCounts()
    {
        try
        {
            var logsPath = _pathResolver.GetLogsDirectory();
            var result = await _rustProcessHelper.RunLogManagerAsync(
                "count",
                logsPath,
                progressFile: null
            );

            if (!result.Success)
            {
                return StatusCode(500, new
                {
                    error = "Failed to count log entries",
                    details = result.Error
                });
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting service counts");
            return StatusCode(500, new
            {
                error = "Failed to get service counts",
                details = ex.Message
            });
        }
    }

    /// <summary>
    /// GET /api/logs/entries-count - Get total count of log entries in database
    /// </summary>
    [HttpGet("entries-count")]
    public IActionResult GetEntriesCount()
    {
        // Delegate to DatabaseController's endpoint for consistency
        return Ok(new { message = "Use GET /api/database/log-entries-count instead" });
    }

    /// <summary>
    /// PATCH /api/logs/position - Update log position (reset to beginning)
    /// RESTful: PATCH is proper method for partial updates
    /// Request body: { "position": 0 } or { "reset": true }
    /// </summary>
    [HttpPatch("position")]
    [RequireAuth]
    public IActionResult ResetLogPosition([FromBody] UpdateLogPositionRequest? request)
    {
        try
        {
            _rustLogProcessorService.ResetLogPosition();
            _logger.LogInformation("Log position reset to beginning");

            return Ok(new
            {
                message = "Log position reset successfully",
                position = 0
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting log position");
            return StatusCode(500, new { error = "Failed to reset log position", details = ex.Message });
        }
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

            return Accepted(new
            {
                message = "Log processing started",
                status = "running"
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Cannot start log processing - already running");
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting log processing");
            return StatusCode(500, new { error = "Failed to start log processing", details = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/logs/process/status - Get log processing status
    /// </summary>
    [HttpGet("process/status")]
    public IActionResult GetProcessingStatus()
    {
        try
        {
            var status = _rustLogProcessorService.GetStatus();
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting processing status");
            return StatusCode(500, new { error = "Failed to get processing status" });
        }
    }

    /// <summary>
    /// DELETE /api/logs/services/{service} - Remove logs for specific service
    /// RESTful: DELETE is proper method for removing resources, service name in path
    /// </summary>
    [HttpDelete("services/{service}")]
    [RequireAuth]
    public async Task<IActionResult> RemoveServiceLogs(string service)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(service))
            {
                return BadRequest(new { error = "Service name is required" });
            }

            var operationId = await _rustLogRemovalService.StartServiceRemovalAsync(service);
            _logger.LogInformation("Started log removal for service: {Service}, OperationId: {OperationId}",
                service, operationId);

            return Accepted(new
            {
                message = $"Started log removal for service: {service}",
                service,
                operationId,
                status = "started"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting log removal for service: {Service}", service);
            return StatusCode(500, new
            {
                error = $"Failed to start log removal for service: {service}",
                details = ex.Message
            });
        }
    }

    /// <summary>
    /// GET /api/logs/remove/status - Get status of log removal operation
    /// </summary>
    [HttpGet("remove/status")]
    public async Task<IActionResult> GetRemovalStatus()
    {
        try
        {
            var status = await _rustLogRemovalService.GetRemovalStatus();

            if (status == null)
            {
                return NotFound(new { error = "No log removal operation found" });
            }

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting log removal status");
            return StatusCode(500, new { error = "Failed to get log removal status" });
        }
    }

    public class UpdateLogPositionRequest
    {
        public long? Position { get; set; }
        public bool? Reset { get; set; }
    }
}
