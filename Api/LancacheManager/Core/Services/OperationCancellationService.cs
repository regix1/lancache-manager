using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Centralizes aggressive operation cancellation and force-kill for ALL operation types so the dead
/// per-service <c>ForceKill*</c> endpoints are no longer needed. Force-kill flow:
/// graceful CANCEL to the Rust child (await its real exit, escalate to a hard kill on timeout) →
/// token cancel → single SignalR completion → tracker cleanup (which runs the owning service's
/// <see cref="Models.OperationInfo.OnTerminalCleanup"/> and disposes the CTS exactly once).
/// </summary>
public class OperationCancellationService
{
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly ProcessManager _processManager;
    private readonly ILogger<OperationCancellationService> _logger;

    public OperationCancellationService(
        IUnifiedOperationTracker operationTracker,
        ProcessManager processManager,
        ILogger<OperationCancellationService> logger)
    {
        _operationTracker = operationTracker;
        _processManager = processManager;
        _logger = logger;
    }

    /// <summary>
    /// Aggressive cancel — terminates any associated process tree, then cancels the token.
    /// Matches log-processor <c>CancelProcessingAsync</c> (kill + cancel).
    /// </summary>
    public bool Cancel(Guid operationId)
    {
        return _operationTracker.CancelOperation(operationId);
    }

    /// <summary>
    /// Force kill fallback when cancel alone does not unblock the UI (e.g. stuck managed post-processing).
    /// </summary>
    public async Task<bool> ForceKillAsync(Guid operationId)
    {
        var operation = _operationTracker.GetOperation(operationId);
        if (operation == null)
        {
            _logger.LogWarning("Force kill requested for unknown operation {Id}", operationId);
            return false;
        }

        _logger.LogWarning(
            "Force killing operation {Id} ({Type}: {Name})",
            operationId, operation.Type, operation.Name);

        // Capture the process BEFORE ForceKillOperation nulls AssociatedProcess.
        var process = operation.AssociatedProcess;

        try
        {
            if (process is { HasExited: false })
            {
                // P2-B / rust-kill-6: graceful-then-force, awaiting the REAL exit (replaces the blind
                // Task.Delay(500)). Writes "CANCEL" to stdin, waits up to the grace period, then escalates
                // to a hard kill and waits for the process tree to actually exit.
                await _processManager.GracefulCancelAsync(process, TimeSpan.FromSeconds(5), $"force-kill op {operationId}");
            }
        }
        catch (ObjectDisposedException)
        {
            // A racing CompleteOperation disposed the Process between capture and the HasExited
            // check (or during GracefulCancelAsync). The op is already finishing — treat as exited.
            _logger.LogDebug("Process for operation {Id} was disposed concurrently during force kill — treating as already exited", operationId);
        }

        _operationTracker.ForceKillOperation(operationId); // cancels token, best-effort kill (idempotent via HasExited)

        var current = _operationTracker.GetOperation(operationId);
        if (current == null
            || current.Status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled)
        {
            // The worker observed cancellation and already completed the op (A.3 flag). Avoid a duplicate
            // SignalR completion — the op is already terminal.
            return true;
        }

        // Every OperationType now registers an OnTerminalEmit, so CompleteOperation fires the terminal
        // SignalR event EXACTLY ONCE (CompletedFlag-gated) for the force-kill case too. No separate
        // force-kill notification path is needed.
        _operationTracker.CompleteOperation(operationId, success: false, error: "Force killed by user");
        return true;
    }
}
