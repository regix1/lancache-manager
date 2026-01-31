using LancacheManager.Core.Models;

namespace LancacheManager.Core.Interfaces;

public interface IUnifiedOperationTracker
{
    /// <summary>
    /// Registers a new operation and returns its unique ID.
    /// </summary>
    string RegisterOperation(OperationType type, string name, CancellationTokenSource cts, object? metadata = null);

    /// <summary>
    /// Cancels an operation by requesting cancellation on its CancellationTokenSource.
    /// Returns true if cancellation was requested or is already in progress (idempotent).
    /// Returns false if the operation was not found.
    /// </summary>
    bool CancelOperation(string operationId);

    /// <summary>
    /// Force kills the associated process for an operation (if any).
    /// Returns true if the process was killed, false if no operation or process found.
    /// </summary>
    bool ForceKillOperation(string operationId);

    /// <summary>
    /// Gets information about a specific operation.
    /// Returns null if the operation is not found.
    /// </summary>
    OperationInfo? GetOperation(string operationId);

    /// <summary>
    /// Gets all active operations, optionally filtered by type.
    /// </summary>
    IEnumerable<OperationInfo> GetActiveOperations(OperationType? filterType = null);

    /// <summary>
    /// Marks an operation as complete and cleans up resources.
    /// </summary>
    void CompleteOperation(string operationId, bool success, string? error = null);

    /// <summary>
    /// Updates the progress of an operation.
    /// </summary>
    void UpdateProgress(string operationId, double percent, string message);
}
