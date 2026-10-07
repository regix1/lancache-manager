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
/// Queued operations do NOT survive an app restart: they never started any work, their
/// queue entries are in-memory only, and no recovery path restores them.
/// </summary>
public interface IOperationQueue
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
    /// <param name="reportRefusal">Whether a refusal thrown by <paramref name="start"/> before it
    /// starts needs announcing on its own. Leave it false wherever the exception reaches a caller
    /// that shows it, which is every HTTP route: the 400 IS the report, and announcing as well puts
    /// two notices on screen for one click. Pass true only from a caller that swallows the
    /// exception, where the announcement is the only thing the reader would ever see. Refusals at
    /// promotion time are unaffected and always reported, because by then nobody is waiting on a
    /// response.</param>
    /// <param name="target">The game, service or other target named in <paramref name="displayName"/>,
    /// carried on the waiting row so the browser can pair it with the reader's own title.</param>
    /// <param name="scanMode">The structural scan mode of a corruption scan request; null for a repeated-miss
    /// scan and for every other type.</param>
    /// <param name="scanThreshold">The miss threshold a corruption scan request runs with; null for every
    /// other type.</param>
    /// <param name="scanLookbackDays">The lookback window, in days, a corruption scan request runs with; null
    /// for every other type. Two corruption scan requests are the same scan only when mode, threshold and
    /// lookback all match, as the scan service decides.</param>
    /// <param name="detectionScanType">The scan a game detection request performs; null for every other type.</param>
    Task<QueuedOperationResponse> EnqueueAsync(
        OperationType type,
        ConflictScope scope,
        string displayName,
        Func<Task<Guid?>> start,
        CancellationToken ct,
        bool reportRefusal = false,
        RunNotice? notice = null,
        string? target = null,
        StructuralScanMode? scanMode = null,
        int? scanThreshold = null,
        int? scanLookbackDays = null,
        DetectionScanType? detectionScanType = null);
}
