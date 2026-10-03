using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Infrastructure.Services;

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
    private readonly OperationStateService _operationStateService;
    private readonly ILogger<OperationCancellationService> _logger;

    public OperationCancellationService(
        IUnifiedOperationTracker operationTracker,
        ProcessManager processManager,
        OperationStateService operationStateService,
        ILogger<OperationCancellationService> logger)
    {
        _operationTracker = operationTracker;
        _processManager = processManager;
        _operationStateService = operationStateService;
        _logger = logger;
    }

    /// <summary>
    /// Aggressive cancel — terminates any associated process tree, then cancels the token.
    /// Delegates to <see cref="IUnifiedOperationTracker.CancelOperation"/>, which writes
    /// <c>CANCEL</c> to the Rust child's stdin (graceful exit) and then cancels the managed
    /// <see cref="System.Threading.CancellationToken"/> — the universal cancel path exposed via
    /// <c>POST /operations/{id}/cancel</c>.
    /// </summary>
    public OperationCancelResult Cancel(Guid operationId)
    {
        return _operationTracker.CancelOperation(operationId);
    }

    public OperationCancelResult Cancel(Guid operationId, IntegrationCaller caller)
    {
        var operation = _operationTracker.GetOperation(operationId, followHandoff: true);
        if (operation is null) return OperationCancelResult.NotFound;
        lock (operation)
        {
            ValidateCaller(operation, caller);
            return _operationTracker.CancelOperation(operation.Id, followHandoff: false);
        }
    }

    private static void ValidateCaller(OperationInfo operation, IntegrationCaller caller)
    {
        if (UnifiedOperationTracker.ReadIntegrationLogin(operation.Metadata) is { } login)
            IntegrationLease.ValidateCaller(login, caller);
    }

    /// <summary>
    /// Force kill fallback when cancel alone does not unblock the UI (e.g. stuck managed post-processing).
    /// </summary>
    public Task<bool> ForceKillAsync(Guid operationId) => ForceKillAsync(operationId, null);

    public async Task<bool> ForceKillAsync(Guid operationId, IntegrationCaller? caller)
    {
        var operation = _operationTracker.GetOperation(operationId, followHandoff: true);
        if (operation == null)
        {
            _logger.LogWarning("Force kill requested for unknown operation {Id}", operationId);
            return false;
        }

        operationId = operation.Id;
        System.Diagnostics.Process? process;
        lock (operation)
        {
            if (caller is not null) ValidateCaller(operation, caller);
            if (operation.CompletedFlag != 0 || operation.Status.IsTerminal()) return true;
            process = operation.AssociatedProcess;
        }

        _logger.LogWarning(
            "Force killing operation {Id} ({Type}: {Name})",
            operationId, operation.Type, operation.Name);

        // Cancel the run and its token before the child is stopped, in the order CancelOperation uses:
        // a child stopped while the token is live reads as a failed exit, and the run would end red.
        _operationTracker.ForceKillOperation(operationId, followHandoff: caller is null);

        try
        {
            if (process is { HasExited: false })
            {
                // ForceKillOperation above already canceled the token and killed the process tree, so the CANCEL line this
                // writes reaches no reader; the call waits up to the grace period for the real exit and kills the tree again if
                // it is still alive.
                await _processManager.GracefulCancelAsync(process, TimeSpan.FromSeconds(5), $"force-kill op {operationId}");
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // A racing CompleteOperation disposed the Process between capture and the HasExited
            // check (or during GracefulCancelAsync). The op is already finishing — treat as exited.
            // A disposed Process reports that state as InvalidOperationException from every member,
            // HasExited included, so the ObjectDisposedException arm alone never caught this.
            _logger.LogDebug(ex, "Process for operation {Id} was disposed concurrently during force kill — treating as already exited", operationId);
        }

        var current = _operationTracker.GetOperation(operationId);
        if (current == null || current.Status.IsTerminal())
        {
            // The worker observed cancellation and already completed the op (A.3 flag). Avoid a duplicate
            // SignalR completion — the op is already terminal.
            return true;
        }

        // Force stop ends the job but never its repair: the outcome is recorded first, so the card
        // turns to repairing (or ends when nothing is owed) at once. A job that saved its own outcome before
        // this force stop completes its run itself, a few statements after that save.
        if (_operationStateService.OwnsRepair(operationId)
            && await _operationStateService.RecordForceStopAsync(operationId))
        {
            return true;
        }
        _operationTracker.CompleteOperation(operationId, success: false, error: "Force killed by user", cancelled: true);
        return true;
    }
}
