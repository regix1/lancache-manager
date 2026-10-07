using System.Threading.Channels;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.Scheduling;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services.Base;

/// <summary>
/// Shared foundation for the scheduled-service base classes. Owns the parts of a scheduled service
/// that do not depend on how its schedule is stored or advanced: the user-facing run-on-startup and
/// notification preferences, run tracking, the run queue that holds Run Now, Run All, owed and held runs,
/// and the state-store load helper.
///
/// The interruptible-sleep machinery (the delay token source, the change flag and the wake calls)
/// also lives here, shared by both loops; each loop keeps only its own interval STORAGE, guarded
/// by the shared <see cref="IntervalLock"/>. The two loop implementations
/// (<see cref="ScheduledBackgroundService"/> and <see cref="ConfigurableScheduledService"/>) each
/// declare their own <see cref="DefaultRunOnStartup"/>: the two hierarchies ship different values,
/// so this class leaves the property abstract rather than picking a default that would silently
/// change startup behaviour for whichever hierarchy did not expect it.
/// </summary>
public abstract class ScheduledServiceBase : BackgroundService
{
    protected readonly ILogger _logger;

    // The notice of the run the loop thread is executing, read by the work it calls. Only the loop
    // thread writes it, from the answers of the run queue below.
    private RunNotice? _currentNotice;
    protected virtual bool QueueManualRuns => true;

    /// <summary>
    /// Whether the schedule is doing work a new request would only repeat. <paramref name="taken"/> is the
    /// run the loop took, null when idle. A loop whose pass is mostly a poll, or whose run outlives the pass
    /// that started it, overrides this. Read on the run queue's reader, so it reads only the tracker.
    /// </summary>
    protected internal virtual bool IsDoingWork(RunNotice? taken) => taken is not null;

    // The schedule's run queue: one reader task owns every request, owed run, held run, the run the
    // loop took, and the waiting cards that stand for them. Every caller posts a message and awaits
    // the answer, so two requests can never see the schedule half-changed by each other. The fields
    // below are read and written only by that reader.
    private readonly Channel<RunMessage> _runMessages =
        Channel.CreateUnbounded<RunMessage>(new UnboundedChannelOptions { SingleReader = true });
    // Requests and held runs are kept one per scan type: slot 0 is the schedule's own run, slots 1 and 2 are
    // the detection scans a person asked for by type. A request never joins one of another type, and the
    // highest slot (Full) is taken and released first.
    private const int SlotCount = 3;
    private int SlotOf(RunNotice notice) => ScanTypeOf(notice) is { } type ? (int)type + 1 : 0;

    // A run that names no scan type is the schedule's own, and its slot follows the schedule's type now. One left
    // in another slot by a scan mode change moves to it first, so a second own run joins it and a person's
    // request of the type it was asked under does not. A request of its new type already there takes it in.
    private void MoveOwnRun(RunNotice?[] slots)
    {
        for (var index = 0; index < SlotCount; index++)
        {
            if (slots[index] is not { Cancelled: false, RequestedScanType: null } own || SlotOf(own) == index) continue;
            slots[index] = null;
            var slot = SlotOf(own);
            if (slots[slot] is { Cancelled: false } waiting && waiting.Adopt(Tracker, own)) continue;
            slots[slot] = own;
        }
    }

    /// <summary>
    /// The detection scan type a run on <paramref name="notice"/> would perform, or null for a schedule that
    /// does not run detection scans. A detection schedule also answers for a run that did not name a type,
    /// with the type its own settings give it now, so the schedule's run and a person's request of the same
    /// type are one run and the Full scan always goes first. Read on the run queue's reader, so it reads
    /// only settings.
    /// </summary>
    protected internal virtual DetectionScanType? ScanTypeOf(RunNotice notice) => notice.RequestedScanType;

    /// <summary>
    /// Whether <paramref name="notice"/> asks for a detection scan of a type other than the schedule's own.
    /// Such a request is an extra scan: it never stands in for the schedule's own pass, and running it leaves
    /// the schedule's countdown and its due run alone. Read on the run queue's reader, so it reads only settings.
    /// </summary>
    protected bool IsExtraRun(RunNotice notice)
        => notice.RequestedScanType is { } type
            && type != ScanTypeOf(new RunNotice(EffectiveNotificationMode, RunTrigger.Scheduled));

    // A request a person or Run All made, waiting for the loop to take it.
    private readonly RunNotice?[] _requested = new RunNotice?[SlotCount];
    // A run the schedule was due for and was refused, owed once the refusal clears.
    private RunNotice? _owed;
    // A run held for a client download, on its waiting card, until downloads end.
    private readonly RunNotice?[] _held = new RunNotice?[SlotCount];
    // The run the loop took, from the moment it took it until it ends or its gate turns it away.
    private RunNotice? _running;
    private RunPhase _phase;

    private enum RunPhase
    {
        Idle,
        // Taken, and not past its gate yet: a held run released now is this run's own work.
        Starting,
        // Past its gate and doing the work.
        Running,
    }

    /// <summary>
    /// The tracker behind every waiting card this schedule shows. Set once by the schedule registry;
    /// null for a loop no registry owns, whose requests have no cards.
    /// </summary>
    internal IUnifiedOperationTracker? Tracker { get; set; }
    public RunNotice CurrentRunNotice
    {
        get => _currentNotice ??= new RunNotice(EffectiveNotificationMode, CurrentRunTrigger);
        private set => _currentNotice = value;
    }

    internal void SelectRunNotice(RunNotice notice)
    {
        CurrentRunNotice = notice;
        CurrentRunTrigger = notice.Trigger;
    }

