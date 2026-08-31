namespace LancacheManager.Models;

/// <summary>
/// SignalR payload emitted when an operation is parked in the wait-queue behind a
/// conflicting operation. The frontend renders the purple "waiting" card from this.
/// <see cref="OperationType"/> is the camelCase wire string (e.g. "cacheClearing").
/// <see cref="BlockedByName"/> is the display name of the operation currently holding the
/// conflict (null when the blocker is unknown, e.g. a local start-gate refusal). The queue
/// re-emits this event with the new blocker when a waiter stays parked behind a different
/// operation after its previous blocker finished, so the card always names the current one.
/// </summary>
public record OperationWaitingNotification(
    Guid OperationId,
    string OperationType,
    string Name,
    string? BlockedByName = null);

/// <summary>
/// Marks an operation that exists only to report a run the server declined before it started, so it
/// has no terminal broadcast of its own. Whoever knows which card the operation's schedule owns
/// watches for this on the tracker's terminal hook and sends that broadcast; without the flag such an
/// operation is indistinguishable from one that already reported itself, and would be announced twice.
/// </summary>
public static class DeclinedRunMetadata
{
    public const string Key = "declinedBeforeStart";
}

/// <summary>
/// SignalR payload emitted when a WAITING operation terminates. A promoted operation normally
/// replaces the waiting card with its own Started event; the explicit <see cref="Promoted"/>
/// flag also lets the frontend remove the waiting card when the promoted operation is intentionally
/// notification-silent.
///
/// <see cref="Promoted"/> and <see cref="Skipped"/> are mutually exclusive and say different things:
/// promoted means a real operation started and took over the card, skipped means the run was declined
/// and never started at all. A skipped run's reason travels in <see cref="Error"/>, the same field the
/// failure path uses, because a card that is removed rather than explained tells the reader nothing.
/// </summary>
public record OperationWaitingCompleteNotification(
    Guid OperationId,
    string OperationType,
    bool Cancelled,
    string? Error = null,
    bool Promoted = false,
    bool Skipped = false);
