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
    /// <param name="initialStatus">Initial status for the registered operation. Defaults to
    /// <see cref="OperationStatus.Running"/>; the operation wait-queue registers parked ops as
    /// <see cref="OperationStatus.Waiting"/> (excluded from <see cref="GetActiveOperations"/>).</param>
    Guid RegisterOperation(OperationType type, string name, CancellationTokenSource cts,
                           object? metadata = null, Action? onTerminalCleanup = null,
                           Func<OperationTerminalInfo, Task>? onTerminalEmit = null,
                           OperationStatus initialStatus = OperationStatus.Running);

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
    /// The result says whether anything was actually stopped, so a caller can tell a live
    /// operation being cancelled from a request that arrived after it finished.
    /// A cancel aimed at an operation that has handed its work on (see
    /// <see cref="RecordHandoff"/>) is forwarded to whichever operation is actually running.
    /// </summary>
    OperationCancelResult CancelOperation(Guid operationId);

    /// <summary>
    /// Records that <paramref name="fromOperationId"/> handed its work to
    /// <paramref name="toOperationId"/>, so a cancel or force-kill aimed at the old id reaches the
    /// operation now doing the work.
    ///
    /// The wait-queue needs this because a parked operation and the operation it is promoted into
    /// are two different tracker registrations with two different tokens. The user's card still
    /// carries the parked id while the promotion runs, so without the handoff a cancel clicked
    /// during that window is answered with a success the code cannot honour: the parked operation
    /// ends as cancelled while the work it started runs to completion.
    /// </summary>
    void RecordHandoff(Guid fromOperationId, Guid toOperationId);

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
    /// Excludes <see cref="OperationStatus.Waiting"/> operations: queued ops have not started
    /// any work, must not block conflict checks, and must stay invisible to the per-type
    /// recovery/status endpoints that treat "active" as "actually running".
    /// </summary>
    IEnumerable<OperationInfo> GetActiveOperations(OperationType? filterType = null);

    /// <summary>
    /// Gets all operations currently parked in <see cref="OperationStatus.Waiting"/> state
    /// (the operation wait-queue). Used by the waiting-card recovery endpoint.
    /// </summary>
    IEnumerable<OperationInfo> GetWaitingOperations();

    /// <summary>
    /// Raised exactly once per operation when it reaches a terminal state (fired from
    /// <see cref="CompleteOperation"/> after the CompletedFlag gate, fire-and-forget).
    /// The operation wait-queue subscribes to promote the next eligible queued operation.
    /// Handlers must not throw; invocation is wrapped defensively.
    /// </summary>
    event Action<OperationInfo>? OperationTerminal;

    /// <summary>
    /// Marks an operation as complete and cleans up resources.
    /// </summary>
    /// <param name="skipped">True when the run started, found nothing to do, and stopped. The
    /// operation becomes <see cref="OperationStatus.Skipped"/>, which is terminal: it raises
    /// <see cref="OperationTerminal"/> and promotes the wait-queue exactly like a completion.
    /// Pass it together with <paramref name="success"/> <c>true</c>, because a skipped run did
    /// not fail and must stay out of the failure funnel.</param>
    void CompleteOperation(
        Guid operationId,
        bool success,
        string? error = null,
        bool cancelled = false,
        bool skipped = false);

    /// <summary>
    /// Updates the progress of an operation.
    /// </summary>
    void UpdateProgress(Guid operationId, double percent, string message);

    /// <summary>
    /// Look up an operation by its canonical <see cref="ConflictScope"/>. Uses an unambiguous
    /// <c>kind:key</c> lookup so a <c>ServiceRemoval</c> on service "steam" cannot collide with a
    /// hypothetical steam appId.
    /// </summary>
    OperationInfo? GetOperationByScope(OperationType type, ConflictScope scope);

    /// <summary>
    /// Update the metadata object on an existing operation.
    /// Used by removal operations to push FilesDeleted/BytesFreed into the tracker.
    /// </summary>
    void UpdateMetadata(Guid operationId, Action<object> updater);
}
