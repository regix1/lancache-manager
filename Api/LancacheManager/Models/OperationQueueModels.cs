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
/// SignalR payload emitted when a WAITING operation terminates. A promoted operation normally
/// replaces the waiting card with its own Started event; the explicit <see cref="Promoted"/>
/// flag also lets the frontend remove the waiting card when the promoted operation is intentionally
/// notification-silent.
/// </summary>
public record OperationWaitingCompleteNotification(
    Guid OperationId,
    string OperationType,
    bool Cancelled,
    string? Error = null,
    bool Promoted = false);