    protected void SelectRunNotice(RunTrigger trigger, RunNotice? notice = null)
    {
        _currentNotice = notice ?? new RunNotice(EffectiveNotificationMode, trigger);
        CurrentRunTrigger = CurrentRunNotice.Trigger;
    }

    /// <summary>
    /// Asks the schedule to run <paramref name="notice"/>. <paramref name="admitted"/> runs on the run
    /// queue inside the admission, with the request that will run and whether it waits behind a busy run,
    /// so a card it raises exists before this answers.
    /// </summary>
    public Task<Admission> TryTriggerImmediateRunAsync(RunNotice notice, Action<RunNotice, bool>? admitted = null)
        => AskAsync<Admission>(reply => new RequestRun(notice, admitted, reply));

    /// <summary>
    /// Reports a run's end: the error it failed with, whether it was canceled, and whether the run is
    /// still kept for later (held or queued again), in which case its waiting card stays.
    /// </summary>
    internal Action<RunNotice, string, string?, bool, bool>? RunCompleted { get; set; }

    /// <summary>
    /// Holds <paramref name="notice"/> for a client download. The schedule keeps one held run: a later
    /// request joins it, and <paramref name="raiseCard"/> puts a card up only for the first. A run the
    /// loop took and the gate turned away is no longer the run in progress. Returns the held run.
    /// </summary>
    internal Task<RunNotice> HoldRunAsync(RunNotice notice, Action<RunNotice> raiseCard)
        => AskAsync<RunNotice>(reply => new HoldForDownload(notice, raiseCard, reply));

    /// <summary>
    /// Downloads ended, so the held run goes ahead. Posted without waiting: the reader handles it before
    /// any message written to this queue later, and nobody needs its answer. It closes into
    /// <paramref name="runningOperation"/>'s card when an operation is doing the work the held run asked
    /// for, into the run the loop took when that run will do it, and is queued otherwise.
    /// </summary>
    internal void ReleaseHeldRun(Func<RunNotice, Guid?> runningOperation)
        => _runMessages.Writer.TryWrite(new ReleaseHold(runningOperation));

    /// <summary>
    /// A person dismissed a waiting card. Posted without waiting for the answer, because the card's
    /// cancel callback can run on the run queue's own reader.
    /// </summary>
    internal void DismissCard(Guid cardId, RunNotice notice)
        => _runMessages.Writer.TryWrite(new CardDismissed(cardId, notice));

    /// <summary>
    /// What Run All reads: the run the loop took (null when idle or when the loop's pass is not doing the
    /// schedule's work) and the held run's card.
    /// </summary>
    internal Task<RunQueueStatus> ReadRunStatusAsync() => AskAsync<RunQueueStatus>(reply => new ReadStatus(reply));

    internal sealed record RunQueueStatus(RunNotice? Run, Guid? HeldCard);

    protected ScheduledServiceBase(ILogger logger)
    {
        _logger = logger;
        _ = Task.Run(ReadRunMessagesAsync);
    }

    /// <summary>
    /// Asked before every run a schedule starts, with the service's own key and what triggered the
    /// attempt: returns the reason the run may not start right now, or null when it may. Static and
    /// set once at startup, the same way ServiceExecutionStateChanged is wired, because this base has
    /// 21 concrete subclasses and a constructor parameter would have to be threaded through every one
    /// of them. Null while nothing has set it, so every run proceeds.
    ///
    /// The trigger travels with the question because a refused run that a person asked for has to be
    /// reported every time, while a refused timer tick does not.
    /// </summary>
    public static Func<string, RunTrigger, Task<string?>>? ScheduleRunGate { get; set; }

    /// <summary>
    /// Awaited before a startup run asks <see cref="ScheduleRunGate"/>, so the answer it gets is
    /// what the download tracker reported rather than the tracker's silence. Takes the service's
    /// own key because only the schedules whose work walks the cache tree are worth waiting for.
    /// Static and set once for the same reason as the gate above; null while nothing has set it,
    /// so every startup run asks straight away.
    /// </summary>
    public static Func<string, CancellationToken, Task>? WaitForDownloadAnswer { get; set; }

    /// <summary>
    /// The name of this service for logging purposes.
    /// </summary>
    protected abstract string ServiceName { get; }

    /// <summary>
    /// Delay before starting the service (allows app to initialize).
    /// Default: 5 seconds.
    /// </summary>
    protected virtual TimeSpan StartupDelay => TimeSpan.FromSeconds(5);

    /// <summary>
    /// Delay before retrying after a loop error, so a persistent failure backs off instead of
    /// tight-looping.
    /// </summary>
    protected virtual TimeSpan ErrorRetryDelay => TimeSpan.FromMinutes(1);

    // Schedule tracking properties
    public DateTime? LastRunUtc { get; protected set; }
    public DateTime? NextRunUtc { get; protected set; }

    // Written by the run queue's reader, read cross-thread by the HTTP GET /schedules path
    // (ServiceScheduleRegistry.GetAll). volatile publishes the write so a status read on another
    // thread cannot latch a stale value and leave the Schedules dot wrong.
    private volatile bool _isCurrentlyExecuting;
    public bool IsCurrentlyExecuting => _isCurrentlyExecuting;

    /// <summary>
    /// Trigger provenance for the run currently executing. Each loop resolves this immediately
    /// before calling ExecuteWorkAsync, so a subclass reading it inside that call sees the trigger
    /// for its own run.
    /// </summary>
    protected RunTrigger CurrentRunTrigger { get; set; } = RunTrigger.Scheduled;

