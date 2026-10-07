using LancacheManager.Core.Interfaces;

namespace LancacheManager.Models;

/// <summary>
/// The notice a run is admitted with: its schedule's notification mode and what started it. It is
/// attached to the tracked operation at registration and alone decides how the run is drawn.
/// </summary>
public sealed class RunNotice(
    NotificationMode mode,
    RunTrigger trigger,
    ScheduleActor? actor = null,
    bool restoredOrigin = false)
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
    public ScheduleActor? Actor { get; internal set; } = actor;
    public bool RestoredOrigin { get; } = restoredOrigin;
    /// <summary>
    /// The detection scan type a person asked for on the Games page. A detection run on this notice's behalf,
    /// including one the schedule holds for a download, runs exactly this type; null means the schedule's own.
    /// </summary>
    public DetectionScanType? RequestedScanType
    {
        get { lock (_lock) return _requestedScanType; }
        init => _requestedScanType = value;
    }
    private DetectionScanType? _requestedScanType;
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

    public void Cancel(IUnifiedOperationTracker? tracker, Guid requestedId)
    {
        Interlocked.Exchange(ref _cancelled, 1);
        Guid? currentId;
        lock (_lock) currentId = OperationId;
        if (tracker is not null && currentId.HasValue && currentId != requestedId) tracker.CancelOperation(currentId.Value);
    }

    /// <summary>
    /// Ends this request's waiting card as handed to <paramref name="card"/>, the card of a run that does the
    /// work this request asked for. The request keeps its own card id, so a cancel aimed at that id stops only
    /// this card and never the other run. True with nothing changed when <paramref name="card"/> already is this
    /// request's card (the same request asked again). False, with nothing changed, when this request has no
    /// waiting card, was canceled, its card is no longer waiting, or the other run is ending.
    /// </summary>
    internal bool CloseInto(IUnifiedOperationTracker tracker, Guid card)
    {
        Guid waiting;
        lock (_lock)
        {
            if (OperationId is not { } id || Cancelled) return false;
            if (id == card) return true;
            waiting = id;
        }
        // Outside the notice lock: the tracker takes operation locks, and nothing takes one while holding a
        // notice lock.
        return tracker.TryCloseInto(waiting, card);
    }

    /// <summary>
    /// Makes this run answer <paramref name="dropped"/>'s request too, so one run does the work both
    /// asked for. A person's click raises this run's trigger, and the dropped request's waiting card
    /// closes into this run's card, or becomes this run's card when it has none. Taken under this
    /// notice's lock, so a run attaching its own operation at the same moment is never handed into
    /// the waiting card. False, with neither notice changed, when this run is canceled, its card is being
    /// canceled, or its card is ending and cannot take the request's waiting card in; the caller then keeps
    /// the request as a run of its own.
    /// </summary>
    internal bool Adopt(IUnifiedOperationTracker? tracker, RunNotice dropped)
    {
        Guid? keptCard;
        // Read before this notice's lock is taken: no notice takes another's lock while holding its own.
        var droppedScanType = dropped.RequestedScanType;
        lock (_lock)
        {
            keptCard = OperationId;
            // A run already canceled, or whose card a person is dismissing, cannot answer a new request:
            // the request would ride on a run that never happens.
            if (Cancelled || keptCard is { } ending && tracker?.GetOperation(ending)?.Cancelled == true) return false;
            if (keptCard is null && dropped.OperationId is { } droppedCard)
            {
                OperationId = droppedCard;
                PendingId = dropped.PendingId;
                Token = dropped.Token;
            }
        }

        // A handed-on waiting card ends as a join, never as a run of its own, so it adds no history row and
        // does not break the schedule's failure streak. A refusal while the request's card is still waiting
        // means this run's card is ending; a card that stopped waiting is being dismissed or already ended,
        // and its own ending needs nothing from this run.
        if (tracker is not null && keptCard is { } card && dropped.OperationId is { } waiting && waiting != card
            && !dropped.CloseInto(tracker, card)
            && tracker.GetOperation(waiting)?.Status == OperationStatus.Waiting)
        {
            return false;
        }

        bool raised;
        lock (_lock)
        {
            // The merged run still performs the scan the person pressed, also when it is held for a download
            // and the schedule's own type is resolved again later. Only requests for the same scan type join,
            // so a type this run already has is the dropped one's.
            _requestedScanType ??= droppedScanType;
            raised = !dropped.Cancelled && Rank(dropped.Trigger) > Rank(Trigger);
            if (raised)
            {
                Trigger = dropped.Trigger;
                Actor = dropped.Actor;
            }
        }
        if (raised && tracker is not null && OperationId is { } current) tracker.RefreshRun(current);
        return true;
    }

    // A person's own click outranks Run All, which outranks a run nobody asked for.
    private static int Rank(RunTrigger trigger) => trigger switch
    {
        RunTrigger.Manual => 2,
        RunTrigger.RunAll => 1,
        _ => 0,
    };
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
