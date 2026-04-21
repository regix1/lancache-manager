using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for database management operations
/// Handles database reset operations
/// </summary>
[ApiController]
[Route("api/database")]
[Authorize(Policy = "AdminOnly")]
public class DatabaseController : ControllerBase
{
    private readonly DatabaseService _dbService;
    private readonly RustDatabaseResetService _rustDatabaseResetService;
    private readonly ILogger<DatabaseController> _logger;
    private readonly IOperationConflictChecker _conflictChecker;

    public DatabaseController(
        DatabaseService dbService,
        RustDatabaseResetService rustDatabaseResetService,
        ILogger<DatabaseController> logger,
        IOperationConflictChecker conflictChecker)
    {
        _dbService = dbService;
        _rustDatabaseResetService = rustDatabaseResetService;
        _logger = logger;
        _conflictChecker = conflictChecker;
    }

    /// <summary>
    /// DELETE /api/database - Reset entire database
    /// RESTful: DELETE is proper method for clearing/resetting resources
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> ResetDatabaseAsync(CancellationToken cancellationToken)
    {
        // Central concurrency check — DatabaseReset is globally catastrophic.
        var conflict = await _conflictChecker.CheckAsync(
            OperationType.DatabaseReset,
            ConflictScope.Bulk(),
            cancellationToken);
        if (conflict != null)
        {
            return Conflict(conflict);
        }

        var started = await _rustDatabaseResetService.StartDatabaseResetAsync();
        if (!started)
        {
            // Race: reset began between our check and StartDatabaseResetAsync.
            var raceConflict = await _conflictChecker.CheckAsync(
                OperationType.DatabaseReset,
                ConflictScope.Bulk(),
                cancellationToken);
            if (raceConflict != null)
            {
                return Conflict(raceConflict);
            }
            return Conflict(new OperationConflictResponse
            {
                Code = "OPERATION_CONFLICT",
                StageKey = "errors.conflict.duplicate",
                Error = "Database reset is already running"
            });
        }

        var operationId = _rustDatabaseResetService.CurrentOperationId ?? Guid.NewGuid();
        _logger.LogInformation("Started full database reset operation: {OperationId}, Started: {Started}", operationId, true);

        return Accepted(new DatabaseResetStartResponse
        {
            Message = "Database reset started",
            OperationId = operationId,
            Status = OperationStatus.Running
        });
    }

    /// <summary>
    /// DELETE /api/database/tables - Reset selected tables
    /// RESTful: DELETE with body specifying which tables to reset
    /// Request body: { "tables": ["Downloads", "ClientStats", ...] }
    /// </summary>
    [HttpDelete("tables")]
    public IActionResult ResetSelectedTables([FromBody] ResetTablesRequest request)
    {
        if (request.Tables == null || request.Tables.Count == 0)
        {
            return BadRequest(new ErrorResponse { Error = "No tables specified for reset" });
        }

        var operationId = _dbService.StartResetSelectedTablesAsync(request.Tables);
        _logger.LogInformation("Started selective database reset operation: {OperationId}, Tables: {Tables}",
            operationId, string.Join(", ", request.Tables));

        return Accepted(new SelectedTablesResetResponse
        {
            Message = "Database reset started for selected tables",
            OperationId = operationId,
            Tables = request.Tables,
            Status = OperationStatus.Running
        });
    }

    /// <summary>
    /// GET /api/database/reset-status - Get status of database reset operation
    /// Checks both Rust-based reset service and C# DatabaseService reset operations
    /// </summary>
    [HttpGet("reset-status")]
    public IActionResult GetDatabaseResetStatus()
    {
        // Check C# DatabaseService reset operations first
        if (_dbService.IsResetOperationRunning)
        {
            var progress = DatabaseService.CurrentResetProgress;
            return Ok(new DatabaseResetStatusResponse
            {
                IsProcessing = progress.IsProcessing,
                Status = progress.Status,
                Message = progress.Message,
                PercentComplete = (int)progress.PercentComplete
            });
        }

        // Fall back to Rust-based reset service status
        var status = _rustDatabaseResetService.GetDatabaseResetStatus();
        return Ok(status);
    }

    /// <summary>
    /// GET /api/database/log-entries-count - Get count of log entries in database
    /// </summary>
    [HttpGet("log-entries-count")]
    public async Task<IActionResult> GetLogEntriesCountAsync()
    {
        var count = await _dbService.GetLogEntriesCountAsync();
        return Ok(new LogEntriesCountResponse { Count = count });
    }
}
