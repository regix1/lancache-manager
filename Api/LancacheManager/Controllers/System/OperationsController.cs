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
[Authorize(Policy = "AccountHolder")]
public class OperationsController : ControllerBase
{
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly OperationCancellationService _cancellationService;
    private readonly IOperationQueue _operationQueue;
    private readonly IServiceScheduleRegistry _scheduleRegistry;

    public OperationsController(
        IUnifiedOperationTracker operationTracker,
        OperationCancellationService cancellationService,
        IOperationQueue operationQueue,
        IServiceScheduleRegistry scheduleRegistry)
    {
        _operationTracker = operationTracker;
        _cancellationService = cancellationService;
        _operationQueue = operationQueue;
        _scheduleRegistry = scheduleRegistry;
    }

    /// <summary>
    /// Gets the status of a single tracked operation.
    /// </summary>
    /// <remarks>
    /// Lightweight liveness/progress probe for a single tracked operation, usable by API
    /// consumers that cannot subscribe to SignalR.
    ///
    /// Returns 200 with { id, active, percentComplete, message }. active=false means the
    /// operation is no longer tracked (completed, failed, cancelled, or never existed).
    /// </remarks>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(OperationStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<OperationStatusResponse> GetOperationStatus(Guid id)
    {
        var active = _operationTracker.GetActiveOperations().Any(o => o.Id == id);
        var op = _operationTracker.GetOperation(id);
        var next = _operationTracker.GetOperation(id, followHandoff: true);
        var response = new OperationStatusResponse
        {
            Id = id,
            Active = active,
            PercentComplete = 100.0
        };
        if (op != null)
        {
            lock (op)
            {
                response.Active = active && !op.Status.IsTerminal();
                response.PercentComplete = response.Active ? op.PercentComplete : 100.0;
                response.Message = response.Active ? op.Message : null;
                response.Status = op.Status;
                response.StartedAt = op.StartedAt;
                response.ParentOperationId = op.ParentOperationId;
                response.Error = op.Status == OperationStatus.Failed ? op.Message : null;
            }
        }
        if (next != null && next.Id != id)
        {
            lock (next)
            {
                response.NextOperationId = next.Id;
                response.NextStatus = next.Status;
            }
        }
        return Ok(response);
    }

    /// <summary>
    /// Lists operations parked in the wait queue.
    /// </summary>
    /// <remarks>
    /// Recovery endpoint for the purple waiting cards: page refresh re-creates them from this
    /// list. Queued ops do not survive an app restart (in-memory queue), so after a restart this
    /// list is empty by design.
    /// </remarks>
    [HttpGet("waiting")]
    [ProducesResponseType(typeof(List<WaitingOperationResponse>), StatusCodes.Status200OK)]
    public ActionResult<List<WaitingOperationResponse>> GetWaitingOperations()
    {
        var waiting = _operationTracker.GetWaitingOperations()
            .OrderBy(op => op.StartedAt)
            .Select(op => new WaitingOperationResponse
            {
                OperationId = op.Id,
                OperationType = op.Type.ToWireString(),
                Name = op.Name,
                ShowNotification = RunNotice.ReadRunNotice(op.Metadata)?.ShowNotification ?? !_operationQueue.IsWaiterSilent(op.Id),
                Status = op.Status.ToWireString(),
                // Two owners park operations and each answers for its own: the queue for a run
                // waiting on another operation, the schedule registry for one held for a download.
                // Asking only the queue lost the blocker's name on every refresh of a held run.
                BlockedByName = _operationQueue.GetWaitingBlockerName(op.Id)
                    ?? _scheduleRegistry.GetHeldRunBlockerName(op.Id)
            })
            .ToList();

        return Ok(waiting);
    }

    /// <summary>
    /// Cancels a running operation.
    /// </summary>
    /// <remarks>
    /// Aggressively cancels a running operation: kills any associated process tree, then cancels
    /// the token. Idempotent — returns 200 OK if already cancelling (re-attempts process kill).
    /// A cancel that arrives after the operation finished is also 200, because the caller's
    /// intent is satisfied, but it carries <c>alreadyFinished: true</c> so the browser can drop
    /// the stale card instead of waiting for a cancellation that will never arrive.
    /// </remarks>
    [HttpPost("{id}/cancel")]
    [ProducesResponseType(typeof(OperationCancelResponse), StatusCodes.Status200OK)]
    public ActionResult<OperationCancelResponse> CancelOperation(Guid id)
    {
        var operation = _operationTracker.GetOperation(id, followHandoff: true);
        if (operation == null)
        {
            return NotFound(ApiResponse.NotFound("Operation", id));
        }

        try
        {
            switch (_cancellationService.Cancel(operation.Id))
            {
                case OperationCancelResult.Requested:
                    return Ok(new OperationCancelResponse
                    {
                        Message = "Cancellation requested (process kill + token cancel)",
                        OperationId = id,
                        Status = operation.Status,
                        AlreadyFinished = operation.Status.IsTerminal()
                    });

                case OperationCancelResult.AlreadyFinished:
                    return Ok(new OperationCancelResponse
                    {
                        Message = "Operation already finished",
                        OperationId = id,
                        Status = operation.Status,
                        AlreadyFinished = true
                    });

                default:
                    // The tracker's cleanup reaper evicted the operation between the lookup above and
                    // the cancel. The frontend reads this message to drop the card.
                    return BadRequest(ApiResponse.Invalid("Operation cannot be cancelled"));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or NullReferenceException)
        {
            // P2-C: the operation completed concurrently (its CTS was disposed / state nulled) between
            // the lookup above and the cancel. The op is already terminal, so the user's intent is
            // satisfied — report success instead of leaking an unhandled 500. The disposed-Process
            // race is answered where the wait happens, in ProcessManager, because reporting every
            // InvalidOperationException as finished would drop the card on a live operation.
            return Ok(new OperationCancelResponse
            {
                Message = "Operation already completed",
                OperationId = id,
                Status = operation.Status,
                AlreadyFinished = true
            });
        }
    }

    /// <summary>
    /// Force-kills a running operation when cancel alone does not unblock the UI.
    /// </summary>
    /// <remarks>
    /// This is the only force-kill surface for every operation type: kill process tree → wait →
    /// SignalR completion → tracker cleanup (which runs the owning service's OnTerminalCleanup).
    /// </remarks>
    [HttpPost("{id}/force-kill")]
    [ProducesResponseType(typeof(OperationForceKillResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OperationForceKillResponse>> ForceKillAsync(Guid id)
    {
        try
        {
            var killed = await _cancellationService.ForceKillAsync(id);
            if (!killed)
            {
                return NotFound(ApiResponse.Error("Operation not found or already completed"));
            }

            return Ok(new OperationForceKillResponse { Message = "Operation force killed", OperationId = id });
        }
        catch (Exception ex) when (ex is ObjectDisposedException or NullReferenceException)
        {
            // P2-C parity with the cancel endpoint: the operation completed concurrently (its CTS /
            // Process was disposed or state nulled) during the force kill. The op is already terminal,
            // so the user's intent is satisfied — report success instead of leaking an unhandled 500.
            // The disposed-Process race is answered in ProcessManager, where the wait happens.
            return Ok(new OperationForceKillResponse
            {
                Message = "Operation already completed",
                OperationId = id
            });
        }
    }
}