    /// <summary>
    /// Takes the waiting request: the Run Now or Run All this loop iteration honors, or null when none
    /// waits. Taking it here (rather than leaving it set) is what stops a later, genuinely scheduled
    /// tick from being misattributed as Manual.
    /// </summary>
    protected Task<RunNotice?> ConsumePendingManualRunAsync() => AskAsync<RunNotice?>(reply => new TakeRequest(reply));

    /// <summary>
    /// Reads whether a request or an owed run is waiting without taking it, so the loop can spot one
    /// that arrived mid-run and go straight into another iteration instead of sleeping.
    /// </summary>
    protected Task<bool> HasPendingRunAsync() => AskAsync<bool>(reply => new PeekRequest(reply));

    /// <summary>
    /// Takes the owed run: one this service was already due for, refused by the download gate and
    /// owed once downloads stop, or null when none is owed. When the loop took a request in the same
    /// pass, the owed run joins it.
    /// </summary>
    protected Task<RunNotice?> ConsumePendingDeferredRunAsync() => AskAsync<RunNotice?>(reply => new TakeOwed(reply));

    /// <summary>
    /// Takes the run this loop pass does: <paramref name="notice"/> when the loop top took one, else a
    /// request or owed run that arrived after the loop top looked, else a new one with
    /// <paramref name="trigger"/>. Taking a late request here is what keeps it from running a second
    /// time after this pass.
    /// </summary>
    protected Task<RunNotice> StartRunAsync(RunTrigger trigger, RunNotice? notice)
        => AskAsync<RunNotice>(reply => new StartPass(trigger, notice, reply));

    /// <summary>
    /// The run passed its gate and starts its work. False when it was canceled first.
    /// </summary>
    protected Task<bool> EnterRunAsync(RunNotice run) => AskAsync<bool>(reply => new EnterWork(run, reply));

    /// <summary>
    /// The run ended or its gate turned it away. <paramref name="report"/> is false for a run that
    /// never reached a terminal state (declined, or stopped by shutdown).
    /// </summary>
    protected Task EndRunAsync(RunNotice run, string serviceKey, string? error, bool canceled, bool report)
        => AskAsync<bool>(reply => new RunEnded(run, serviceKey, error, canceled, report, reply));

    /// <summary>
    /// The run queue's answer to a request: whether it was admitted, the request that will run (a waiting
    /// one the new request joined, or the new request itself), and whether it waits behind a busy run.
    /// </summary>
    public sealed record Admission(bool Admitted, RunNotice Retained, bool FollowUpQueued);

    private abstract record RunMessage
    {
        public abstract void Fail(Exception error);
    }

    private abstract record RunMessage<T>(TaskCompletionSource<T> Reply) : RunMessage
    {
        public override void Fail(Exception error) => Reply.TrySetException(error);
    }

    private sealed record RequestRun(RunNotice Notice, Action<RunNotice, bool>? Admitted, TaskCompletionSource<Admission> Reply)
        : RunMessage<Admission>(Reply);
    private sealed record TakeRequest(TaskCompletionSource<RunNotice?> Reply) : RunMessage<RunNotice?>(Reply);
    private sealed record PeekRequest(TaskCompletionSource<bool> Reply) : RunMessage<bool>(Reply);
    private sealed record TakeOwed(TaskCompletionSource<RunNotice?> Reply) : RunMessage<RunNotice?>(Reply);
    private sealed record StartPass(RunTrigger Trigger, RunNotice? Notice, TaskCompletionSource<RunNotice> Reply)
        : RunMessage<RunNotice>(Reply);
    private sealed record EnterWork(RunNotice Run, TaskCompletionSource<bool> Reply) : RunMessage<bool>(Reply);
    private sealed record RunEnded(RunNotice Run, string ServiceKey, string? Error, bool Canceled, bool Report,
        TaskCompletionSource<bool> Reply) : RunMessage<bool>(Reply);
    private sealed record HoldForDownload(RunNotice Notice, Action<RunNotice> RaiseCard, TaskCompletionSource<RunNotice> Reply)
        : RunMessage<RunNotice>(Reply);
    private sealed record ReleaseHold(Func<RunNotice, Guid?> RunningOperation) : RunMessage
    {
        // Nothing waits on a release; the reader logs a failure.
        public override void Fail(Exception error) { }
    }
    private sealed record CardDismissed(Guid CardId, RunNotice Notice) : RunMessage
    {
        // Nothing waits on a dismissal; the reader logs a failure.
        public override void Fail(Exception error) { }
    }
    private sealed record ReadStatus(TaskCompletionSource<RunQueueStatus> Reply) : RunMessage<RunQueueStatus>(Reply);

    // Posts a message; the reader answers it. Nothing the reader runs may call this on its own queue.
    // No timeout: the reader never completes and catches each message's failure, so every reply arrives.
    private Task<T> AskAsync<T>(Func<TaskCompletionSource<T>, RunMessage> message)
    {
        var reply = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runMessages.Writer.TryWrite(message(reply));
        return reply.Task;
    }

