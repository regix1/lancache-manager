using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
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
    private readonly OperationCancellationService _cancellationService;

    public OperationsController(
        IUnifiedOperationTracker operationTracker,
        OperationCancellationService cancellationService)
    {
        _operationTracker = operationTracker;
        _cancellationService = cancellationService;
    }

    /// <summary>
    /// Aggressively cancels a running operation: kills any associated process tree, then cancels the token.
    /// Idempotent — returns 200 OK if already cancelling (re-attempts process kill).
    /// </summary>
    [HttpPost("{id}/cancel")]
    public IActionResult CancelOperation(Guid id)
    {
        var operation = _operationTracker.GetOperation(id);
        if (operation == null)
        {
            return NotFound(new { error = "Operation not found", operationId = id });
        }

        var cancelled = _cancellationService.Cancel(id);
        if (cancelled)
        {
            return Ok(new
            {
                message = "Cancellation requested (process kill + token cancel)",
                operationId = id,
                status = operation.Status
            });
        }

        return BadRequest(new { error = "Operation cannot be cancelled", operationId = id });
    }

    /// <summary>
    /// Force-kills a running operation when cancel alone does not unblock the UI.
    /// Mirrors cache-clear POST /api/cache/operations/{id}/kill and log-processing force-kill:
    /// kill process tree → wait → SignalR completion → tracker cleanup.
    /// </summary>
    [HttpPost("{id}/force-kill")]
    public async Task<IActionResult> ForceKillOperationAsync(Guid id)
    {
        var killed = await _cancellationService.ForceKillAsync(id);
        if (!killed)
        {
            return NotFound(new { error = "Operation not found or already completed", operationId = id });
        }

        return Ok(new { message = "Operation force killed", operationId = id });
    }
}
