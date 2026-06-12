namespace LancacheManager.Models;

/// <summary>
/// SignalR payload emitted when an operation is parked in the wait-queue behind a
/// conflicting operation. The frontend renders the purple "waiting" card from this.
/// <see cref="OperationType"/> is the camelCase wire string (e.g. "cacheClearing").
/// </summary>
public record OperationWaitingNotification(Guid OperationId, string OperationType, string Name);

/// <summary>
/// SignalR payload emitted when a WAITING operation terminates WITHOUT being promoted
/// (cancelled from the card, or its start delegate failed at promotion time).
/// Promotion itself emits nothing here - the promoted operation's own Started event
/// replaces the waiting card.
/// </summary>
public record OperationWaitingCompleteNotification(
    Guid OperationId,
    string OperationType,
    bool Cancelled,
    string? Error = null);
