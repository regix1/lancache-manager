using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/operations")]
public class OperationsController : ControllerBase
{
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly ILogger<OperationsController> _logger;

    public OperationsController(
        IUnifiedOperationTracker operationTracker,
        ILogger<OperationsController> logger)
    {
        _operationTracker = operationTracker;
        _logger = logger;
    }

    /// <summary>
    /// Gets all active operations, optionally filtered by type.
    /// </summary>
    /// <param name="type">Optional operation type filter</param>
    /// <returns>List of active operations</returns>
    [HttpGet]
    public ActionResult<IEnumerable<OperationInfo>> GetActiveOperations([FromQuery] OperationType? type = null)
    {
        var operations = _operationTracker.GetActiveOperations(type);
        return Ok(operations);
    }

    /// <summary>
    /// Gets a specific operation by ID.
    /// </summary>
    /// <param name="id">Operation ID</param>
    /// <returns>Operation information or 404 if not found</returns>
    [HttpGet("{id}")]
    public ActionResult<OperationInfo> GetOperation(string id)
    {
        var operation = _operationTracker.GetOperation(id);
        if (operation == null)
        {
            return NotFound(new { error = "Operation not found", operationId = id });
        }

        return Ok(operation);
    }

    /// <summary>
    /// Cancels a running operation.
    /// This endpoint is idempotent - returns 200 OK if already cancelling.
    /// </summary>
    /// <param name="id">Operation ID</param>
    /// <returns>200 OK if cancelled or already cancelling, 404 if operation not found</returns>
    [HttpPost("{id}/cancel")]
    public IActionResult CancelOperation(string id)
    {
        var operation = _operationTracker.GetOperation(id);
        if (operation == null)
        {
            return NotFound(new { error = "Operation not found", operationId = id });
        }

        var cancelled = _operationTracker.CancelOperation(id);
        if (cancelled)
        {
            return Ok(new
            {
                message = "Cancellation requested",
                operationId = id,
                status = operation.Status
            });
        }

        return BadRequest(new { error = "Operation cannot be cancelled", operationId = id });
    }

    /// <summary>
    /// Force kills the process associated with an operation.
    /// Use this when normal cancellation doesn't work.
    /// </summary>
    /// <param name="id">Operation ID</param>
    /// <returns>200 OK if process killed, 404 if operation not found, 400 if no process to kill</returns>
    [HttpPost("{id}/kill")]
    public IActionResult ForceKillOperation(string id)
    {
        var operation = _operationTracker.GetOperation(id);
        if (operation == null)
        {
            return NotFound(new { error = "Operation not found", operationId = id });
        }

        var killed = _operationTracker.ForceKillOperation(id);
        if (killed)
        {
            return Ok(new
            {
                message = "Process killed",
                operationId = id
            });
        }

        return BadRequest(new { error = "No process to kill or operation cannot be force killed", operationId = id });
    }
}
