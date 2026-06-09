using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Models;
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

        try
        {
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
        catch (Exception ex) when (ex is ObjectDisposedException or NullReferenceException)
        {
            // P2-C: the operation completed concurrently (its CTS was disposed / state nulled) between
            // the lookup above and the cancel. The op is already terminal, so the user's intent is
            // satisfied — report success instead of leaking an unhandled 500.
            return Ok(new
            {
                message = "Operation already completed",
                operationId = id,
                status = OperationStatus.Completed
            });
        }
    }

    /// <summary>
    /// Force-kills a running operation when cancel alone does not unblock the UI.
    /// Mirrors cache-clear POST /api/cache/operations/{id}/kill and log-processing force-kill:
    /// kill process tree → wait → SignalR completion → tracker cleanup.
    /// </summary>
    [HttpPost("{id}/force-kill")]
    public async Task<IActionResult> ForceKillOperationAsync(Guid id)
    {
        try
        {
            var killed = await _cancellationService.ForceKillAsync(id);
            if (!killed)
            {
                return NotFound(new { error = "Operation not found or already completed", operationId = id });
            }

            return Ok(new { message = "Operation force killed", operationId = id });
        }
        catch (Exception ex) when (ex is ObjectDisposedException or NullReferenceException)
        {
            // P2-C parity with the cancel endpoint: the operation completed concurrently (its CTS /
            // Process was disposed or state nulled) during the force kill. The op is already terminal,
            // so the user's intent is satisfied — report success instead of leaking an unhandled 500.
            return Ok(new
            {
                message = "Operation already completed",
                operationId = id,
                status = OperationStatus.Completed
            });
        }
    }
}
