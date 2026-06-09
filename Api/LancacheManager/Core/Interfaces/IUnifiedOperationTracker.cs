using System.Diagnostics;
using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

public interface IUnifiedOperationTracker
{
    /// <summary>
    /// Registers a new operation and returns its unique ID.
    /// </summary>
    /// <remarks>
    /// CTS OWNERSHIP: once a <see cref="CancellationTokenSource"/> is handed to the tracker it is
    /// owned by the tracker. The tracker is the SINGLE disposer — it disposes the CTS exactly once
    /// inside <see cref="CompleteOperation"/>. Callers MUST NOT dispose a CTS they have passed to a
    /// successfully-registered operation. The lone exception is when <see cref="TryRestoreOperation"/>
    /// returns <c>false</c> (the ID was already in use and the just-created CTS was never adopted):
    /// the caller still owns that CTS and must dispose it itself.
    /// </remarks>
    /// <param name="onTerminalCleanup">Optional synchronous callback invoked exactly once when the
    /// operation reaches a terminal state, letting the owning service reset its local mutable state
    /// (e.g. null out its <c>_currentOperationId</c>/<c>_cts</c>) regardless of which path completed
    /// the op (worker <c>finally</c> vs universal force-kill). Must not throw or block.</param>
    /// <param name="onTerminalEmit">Optional callback invoked EXACTLY ONCE inside
    /// <see cref="CompleteOperation"/> (CompletedFlag-gated), fire-and-forget, so the owning service
    /// emits its terminal SignalR event from a single place regardless of which path completed the op
    /// (worker success, worker OCE-catch, or universal force-kill). Receives a strongly-typed
    /// <see cref="OperationTerminalInfo"/>. Must not throw (exceptions are swallowed/logged).</param>
    Guid RegisterOperation(OperationType type, string name, CancellationTokenSource cts,
                           object? metadata = null, Action? onTerminalCleanup = null,
                           Func<OperationTerminalInfo, Task>? onTerminalEmit = null);

    /// <summary>
    /// Re-registers a previously-persisted operation by its original ID (recovery after restart).
    /// Returns true if the operation was registered; false if the ID is already in use
    /// (caller should treat as benign - the operation is already tracked).
    /// </summary>
    /// <remarks>
    /// CTS OWNERSHIP: see <see cref="RegisterOperation"/>. When this returns <c>false</c> the tracker
    /// did NOT adopt the supplied CTS, so the caller retains ownership and must dispose it.
    /// </remarks>
    /// <param name="onTerminalCleanup">See <see cref="RegisterOperation"/>.</param>
    /// <param name="onTerminalEmit">See <see cref="RegisterOperation"/>.</param>
    bool TryRestoreOperation(Guid operationId, OperationType type, string name, CancellationTokenSource cts,
                             object? metadata = null, Action? onTerminalCleanup = null,
                             Func<OperationTerminalInfo, Task>? onTerminalEmit = null);

    /// <summary>
    /// Aggressively cancels an operation: terminates any associated process tree immediately,
    /// then cancels the operation's <see cref="CancellationTokenSource"/>.
    /// Idempotent — repeated calls re-attempt process termination.
    /// Returns false only when the operation was not found or has no cancellation source.
    /// </summary>
    bool CancelOperation(Guid operationId);

    /// <summary>
    /// Associates a running OS process with an operation so cancel/force-kill can terminate it.
    /// </summary>
    void AssociateProcess(Guid operationId, Process process);

    /// <summary>
    /// Clears the associated process reference when it has exited or been superseded.
    /// </summary>
    void DisassociateProcess(Guid operationId, Process process);

    /// <summary>
    /// Force kills the associated process for an operation (if any) and cancels its token.
    /// Returns true when the operation was found (even if no process was running).
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
    /// Backward-compat wrapper over the kind-prefixed index - probes
    /// <c>steam:</c>/<c>epic:</c>/<c>service:</c> variants in priority order based on <paramref name="type"/>.
    /// New call sites should prefer <see cref="GetOperationByScope"/>.
    /// </summary>
    OperationInfo? GetOperationByEntityKey(OperationType type, string entityKey);

    /// <summary>
    /// Look up an operation by its canonical <see cref="ConflictScope"/>. Preferred over
    /// <see cref="GetOperationByEntityKey"/> - uses an unambiguous <c>kind:key</c> lookup
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
