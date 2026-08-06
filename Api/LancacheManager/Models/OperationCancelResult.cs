namespace LancacheManager.Models;

/// <summary>
/// What a cancel request actually did. A plain bool collapsed
/// <see cref="AlreadyFinished"/> into <see cref="Requested"/>, so the caller could not tell a
/// live operation being stopped from a cancel that arrived too late and did nothing. The
/// cancel endpoint needs that distinction to tell the browser its card is stale.
/// </summary>
public enum OperationCancelResult
{
    /// <summary>
    /// No operation with that ID is tracked. It either never existed or was already evicted by
    /// the tracker's cleanup reaper.
    /// </summary>
    NotFound,

    /// <summary>
    /// The operation is tracked but already terminal, so there was nothing left to cancel. The
    /// caller's intent is satisfied, but no work was stopped.
    /// </summary>
    AlreadyFinished,

    /// <summary>
    /// Cancellation was applied: the associated process tree was killed where present and the
    /// token was cancelled (or a cancel already in flight was re-attempted).
    /// </summary>
    Requested
}