    private async Task ReadRunMessagesAsync()
    {
        await foreach (var message in _runMessages.Reader.ReadAllAsync())
        {
            try
            {
                Handle(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{ServiceName} run queue failed to handle {Message}", ServiceName, message.GetType().Name);
                message.Fail(ex);
            }
        }
    }

    // Decides each message against the state as it stands and records the decision before any card
    // is touched; the tracker sends the card rows on its own publish task, so nothing here waits on
    // the network. Nothing this runs, the callbacks it calls included, may read the download gate or
    // the speed tracker, ask a registry method that asks a run queue, or ask this queue: the gate read
    // can raise the downloads-ended edge on this thread, and a queue that asks itself never answers.
    // Tracker reads and writes are allowed.
    private void Handle(RunMessage message)
    {
        switch (message)
        {
            case RequestRun request:
                request.Reply.TrySetResult(Request(request.Notice, request.Admitted));
                break;
            case TakeRequest take:
                _running = TakeRequested(includeExtra: true);
                _phase = _running is null ? RunPhase.Idle : RunPhase.Starting;
                take.Reply.TrySetResult(_running);
                break;
            case PeekRequest peek:
                peek.Reply.TrySetResult(_requested.Any(request => request is { Cancelled: false }) || _owed is { Cancelled: false });
                break;
            case TakeOwed take:
                take.Reply.TrySetResult(TakeOwedRun());
                break;
            case StartPass start:
                if (start.Notice is { } taken)
                {
                    _running = taken;
                }
                else
                {
                    // A request or owed run that arrived after the loop top looked is this pass's, unless it asks
                    // for another scan type: that one is an extra scan the loop takes on its own turn.
                    _running = TakeRequested(includeExtra: false);
                    TakeOwedRun();
                    _running ??= new RunNotice(EffectiveNotificationMode, start.Trigger);
                }
                _phase = RunPhase.Starting;
                start.Reply.TrySetResult(_running);
                break;
            case EnterWork enter:
                if (!enter.Run.Cancelled)
                {
                    if (ReferenceEquals(_running, enter.Run)) _phase = RunPhase.Running;
                    _isCurrentlyExecuting = true;
                }
                enter.Reply.TrySetResult(!enter.Run.Cancelled);
                break;
            case RunEnded ended:
                if (ReferenceEquals(_running, ended.Run))
                {
                    _running = null;
                    _phase = RunPhase.Idle;
                }
                _isCurrentlyExecuting = false;
                // Decided against the state this message sees: a run held or queued again keeps its
                // card for the run still to come, whichever order its end and its release arrived in.
                if (ended.Report)
                    RunCompleted?.Invoke(ended.Run, ended.ServiceKey, ended.Error, ended.Canceled, Keeps(ended.Run));
                ended.Reply.TrySetResult(true);
                break;
            case HoldForDownload hold:
                hold.Reply.TrySetResult(Hold(hold.Notice, hold.RaiseCard));
                break;
            case ReleaseHold release:
                Release(release.RunningOperation);
                break;
            case CardDismissed dismissed:
                Drop(dismissed.Notice, dismissed.CardId);
                Tracker?.CancelParkedOperation(dismissed.CardId);
                break;
            case ReadStatus read:
                read.Reply.TrySetResult(new RunQueueStatus(OwnRunInProgress(), _held[0]?.OperationId));
                break;
        }
    }

    // The run the loop took, when it is the schedule's own work. An extra scan of another type is not: it never
    // stands in for the schedule's run, so a status read and a fresh Run All look past it.
    private RunNotice? OwnRunInProgress()
        => IsDoingWork(_running) && !(_running is { } taken && IsExtraRun(taken)) ? _running : null;

    private Admission Request(RunNotice notice, Action<RunNotice, bool>? admitted)
    {
        for (var index = 0; index < SlotCount; index++)
        {
            if (_requested[index] is { Cancelled: true }) _requested[index] = null;
        }
        MoveOwnRun(_requested);
        var slot = SlotOf(notice);
        // A request joining one already waiting rides on it: a Run Now makes a waiting Run All a
        // person's own, so it draws the way a Run Now does, also on a schedule that takes no
        // second run. A waiting request whose card is ending cannot take it in, so the new request
        // takes its place.
        if (_requested[slot] is { } waitingRequest && !waitingRequest.Adopt(Tracker, notice)) _requested[slot] = null;
        // Only a request of this scan type is this schedule's run waiting; one of another type is its own scan.
        var anyRequested = _requested[slot] is not null;
        var busy = IsDoingWork(_running);
        var running = OwnRunInProgress();
        // Run All starts idle schedules only. A follow-up queued behind a busy one shows a second
        // card for a run nobody asked for. A run being canceled will not do the work, so it does not
        // count. A Run All run held for a download already has its card (PendingId) and still queues.
        var freshRunAll = notice.Trigger == RunTrigger.RunAll && notice.PendingId is null;
        if ((!QueueManualRuns && (busy || anyRequested))
            || (freshRunAll && (running is { Cancelled: false } || anyRequested)))
        {
            return new Admission(false, _requested[slot] ?? running ?? notice, false);
        }

        var retained = _requested[slot] ??= notice;
        admitted?.Invoke(retained, busy);
        CancelIntervalDelay();
        return new Admission(true, retained, busy);
    }

    private void Owe(RunNotice notice)
    {
        if (_owed is { Cancelled: true }) _owed = null;
        if (_owed is { } owed && owed.Adopt(Tracker, notice))
        {
            CancelIntervalDelay();
            return;
        }
        _owed = notice;
        CancelIntervalDelay();
    }

    // A request whose card was dismissed is gone, even when its dismissal has not arrived yet. The Full
    // detection request is taken before the Quick one, and a run the schedule owes at a higher scan type
    // than the request goes first: the request stays for the pass after it. A request for another scan type
    // than the schedule's own stays when the caller takes only the schedule's own run.
    private RunNotice? TakeRequested(bool includeExtra)
    {
        var owedType = _owed is { Cancelled: false } owed ? ScanTypeOf(owed) : null;
        for (var index = SlotCount - 1; index >= 0; index--)
        {
            var request = _requested[index];
            if (request is not { Cancelled: false })
            {
                _requested[index] = null;
                continue;
            }
            if (!includeExtra && IsExtraRun(request)) continue;
            if (owedType > ScanTypeOf(request)) return null;
            _requested[index] = null;
            ClearWakeForTakenRun();
            return request;
        }
        return null;
    }

    // A wake recorded before a take was for the run just taken, so it must not cut that run's failure
    // back-off short and retry at once. A wake recorded after it (a Run Now admitted while the run starts)
    // is for another request and stays, as does an interval change the loop has not handled yet.
    private void ClearWakeForTakenRun()
    {
        lock (IntervalLock)
        {
            if (!_intervalJustChanged) _wakePending = false;
        }
    }

    private RunNotice? TakeOwedRun()
    {
        var taken = _owed is { Cancelled: false } owed ? owed : null;
        _owed = null;
        if (taken is null) return null;
        // The owed run and a request taken in the same pass are one run when they ask for the same scan; the
        // owed run's card closes into it.
        if (_running is null)
        {
            _running = taken;
            _phase = RunPhase.Starting;
        }
        else if (ScanTypeOf(_running) != ScanTypeOf(taken) || !_running.Adopt(Tracker, taken))
        {
            // The run taken this pass is ending or is another scan's work, so the owed run waits for the
            // next pass.
            Owe(taken);
            return null;
        }
        ClearWakeForTakenRun();
        return taken;
    }

    private RunNotice Hold(RunNotice notice, Action<RunNotice> raiseCard)
    {
        if (notice.Cancelled) return notice;
        // The gate turned this run away, so it no longer covers a held run released after this.
        if (ReferenceEquals(_running, notice))
        {
            _running = null;
            _phase = RunPhase.Idle;
        }
        MoveOwnRun(_held);
        var slot = SlotOf(notice);
        if (_held[slot] is { Cancelled: true }) _held[slot] = null;
        if (_held[slot] is { } held)
        {
            // One hold per scan type, one card: a later request of that type joins it, and a card the
            // request already had closes into the hold's.
            if (held.Adopt(Tracker, notice)) return held;
            // The hold's card is ending, so this request becomes the hold.
            _held[slot] = null;
        }

        raiseCard(notice);
        _held[slot] = notice;
        return notice;
    }

    // Every hold goes ahead, the Full detection request first, so the requests reach the queue in that order.
    private void Release(Func<RunNotice, Guid?> runningOperation)
    {
        for (var index = SlotCount - 1; index >= 0; index--)
        {
            if (_held[index] is not { } held) continue;
            _held[index] = null;
            ReleaseOne(held, runningOperation);
        }
    }

    private void ReleaseOne(RunNotice held, Func<RunNotice, Guid?> runningOperation)
    {
        // A card already being dismissed is gone, even before its cancel reaches the notice.
        if (held.Cancelled || held.OperationId is { } heldCard && Tracker?.GetOperation(heldCard)?.Cancelled == true) return;
        // The card stops naming the download it waited for.
        if (held.OperationId is { } card) Tracker?.SetBlockedByName(card, null);
        // An operation already doing the work: the card closes into its card as a join, which a cancel
        // aimed at the held card does not follow. A refused join (that operation is ending) falls through.
        if (Tracker is not null && runningOperation(held) is { } running && held.CloseInto(Tracker, running)) return;
        // The run the loop took does the work it waited for, unless it is a person's Run Now and
        // that run already started, which queues a second run the way a Run Now does then. A run asked
        // for a scan type other than the held run's is another scan's work, so the held run queues.
        if (_running is { Cancelled: false } taken && ScanTypeOf(taken) == ScanTypeOf(held)
            && (held.Trigger != RunTrigger.Manual || _phase == RunPhase.Starting)
            && taken.Adopt(Tracker, held))
        {
            return;
        }
        if (held.Trigger is RunTrigger.Manual or RunTrigger.RunAll) Request(held, admitted: null);
        else Owe(held);
    }

    // Drops the requests a dismissed card stands for: the notice that raised it, and any run that
    // took the card over, which is canceled with it.
    private void Drop(RunNotice notice, Guid? card)
    {
        bool StandsFor(RunNotice? run) => run is not null
            && (ReferenceEquals(run, notice) || card is not null && run.OperationId == card);

        for (var index = 0; index < SlotCount; index++)
        {
            if (StandsFor(_requested[index])) DropOwner(ref _requested[index], notice, card);
            if (StandsFor(_held[index])) DropOwner(ref _held[index], notice, card);
        }
        if (StandsFor(_owed)) DropOwner(ref _owed, notice, card);
        if (StandsFor(_running) && !ReferenceEquals(_running, notice)) _running!.Cancel(Tracker, card!.Value);
    }

    private void DropOwner(ref RunNotice? slot, RunNotice notice, Guid? card)
    {
        if (!ReferenceEquals(slot, notice)) slot!.Cancel(Tracker, card!.Value);
        slot = null;
    }

    private bool Keeps(RunNotice run)
        => _held.Contains(run) || _requested.Contains(run) || ReferenceEquals(_owed, run);

    // Shared interruptible-sleep machinery. Guards the delay token source AND each loop's own
    // interval storage, so an interval change and the wake it triggers are seen together. The lock
    // is reentrant, so callers that already hold it stay atomic.
    private CancellationTokenSource? _intervalChangedCts;
    protected object IntervalLock { get; } = new();
    private volatile bool _intervalJustChanged;
    // Set under IntervalLock when a wake finds no sleep to cut short, so the next sleep returns at once.
    // Whoever wakes the loop records its request before waking it: a wake that lands while an earlier
    // one is being cleared is dropped, and the loop still finds the recorded request on its next take.
    private bool _wakePending;

    /// <summary>
    /// True when the current wake came from an interval or schedule change rather than the sleep
    /// elapsing: the loop must skip work on that wake and re-sleep on the new value. Volatile via
    /// the backing field, so the loop thread sees a change made on an HTTP thread.
    /// </summary>
    protected bool IntervalJustChanged
    {
        get => _intervalJustChanged;
        set => _intervalJustChanged = value;
    }

    /// <summary>
    /// Cancels the loop's current sleep so it wakes on the next iteration. Safe to call while
    /// already holding <see cref="IntervalLock"/>, which is how an interval change stays atomic
    /// with the wake it triggers.
    /// </summary>
    protected void CancelIntervalDelay()
    {
        lock (IntervalLock)
        {
            // A wake that lands while the loop is between its peek and its sleep has no sleep to cancel;
            // recorded here, it makes that sleep return at once instead of waiting a full interval.
            _wakePending = true;
            try
            {
                _intervalChangedCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed - will be recreated on next loop iteration
            }
        }
    }

    /// <summary>
    /// Delay that an interval change, schedule change or Run Now can interrupt. On interruption
    /// (not a shutdown) it returns so the loop continues immediately and picks up the change; on
    /// shutdown it returns and the loop's own stop-token check ends it. Returns true when a wake cut
    /// the sleep short (or came before it started), false when the sleep ran out or the host is stopping.
    /// </summary>
    protected async Task<bool> InterruptibleDelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        CancellationTokenSource? linkedCts = null;
        try
        {
            lock (IntervalLock)
            {
                if (_wakePending)
                {
                    _wakePending = false;
                    return true;
                }
                _intervalChangedCts?.Dispose();
                _intervalChangedCts = new CancellationTokenSource();
            }

            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, _intervalChangedCts!.Token);

            // A wake cancels this delay from the run-queue reader, and Cancel runs continuations on the
            // caller's thread; yielding keeps the woken loop off the reader and out of IntervalLock.
            await Task.Delay(delay, linkedCts.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            return false;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // This wake is consumed here; a later one sets the flag again. Cleared before the loop reaches
            // its take, so a request posted after this point is either taken by it or wakes the next sleep.
            lock (IntervalLock) _wakePending = false;
            _logger.LogDebug("{ServiceName} sleep interrupted by interval change or trigger", ServiceName);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Service is shutting down - let the loop exit
            return false;
        }
        finally
        {
            linkedCts?.Dispose();
        }
    }

