using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

public interface IUnifiedOperationTracker
{
    /// <summary>
    /// Registers a new operation and returns its unique ID.
    /// </summary>
    Guid RegisterOperation(OperationType type, string name, CancellationTokenSource cts, object? metadata = null);

    /// <summary>
    /// Re-registers a previously-persisted operation by its original ID (recovery after restart).
    /// Returns true if the operation was registered; false if the ID is already in use
    /// (caller should treat as benign — the operation is already tracked).
    /// </summary>
    bool TryRestoreOperation(Guid operationId, OperationType type, string name, CancellationTokenSource cts, object? metadata = null);

    /// <summary>
    /// Cancels an operation by requesting cancellation on its CancellationTokenSource.
    /// Returns true if cancellation was requested or is already in progress (idempotent).
    /// Returns false if the operation was not found.
    /// </summary>
    bool CancelOperation(Guid operationId);

    /// <summary>
    /// Force kills the associated process for an operation (if any).
    /// Returns true if the process was killed, false if no operation or process found.
    /// </summary>
    bool ForceKillOperation(Guid operationId);

    /// <summary>
    /// Gets information about a specific operation.
    /// Returns null if the operation is not found.
    /// </summary>
    OperationInfo? GetOperation(Guid operationId);

    /// <summary>
    /// Gets all active operations, optionally filtered by type.
    /// </summary>
    IEnumerable<OperationInfo> GetActiveOperations(OperationType? filterType = null);

    /// <summary>
    /// Marks an operation as complete and cleans up resources.
    /// </summary>
    void CompleteOperation(Guid operationId, bool success, string? error = null);

    /// <summary>
    /// Updates the progress of an operation.
    /// </summary>
    void UpdateProgress(Guid operationId, double percent, string message);

    /// <summary>
    /// Look up an operation by its entity key (e.g., appId for games, serviceName for services).
    /// Uses the secondary index maintained internally.
    /// Backward-compat wrapper over the kind-prefixed index — probes
    /// <c>steam:</c>/<c>epic:</c>/<c>service:</c> variants in priority order based on <paramref name="type"/>.
    /// New call sites should prefer <see cref="GetOperationByScope"/>.
    /// </summary>
    OperationInfo? GetOperationByEntityKey(OperationType type, string entityKey);

    /// <summary>
    /// Look up an operation by its canonical <see cref="ConflictScope"/>. Preferred over
    /// <see cref="GetOperationByEntityKey"/> — uses an unambiguous <c>kind:key</c> lookup
    /// so a <c>ServiceRemoval</c> on service "steam" cannot collide with a hypothetical
    /// steam appId.
    /// </summary>
    OperationInfo? GetOperationByScope(OperationType type, ConflictScope scope);

    /// <summary>
    /// Update the metadata object on an existing operation.
    /// Used by removal operations to push FilesDeleted/BytesFreed into the tracker.
    /// </summary>
    void UpdateMetadata(Guid operationId, Action<object> updater);
}
