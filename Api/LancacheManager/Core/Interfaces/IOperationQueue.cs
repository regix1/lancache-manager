using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Thin wait-queue gate in front of the existing operation start paths.
///
/// Controllers keep their own <see cref="IOperationConflictChecker"/> pre-check; when it
/// reports a conflict they call <see cref="EnqueueAsync"/> instead of returning 409. The
/// queue re-checks under its promotion mutex (the blocker may have finished before the
/// enqueue committed), deduplicates identical requests, or parks the operation as a
/// tracker-registered <see cref="OperationStatus.Waiting"/> op. When any operation reaches
/// a terminal state (tracker's OperationTerminal hook - fires for success, failure, cancel
/// AND force-kill alike) the queue promotes eligible waiters FIFO by invoking the stored
/// start delegate, which runs the operation's EXISTING start path unchanged.
///
/// Queued operations do NOT survive an app restart: they never started any work, their
/// queue entries are in-memory only, and no recovery path restores them.
/// </summary>
public interface IOperationQueue
{
    /// <summary>
    /// Park (or deduplicate, or immediately start) an operation that hit a conflict.
    /// </summary>
    /// <param name="type">Operation type (conflict-matrix identity).</param>
    /// <param name="scope">Conflict scope (bulk / service / entity).</param>
    /// <param name="displayName">Human-readable name shown in the tracker.</param>
    /// <param name="start">The operation's EXISTING start path. Invoked at promotion time
    /// (or immediately when the conflict vanished). Must return the started operation's id,
    /// or null when the start path internally refused. Must only capture singleton services
    /// or factories - it runs after the originating HTTP request has completed.</param>
    Task<QueuedOperationResponse> EnqueueAsync(
        OperationType type,
        ConflictScope scope,
        string displayName,
        Func<Task<Guid?>> start,
        CancellationToken ct);
}
