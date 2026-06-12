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
    /// GET /api/operations/{id}
    ///
    /// Lightweight liveness/progress probe for a single tracked operation, usable by API
    /// consumers that cannot subscribe to SignalR.
    ///
    /// Returns 200 with { id, active, percentComplete, message }. active=false means the
    /// operation is no longer tracked (completed, failed, cancelled, or never existed).
    /// </summary>
    [HttpGet("{id}")]
    public IActionResult GetOperationStatus(Guid id)
    {
        var op = _operationTracker.GetActiveOperations().FirstOrDefault(o => o.Id == id);
        if (op == null)
        {
            return Ok(new { id, active = false, percentComplete = 100.0, message = (string?)null });
        }

        return Ok(new { id, active = true, percentComplete = op.PercentComplete, message = op.Message });
    }

    /// <summary>
    /// GET /api/operations/waiting
    ///
    /// Lists operations parked in the wait-queue (status Waiting). Recovery endpoint for the
    /// purple waiting cards: page refresh re-creates them from this list. Queued ops do not
    /// survive an app restart (in-memory queue), so after a restart this list is empty by design.
    /// </summary>
    [HttpGet("waiting")]
    public IActionResult GetWaitingOperations()
    {
        var waiting = _operationTracker.GetWaitingOperations()
            .OrderBy(op => op.StartedAt)
            .Select(op => new
            {
                operationId = op.Id,
                operationType = op.Type.ToWireString(),
                name = op.Name
            })
            .ToList();

        return Ok(waiting);
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
