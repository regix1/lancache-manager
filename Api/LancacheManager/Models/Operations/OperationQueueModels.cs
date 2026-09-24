using LancacheManager.Core.Interfaces;

namespace LancacheManager.Models;

/// <summary>
/// The notice a run is admitted with: its schedule's notification mode and what started it. It is
/// attached to the tracked operation at registration and alone decides how the run is drawn.
/// </summary>
public sealed class RunNotice(NotificationMode mode, RunTrigger trigger)
{
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
    public bool HideNotification => Mode == NotificationMode.Hidden;

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
}

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
