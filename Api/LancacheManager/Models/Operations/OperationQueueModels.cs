using LancacheManager.Core.Interfaces;

namespace LancacheManager.Models;

/// <summary>
/// SignalR payload emitted when an operation is parked in the wait-queue behind a
/// conflicting operation. The frontend renders the purple "waiting" card from this.
/// <see cref="OperationType"/> is the camelCase wire string (e.g. "cacheClearing").
/// <see cref="BlockedByName"/> is the display name of the operation currently holding the
/// conflict (null when the blocker is unknown, e.g. a local start-gate refusal). The queue
/// re-emits this event with the new blocker when a waiter stays parked behind a different
/// operation after its previous blocker finished, so the card always names the current one.
///
/// <see cref="Silent"/> marks a run that uses background progress instead of a card.
/// <see cref="Hidden"/> marks a run that never enters notification state. Both wait exactly like
/// visible runs, and the visibility fields remain stable when the blocker changes.
/// </summary>
public record OperationWaitingNotification(
    Guid OperationId,
    string OperationType,
    string Name,
    string? BlockedByName = null,
    bool Silent = false,
    bool Hidden = false,
    bool? Acknowledge = null);

/// <summary>
/// Marks an operation that exists only to report a run the server declined before it started, so it
/// has no terminal broadcast of its own. Whoever knows which card the operation's schedule owns
/// watches for this on the tracker's terminal hook and sends that broadcast; without the flag such an
/// operation is indistinguishable from one that already reported itself, and would be announced twice.
/// </summary>
public sealed class RunNotice(NotificationMode mode, RunTrigger trigger)
{
    private int _acknowledged;
    private int _cancelled;
    private readonly object _lock = new();
    public Guid? OperationId { get; private set; }
    public Guid? PendingId { get; internal set; }
    /// <summary>
    /// The operation this run was parked behind, recorded when the queue promotes it.
    /// A scan uses it to reuse that operation's results instead of starting the same work again.
    /// </summary>
    public Guid? BlockedByOperationId { get; internal set; }
    public CancellationToken Token { get; internal set; }
    public bool Cancelled => Volatile.Read(ref _cancelled) != 0;
    public NotificationMode Mode { get; } = mode;
    public RunTrigger Trigger { get; internal set; } = trigger;
    public bool ShowNotification => Mode.AllowsTrigger(Trigger);
    public bool HideNotification =>
        Mode == NotificationMode.Hidden ||
        (Mode == NotificationMode.Manual && Trigger != RunTrigger.Manual);
    public bool TryAcknowledge() => Interlocked.Exchange(ref _acknowledged, 1) == 0;

    public void Attach(IUnifiedOperationTracker tracker, Guid operationId)
    {
        Guid? previousId;
        bool waiting;
        bool cancelled;
        lock (_lock)
        {
            previousId = OperationId;
            if (previousId == operationId) return;
            var previous = previousId.HasValue ? tracker.GetOperation(previousId.Value) : null;
            waiting = previous?.Status == OperationStatus.Waiting;
            cancelled = Cancelled || previous?.Cancelled == true;
            if (previousId.HasValue) tracker.RecordHandoff(previousId.Value, operationId);
            OperationId = operationId;
        }

        if (waiting && previousId.HasValue) tracker.CompleteOperation(previousId.Value, success: true);
        if (cancelled || Cancelled) tracker.CancelOperation(operationId);
    }

    public void Cancel(IUnifiedOperationTracker tracker, Guid requestedId)
    {
        Interlocked.Exchange(ref _cancelled, 1);
        Guid? currentId;
        lock (_lock) currentId = OperationId;
        if (currentId.HasValue && currentId != requestedId) tracker.CancelOperation(currentId.Value);
    }

    internal static RunNotice? ReadRunNotice(object? state) => state switch
    {
        RunNotice notice => notice,
        GameDetectionMetrics metrics => metrics.Notice,
        IReadOnlyDictionary<string, object?> values when values.TryGetValue("runNotice", out var value) => value as RunNotice,
        IDictionary<string, object> values when values.TryGetValue("runNotice", out var value) => value as RunNotice,
        _ => null
    };
}

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
    bool Skipped = false,
    Guid? NextOperationId = null,
    string? NextStatus = null,
    bool Hidden = false);
