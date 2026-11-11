using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Thin wait-queue gate in front of the existing operation start paths.
///
/// Callers may enter the queue directly to atomically check-and-start, or after their own
/// <see cref="IOperationConflictChecker"/> pre-check. The queue re-checks under its
/// promotion mutex (the blocker may have finished before the enqueue committed), deduplicates
/// identical requests, or parks the operation as a
/// tracker-registered <see cref="OperationStatus.Waiting"/> op. When any operation reaches
/// a terminal state (tracker's OperationTerminal hook - fires for success, failure, cancel
/// AND force-kill alike) the queue promotes eligible waiters FIFO by invoking the stored
/// start delegate, which runs the operation's EXISTING start path unchanged.
///
/// Waiting operations do NOT survive an app restart: they never started any work, their
/// queue entries are in-memory only, and no recovery path restores them.
/// </summary>
public interface IOperationWaitlist
{
    /// <summary>
    /// Atomically start, park, or deduplicate an operation according to the current conflicts.
    /// </summary>
    /// <param name="type">Operation type (conflict-matrix identity).</param>
    /// <param name="scope">Conflict scope (bulk / service / entity).</param>
    /// <param name="displayName">Human-readable name shown in the tracker.</param>
    /// <param name="start">The operation's EXISTING start path. Invoked at promotion time
    /// (or immediately when the conflict vanished). Must return the started operation's id,
    /// or null when the start path internally refused. Must only capture singleton services
    /// or factories - it runs after the originating HTTP request has completed.</param>
    /// <param name="requestKey">What this request asks for, built by its job type's key
    /// (<see cref="OperationRequestKey"/>). A request joins a waiting or running one only when the two keys are
    /// equal; otherwise it waits on its own card and runs after. A key of another job type's record is refused.</param>
    /// <param name="reportRefusal">Whether a refusal thrown by <paramref name="start"/> before it
    /// starts needs announcing on its own. Leave it false wherever the exception reaches a caller
    /// that shows it, which is every HTTP route: the 400 IS the report, and announcing as well puts
    /// two notices on screen for one click. Pass true only from a caller that swallows the
    /// exception, where the announcement is the only thing the reader would ever see. Refusals at
    /// promotion time are unaffected and always reported, because by then nobody is waiting on a
    /// response.</param>
    /// <param name="target">The game, service or other target named in <paramref name="displayName"/>,
    /// carried on the waiting row so the browser can pair it with the reader's own title.</param>
    Task<OperationStartResponse> EnqueueAsync(
        OperationType type,
        ConflictScope scope,
        string displayName,
        Func<Task<Guid?>> start,
        CancellationToken ct,
        OperationRequestKey requestKey,
        bool reportRefusal = false,
        RunNotice? notice = null,
        string? target = null);

    /// <summary>
    /// Checks conflicts and runs <paramref name="start"/> under the same lock as <see cref="EnqueueAsync"/>,
    /// and returns the conflict without starting or parking. For a start whose caller answers a conflict
    /// itself. <paramref name="start"/> must return once the run is registered with the tracker, never
    /// after the run, because the lock is held until it returns. It returns the id of the run it registered,
    /// or null when it registered none; a start whose run no request ever joins may return null either way.
    /// </summary>
    /// <param name="requestKey">As for <see cref="EnqueueAsync"/>: recorded on the run whose id the start
    /// returned, so a later request joins it only when it asks for the same work.</param>
    Task<OperationConflictResponse?> StartUnlessBusyAsync(
        OperationType type,
        ConflictScope scope,
        Func<Task<Guid?>> start,
        CancellationToken ct,
        OperationRequestKey requestKey);
}