    /// <summary>
    /// Waits out a failed run's back-off until <paramref name="retryAt"/>. A request, an owed run or a
    /// settings change ends it early, because the loop has to handle that wake. A wake whose request was
    /// dismissed before this check does not, so the retry keeps its full delay.
    /// </summary>
    protected async Task WaitForRetryAsync(DateTime retryAt, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && retryAt > DateTime.UtcNow)
        {
            if (await InterruptibleDelayAsync(TimeUntil(retryAt), stoppingToken)
                && (IntervalJustChanged || await HasPendingRunAsync()))
            {
                return;
            }
        }
    }

    public override void Dispose()
    {
        lock (IntervalLock)
        {
            _intervalChangedCts?.Dispose();
            _intervalChangedCts = null;
        }
        base.Dispose();
    }

    /// <summary>
    /// Hardcoded default for whether work runs at startup, before the first interval elapses.
    /// Left abstract because the two loop base classes ship different defaults - a shared default
    /// here would silently flip startup behaviour for one of the two hierarchies. Individual
    /// services override it to express their intended default; the user can override it at runtime
    /// via SetRunOnStartup - typically loaded from IStateService in each service's constructor and
    /// updated via the Schedules UI.
    /// </summary>
    public abstract bool DefaultRunOnStartup { get; }

    /// <summary>
    /// User-controlled override for RunOnStartup (null = use DefaultRunOnStartup).
    /// </summary>
    private bool? _runOnStartupOverride;

    /// <summary>
    /// Effective value of RunOnStartup: user override if set, else DefaultRunOnStartup.
    /// </summary>
    public bool RunOnStartup => _runOnStartupOverride ?? DefaultRunOnStartup;

    /// <summary>
    /// Set the user-controlled RunOnStartup override. Pass null to clear and revert
    /// to DefaultRunOnStartup. Note: this only affects future startups - once a service
    /// has already started its loop, toggling this won't retroactively run or skip
    /// the startup pass.
    /// </summary>
    public void SetRunOnStartup(bool? value)
    {
        _runOnStartupOverride = value;
        _logger.LogDebug("{ServiceName} RunOnStartup override set to {Value}", ServiceName, value);
    }

    /// <summary>
    /// Hardcoded default notification mode for this service. Subclasses that emit lifecycle
    /// notifications override this to express their intended default; the user can override it at
    /// runtime via SetNotificationMode - typically loaded from IStateService in each service's
    /// constructor and updated via the Schedules UI.
    /// </summary>
    protected virtual NotificationMode DefaultNotificationMode => NotificationMode.All;

    /// <summary>
    /// User-controlled override for the notification mode (null = use DefaultNotificationMode).
    /// </summary>
    private NotificationMode? _notificationModeOverride;

    /// <summary>
    /// Effective notification mode: user override if set, else DefaultNotificationMode.
    /// </summary>
    public NotificationMode EffectiveNotificationMode => _notificationModeOverride ?? DefaultNotificationMode;

    /// <summary>
    /// Set the user-controlled notification-mode override. Pass null to clear and revert to
    /// DefaultNotificationMode.
    /// </summary>
    public void SetNotificationMode(NotificationMode? mode) => _notificationModeOverride = mode;

    /// <summary>
    /// Whether this service emits lifecycle notifications the user can gate from the Schedules UI.
    /// Only services that actually notify override this to true; every other schedule card hides
    /// the Notifications control.
    /// </summary>
    protected virtual bool SupportsNotifications => false;

    /// <summary>
    /// Convenience helper for subclass constructors: applies any user-saved interval, run-on-startup
    /// and notification overrides for this service from the state store. Pass the same service key
    /// the registry uses, so the values written by the Schedules UI are the ones read back.
    /// </summary>
    protected void LoadStateOverrides(IStateService stateService, string serviceKey)
    {
        var savedInterval = stateService.GetServiceInterval(serviceKey);
        if (savedInterval.HasValue)
        {
            ApplyLoadedInterval(TimeSpan.FromHours(savedInterval.Value));
        }

        var savedRunOnStartup = stateService.GetServiceRunOnStartup(serviceKey);
        if (savedRunOnStartup.HasValue)
        {
            SetRunOnStartup(savedRunOnStartup.Value);
        }

        var savedNotificationMode = stateService.GetServiceNotificationMode(serviceKey);
        if (savedNotificationMode.HasValue)
        {
            SetNotificationMode(savedNotificationMode.Value);
        }

        var savedCustomSchedule = stateService.GetServiceCustomSchedule(serviceKey);
        if (savedCustomSchedule is not null)
        {
            UpdateCustomSchedule(savedCustomSchedule);
        }
    }

    /// <summary>
    /// Applies an interval loaded from the state store. Each loop base routes this to its own
    /// interval setter, which differ in what they treat as the default to fall back to.
    /// </summary>
    protected abstract void ApplyLoadedInterval(TimeSpan interval);

    private readonly object _customScheduleLock = new();
    private CustomSchedule? _customSchedule;
    private volatile bool _unreachableScheduleLogged;

    /// <summary>
    /// Custom schedule currently driving this service, or null when it runs on its plain interval. A
    /// schedule wins outright over the interval, and the interval is left untouched so clearing the
    /// schedule puts the service straight back on the cadence it had.
    /// Thread-safe: the loop reads it every iteration while an HTTP save writes it.
    /// </summary>
    public CustomSchedule? ConfiguredCustomSchedule
    {
        get { lock (_customScheduleLock) return _customSchedule; }
    }

    /// <summary>
    /// Sets or clears the custom schedule at runtime, waking the loop so the change takes effect
    /// immediately rather than after the sleep computed from the previous value. Public because the
    /// schedule registry already holds the typed service instance and can apply the value straight
    /// through, and because both loop bases expose the same setter.
    /// </summary>
    public void UpdateCustomSchedule(CustomSchedule? schedule)
    {
        lock (_customScheduleLock)
        {
            _customSchedule = schedule;
            // Re-arm the never-fires warning so a corrected schedule that still cannot fire says so
            // again instead of staying silent behind the previous one.
            _unreachableScheduleLogged = false;
        }

        WakeForScheduleChange();

        if (schedule is null)
        {
            _logger.LogInformation("{ServiceName} custom schedule cleared, back on its interval", ServiceName);
        }
        else
        {
            _logger.LogInformation("{ServiceName} custom schedule set to '{Expression}' ({TimeZoneId})",
                ServiceName, schedule.Expression, schedule.TimeZoneId);
        }
    }

    /// <summary>
    /// Tells the loop that the value its current sleep was computed from has changed: the loop must
    /// skip work on the wake this causes and re-sleep on the new value.
    /// </summary>
    protected void WakeForScheduleChange()
    {
        lock (IntervalLock)
        {
            // Reuses the flag an interval change already owns: both mean "the sleep you are in was
            // computed from a value that has since changed", and the loop's response is the same.
            _intervalJustChanged = true;

            CancelIntervalDelay();
        }
    }

    /// <summary>
    /// The instant this service should next run: the custom schedule's next occurrence when one is
    /// set, otherwise the interval counted from now. Null means nothing is scheduled - the service is
    /// paused (interval at or below zero), or the schedule can never fire again.
    /// </summary>
    protected static DateTime? ComputeNextRun(CustomSchedule? schedule, TimeSpan interval)
    {
        if (schedule is not null)
        {
            return ScheduleTiming.ComputeNextRun(schedule, DateTime.UtcNow);
        }

        return interval > TimeSpan.Zero ? DateTime.UtcNow + interval : null;
    }

    /// <summary>
    /// How long to wait until <paramref name="nextRunUtc"/>, never negative. The next run and this
    /// wait read the clock a moment apart, so an occurrence that has just passed clamps to zero rather
    /// than asking for a negative delay.
    /// </summary>
    protected static TimeSpan TimeUntil(DateTime nextRunUtc)
    {
        var remaining = nextRunUtc - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// The time an interval sleep was already waiting for, when a wake that ran nothing (a Run Now
    /// dismissed before the loop took it) cut that sleep short before it was due. The countdown keeps
    /// its old due time instead of restarting from the wake. Null when the wake was an interval or
    /// schedule change, when a custom schedule names its own instants, or when the time has passed.
    /// </summary>
    protected static DateTime? ResumedDueAt(bool woken, bool intervalChanged, CustomSchedule? schedule, DateTime? dueAt)
        => woken && !intervalChanged && schedule is null && dueAt is { } due && due > DateTime.UtcNow ? due : null;

    /// <summary>
    /// Whether this iteration may do work. A schedule replaces the interval outright, so a service
    /// that has one is gated on whether the schedule can fire at all rather than on its interval: a
    /// loop only reaches its work branch after sleeping to an occurrence, while a schedule with no
    /// next run (an expression that can never land inside its own window) idles on the ordinary
    /// interval sleep instead. Testing the interval in that case would run the work on exactly the
    /// schedule the user set to stop it.
    /// </summary>
    protected static bool IsWorkDue(CustomSchedule? schedule, TimeSpan interval)
    {
        return schedule is not null
            ? ScheduleTiming.ComputeNextRun(schedule, DateTime.UtcNow) is not null
            : interval > TimeSpan.Zero;
    }

    /// <summary>
    /// Warns, at most once per schedule, that a schedule has no next run at all - an expression with
    /// no future occurrence, or one that can never land inside its own window. A loop reaches this on
    /// every wake, so warning per iteration would fill the log while the service sits idle.
    /// </summary>
    protected void WarnScheduleNeverFires(CustomSchedule schedule)
    {
        if (_unreachableScheduleLogged)
        {
            return;
        }

        _unreachableScheduleLogged = true;
        _logger.LogWarning(
            "{ServiceName} custom schedule '{Expression}' ({TimeZoneId}) has no next run and will not fire until it is changed",
            ServiceName, schedule.Expression, schedule.TimeZoneId);
    }

    /// <summary>
    /// Safely delay, catching cancellation exceptions.
    /// </summary>
    protected static async Task SafeDelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown or interval change
        }
    }

    /// <summary>
    /// Runs one scheduled-work attempt under the cancellation, failure and completion handling both
    /// loop bases need. <paramref name="executeWork"/> carries everything that differs between the
    /// two loops (trigger attribution, the start broadcast, the actual work call and the success
    /// NextRunUtc); <paramref name="errorLogMessage"/> and <paramref name="broadcastEnd"/> carry the
    /// two values that differ in this shared tail itself. <paramref name="retryFailure"/> is false for a
    /// schedule that is not due to run (Disabled or Startup only): a failed run there is not retried.
    /// </summary>
    /// <returns>
    /// ShuttingDown is true when the caller's while loop should break because the service is
    /// stopping. RunFailed is true when the caller should continue straight to its next iteration
    /// because this attempt already backed off via <see cref="ErrorRetryDelay"/>.
    /// </returns>
    protected async Task<(bool ShuttingDown, bool RunFailed)> RunScheduledWorkAsync(
        string serviceKey,
        RunTrigger trigger,
        Func<CancellationToken, Task> executeWork,
        CancellationToken stoppingToken,
        string errorLogMessage,
        Action broadcastEnd,
        RunNotice? notice = null,
        bool retryFailure = true)
    {
        // Keep the taken notice owned through the gate and terminal publication.
        var runNotice = await StartRunAsync(trigger, notice);
        SelectRunNotice(trigger, runNotice);
        var workStarted = false;
        var executionEntered = false;
        var declined = false;
        var runFailed = false;
        var shuttingDown = false;
        string? error = null;
        CancellationTokenSource? runCts = null;
        try
        {
            if (runNotice.Cancelled) return (false, false);
            stoppingToken.ThrowIfCancellationRequested();
            var runDenial = ScheduleRunGate is { } gate ? await gate(serviceKey, CurrentRunTrigger) : null;
            if (runDenial is not null)
            {
                declined = true;
                _logger.LogInformation("{ServiceName} run skipped: {Reason}", ServiceName, runDenial);
                return (false, false);
            }
            runNotice = CurrentRunNotice;
            if (!await EnterRunAsync(runNotice)) return (false, false);
            // A held run released while this one waited at its gate may have raised its trigger.
            SelectRunNotice(runNotice);
            runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, runNotice.Token);
            executionEntered = true;
            runCts.Token.ThrowIfCancellationRequested();
            workStarted = true;
            await executeWork(runCts.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown - end the loop cleanly. A non-shutdown OCE (e.g. an inner
            // per-iteration timeout) falls through to the Exception handler below
            // instead of silently ending the service loop.
            shuttingDown = true;
        }
        catch (OperationCanceledException) when (runNotice.Cancelled || runCts?.IsCancellationRequested == true)
        {
            // A cancelled admission does not change the next scheduled occurrence.
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogError(ex, errorLogMessage, ServiceName);
            runFailed = retryFailure;
            // The next attempt is the retry below, not the elapsed schedule - point the
            // countdown in the run-END broadcast at the retry deadline. A schedule set to Disabled
            // or Startup only has no retry: its failure ends on the card and nothing is counted down.
            if (retryFailure) NextRunUtc = DateTime.UtcNow + ErrorRetryDelay;
        }
        finally
        {
            // A run that threw still ran, so it stamps here rather than after ExecuteWorkAsync:
            // the Schedules page reads this for "Last run", and the frontend also clears its
            // optimistic Run Now flag when this value moves. Stamping only on success left a
            // failed run's end-broadcast carrying an unchanged time, which held that button
            // disabled until a safety timeout expired. Shutdown is excluded because the work
            // never reached a terminal state - the service is stopping, not finishing. An extra scan of
            // another type is not the schedule's own run, so it leaves the stamp alone.
            if (!shuttingDown && workStarted && !IsExtraRun(runNotice))
            {
                LastRunUtc = DateTime.UtcNow;
            }
            try
            {
                await EndRunAsync(runNotice, serviceKey, error, runNotice.Cancelled || runCts?.IsCancellationRequested == true,
                    report: !shuttingDown && !declined);
                if (executionEntered) broadcastEnd();
            }
            finally
            {
                runCts?.Dispose();
            }
        }

        // Back off AFTER the finally above has cleared the flag and broadcast the end, so a
        // failed run does not sit falsely "running" (green dot) for the whole retry delay. A Run Now
        // wakes the back-off the way it wakes the interval sleep, so it does not wait out the retry.
        if (runFailed && NextRunUtc is { } retryAt)
        {
            await WaitForRetryAsync(retryAt, stoppingToken);
        }

        return (shuttingDown, runFailed);
    }
}
