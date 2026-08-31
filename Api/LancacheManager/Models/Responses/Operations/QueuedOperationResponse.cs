namespace LancacheManager.Models;

/// <summary>
/// Accepted-response body returned by endpoints that used to reply 409 on operation
/// conflicts. Under the wait-queue model a conflicting request is never rejected:
/// it is parked (<see cref="Queued"/> = true, <see cref="OperationId"/> = the waiting
/// operation's id, cancellable via the universal cancel endpoint), deduplicated
/// (<see cref="AlreadyRunning"/> = true, id of the existing identical operation), or
/// started immediately when the blocker finished before the enqueue committed.
/// </summary>
public sealed class QueuedOperationResponse
{
    public Guid OperationId { get; init; }

    /// <summary>True when the operation was parked in the wait-queue.</summary>
    public bool Queued { get; init; }

    /// <summary>True when an identical operation (same type + scope) was already active or queued.</summary>
    public bool AlreadyRunning { get; init; }

    /// <summary>"waiting" | "started" | "alreadyRunning" | "skipped" - convenience mirror of the
    /// flags. "skipped" is the same word the operation tracker already puts on the wire for a run
    /// that stopped without doing anything, so one idea keeps one name.</summary>
    public string Status { get; init; } = "waiting";

    /// <summary>Why nothing was started, set only when <see cref="Status"/> is "skipped". Null on
    /// every other status.</summary>
    public string? SkippedReason { get; init; }
}
