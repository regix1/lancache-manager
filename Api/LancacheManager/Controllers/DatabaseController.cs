using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for database management operations
/// Handles database reset operations
/// </summary>
[ApiController]
[Route("api/database")]
public class DatabaseController : ControllerBase
{
    private readonly DatabaseRepository _dbService;
    private readonly RustDatabaseResetService _rustDatabaseResetService;
    private readonly ILogger<DatabaseController> _logger;

    public DatabaseController(
        DatabaseRepository dbService,
        RustDatabaseResetService rustDatabaseResetService,
        ILogger<DatabaseController> logger)
    {
        _dbService = dbService;
        _rustDatabaseResetService = rustDatabaseResetService;
        _logger = logger;
    }

    /// <summary>
    /// DELETE /api/database - Reset entire database
    /// RESTful: DELETE is proper method for clearing/resetting resources
    /// </summary>
    [HttpDelete]
    [RequireAuth]
    public async Task<IActionResult> ResetDatabase()
    {
        try
        {
            var operationId = await _rustDatabaseResetService.StartDatabaseResetAsync();
            _logger.LogInformation("Started full database reset operation: {OperationId}", operationId);

            return Accepted(new
            {
                message = "Database reset started",
                operationId,
                status = "running"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting database reset");
            return StatusCode(500, new { error = "Failed to start database reset", details = ex.Message });
        }
    }

    /// <summary>
    /// DELETE /api/database/tables - Reset selected tables
    /// RESTful: DELETE with body specifying which tables to reset
    /// Request body: { "tables": ["Downloads", "ClientStats", ...] }
    /// </summary>
    [HttpDelete("tables")]
    [RequireAuth]
    public IActionResult ResetSelectedTables([FromBody] ResetTablesRequest request)
    {
        try
        {
            if (request.Tables == null || request.Tables.Count == 0)
            {
                return BadRequest(new { error = "No tables specified for reset" });
            }

            var operationId = _dbService.StartResetSelectedTablesAsync(request.Tables);
            _logger.LogInformation("Started selective database reset operation: {OperationId}, Tables: {Tables}",
                operationId, string.Join(", ", request.Tables));

            return Accepted(new
            {
                message = "Database reset started for selected tables",
                operationId,
                tables = request.Tables,
                status = "running"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting selective database reset");
            return StatusCode(500, new { error = "Failed to start database reset", details = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/database/reset-status - Get status of database reset operation
    /// Checks both Rust-based reset service and C# DatabaseRepository reset operations
    /// </summary>
    [HttpGet("reset-status")]
    public IActionResult GetDatabaseResetStatus()
    {
        try
        {
            // Check C# DatabaseRepository reset operations first
            if (_dbService.IsResetOperationRunning)
            {
                var progress = DatabaseRepository.CurrentResetProgress;
                return Ok(new
                {
                    isProcessing = progress.IsProcessing,
                    status = progress.Status,
                    message = progress.Message,
                    percentComplete = progress.PercentComplete
                });
            }

            // Fall back to Rust-based reset service status
            var status = _rustDatabaseResetService.GetDatabaseResetStatus();
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting database reset status");
            return StatusCode(500, new { error = "Failed to get database reset status" });
        }
    }

    /// <summary>
    /// GET /api/database/log-entries-count - Get count of log entries in database
    /// </summary>
    [HttpGet("log-entries-count")]
    public async Task<IActionResult> GetLogEntriesCount()
    {
        try
        {
            var count = await _dbService.GetLogEntriesCountAsync();
            return Ok(new { count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting log entries count");
            return StatusCode(500, new { error = "Failed to get log entries count", details = ex.Message });
        }
    }

    public class ResetTablesRequest
    {
        public List<string> Tables { get; set; } = new();
    }
}
