using LancacheManager.Models;
using LancacheManager.Core.Interfaces;
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
[Authorize(Policy = "AccountHolder")]
public class DatabaseController : ControllerBase
{
    private readonly DatabaseService _dbService;
    private readonly ILogger<DatabaseController> _logger;
    private readonly IOperationConflictChecker _conflictChecker;
    private readonly IOperationQueue _operationQueue;

    public DatabaseController(
        DatabaseService dbService,
        ILogger<DatabaseController> logger,
        IOperationConflictChecker conflictChecker,
        IOperationQueue operationQueue)
    {
        _dbService = dbService;
        _logger = logger;
        _conflictChecker = conflictChecker;
        _operationQueue = operationQueue;
    }

    /// <summary>
    /// Empties every table the application stores data in, including the sessions table, so this
    /// call signs the caller out and every other user with it.
    /// </summary>
    /// <remarks>
    /// The accounts themselves survive, so everyone can sign back in; the platform logins do not,
    /// because clearing sessions also signs out Steam, Xbox, Epic and the prefill daemons. The
    /// corruption scanner's structural baseline is cleared too, so its next scan re-reads every
    /// file instead of skipping unchanged ones. Left untouched: Entity Framework's migration
    /// history and the scanner's own schema version marker, neither of which holds user data.
    ///
    /// A conflicting bulk operation does not reject the request; it is parked on the wait queue
    /// and started automatically once the conflict clears, so the caller always gets back an
    /// operation to track rather than a 409.
    /// </remarks>
    [HttpDelete]
    [ProducesResponseType(typeof(DatabaseResetStartResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(QueuedOperationResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ResetDatabaseAsync(CancellationToken cancellationToken)
    {
        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        Task<Guid?> StartDatabaseResetAsync() => Task.FromResult<Guid?>(_dbService.StartFullResetAsync());

        var conflict = await _conflictChecker.CheckAsync(
            OperationType.DatabaseReset,
            ConflictScope.Bulk(),
            cancellationToken);
        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.DatabaseReset, ConflictScope.Bulk(), "Database Reset",
                StartDatabaseResetAsync, cancellationToken));
        }

        var operationId = (await StartDatabaseResetAsync())!.Value;
        _logger.LogInformation("Started full database reset operation: {OperationId}, Started: {Started}", operationId, true);

        return Accepted(new DatabaseResetStartResponse
        {
            Message = "Database reset started",
            OperationId = operationId,
            Status = OperationStatus.Running
        });
    }

    /// <summary>
    /// Wipes only the named tables instead of the whole database.
    /// </summary>
    /// <remarks>
    /// Uses the same wait-queue model as the full reset: a conflicting bulk operation parks this
    /// request instead of rejecting it.
    /// </remarks>
    /// <param name="request">The table names to clear; an empty or missing list is rejected.</param>
    [HttpDelete("tables")]
    [ProducesResponseType(typeof(SelectedTablesResetResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(QueuedOperationResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ResetSelectedTablesAsync([FromBody] ResetTablesRequest request, CancellationToken cancellationToken)
    {
        if (request.Tables == null || request.Tables.Count == 0)
        {
            return BadRequest(ApiResponse.Error("No tables specified for reset"));
        }

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        // _dbService.StartResetAsync has no internal single-flight guard of its own (unlike the
        // full-reset path above), so without this check a second selected-tables reset - or one
        // overlapping a full reset - would just register a second tracked operation unchecked.
        Task<Guid?> StartSelectedTablesResetAsync() => Task.FromResult<Guid?>(_dbService.StartResetAsync(request.Tables));

        var conflict = await _conflictChecker.CheckAsync(
            OperationType.DatabaseReset,
            ConflictScope.Bulk(),
            cancellationToken);
        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.DatabaseReset, ConflictScope.Bulk(), "Database Reset",
                StartSelectedTablesResetAsync, cancellationToken));
        }

        var operationId = (await StartSelectedTablesResetAsync())!.Value;
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
    /// Reports whether a database reset is currently running.
    /// </summary>
    /// <remarks>
    /// Recovery endpoint for the database reset notification card, covering both full and
    /// selected-tables resets. Both run through <see cref="DatabaseService"/>, so a page refresh
    /// mid-reset finds the operation regardless of which one started it.
    /// </remarks>
    [HttpGet("reset-status")]
    [ProducesResponseType(typeof(DatabaseResetStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<DatabaseResetStatusResponse> GetDatabaseResetStatus()
    {
        if (!_dbService.IsResetOperationRunning)
        {
            return Ok(new DatabaseResetStatusResponse { IsProcessing = false });
        }

        var progress = DatabaseService.CurrentResetProgress;
        if (progress == null)
        {
            return Ok(new DatabaseResetStatusResponse
            {
                IsProcessing = true,
                Status = OperationStatus.Pending,
                OperationId = DatabaseService.CurrentResetOperationId
            });
        }

        return Ok(new DatabaseResetStatusResponse
        {
            IsProcessing = progress.IsProcessing,
            Status = progress.Status,
            Message = progress.Message,
            PercentComplete = progress.Snapshot.PercentComplete,
            OperationId = progress.OperationId,
            StageKey = progress.Snapshot.StageKey,
            Context = progress.Snapshot.Context,
            TablesCleared = progress.TablesCleared,
            TotalTables = progress.TotalTables,
            FilesDeleted = progress.FilesDeleted
        });
    }

    /// <summary>
    /// Returns the count of log entries currently stored in the database.
    /// </summary>
    /// <remarks>
    /// This is the survivor of a duplicate pair; <c>LogsController.GetEntriesCount</c> was
    /// removed because it returned a sentence instead of a number.
    /// </remarks>
    [HttpGet("log-entries-count")]
    [ProducesResponseType(typeof(LogEntriesCountResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LogEntriesCountResponse>> GetLogCountAsync()
    {
        var count = await _dbService.GetLogEntriesCountAsync();
        return Ok(new LogEntriesCountResponse { Count = count });
    }
}
