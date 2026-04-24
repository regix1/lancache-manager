using LancacheManager.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Controller for managing operations.
/// All endpoints require authentication to prevent unauthorized cancellation/termination of operations.
/// </summary>
[ApiController]
[Route("api/operations")]
[Authorize(Policy = "AdminOnly")]
public class OperationsController : ControllerBase
{
    private readonly IUnifiedOperationTracker _operationTracker;

    public OperationsController(
        IUnifiedOperationTracker operationTracker)
    {
        _operationTracker = operationTracker;
    }

    /// <summary>
    /// Cancels a running operation.
    /// This endpoint is idempotent - returns 200 OK if already cancelling.
    /// </summary>
    /// <param name="id">Operation ID</param>
    /// <returns>200 OK if cancelled or already cancelling, 404 if operation not found</returns>
    [HttpPost("{id}/cancel")]
    public IActionResult CancelOperation(Guid id)
    {
        var operation = _operationTracker.GetOperation(id);
        if (operation == null)
            return NotFound(new { error = "Operation not found", operationId = id });

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

}
