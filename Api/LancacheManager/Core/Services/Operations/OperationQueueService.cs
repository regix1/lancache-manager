using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Implements the operation wait-queue (see <see cref="IOperationQueue"/>).
///
/// Concurrency model:
///  - <see cref="_gate"/> (async mutex) serializes enqueue decisions and promotions, closing
///    the double-start race (a blocker completing while a second conflicting op is mid-enqueue).
///  - <see cref="_sync"/> (plain lock) protects the waiter list for the synchronous callbacks
///    (onTerminalCleanup, cancel) that must not await.
///  - Promotion is triggered by <see cref="IUnifiedOperationTracker.OperationTerminal"/>, which
///    fires exactly once per op for success, failure, cancel AND force-kill (CompletedFlag gate),
///    so a crashed/force-killed blocker still unblocks its waiters.
///
/// A waiting op is a REAL tracker registration (status Waiting) so the universal cancel
/// endpoint works on it and the frontend card carries a real operationId. At promotion the
/// waiting op is completed success-silently and the stored start delegate runs the operation's
/// EXISTING start path, which self-registers its own operation (own id, own CTS) exactly as a
/// directly-started op would. The frontend waiting card transitions because both cards share
/// the per-type singleton notification id and the promoted op's Started event replaces it.
///
/// Because promotion swaps one tracker registration for another, the id on the user's card is
/// briefly the id of an operation that no longer drives anything. Cancel intent is carried across
/// that swap two ways, and both are needed:
///  - <see cref="IUnifiedOperationTracker.RecordHandoff"/> is published BEFORE the waiting op is
///    completed, so every later cancel aimed at the old id follows it to the running op.
///  - the waiting op's Cancelled latch is re-read AFTER the handoff is published, which catches a
///    cancel that landed earlier, while the start delegate was still running.
/// Without them a cancel clicked during promotion returned 200, ended the waiting card, and left
/// the work it had just started running to completion.
/// </summary>
public sealed class OperationQueueService : IOperationQueue
{
    private sealed class Waiter
    {
        public required Guid WaitingId { get; init; }
        public required OperationType Type { get; init; }
        public required ConflictScope Scope { get; init; }
        public required string Name { get; init; }
        public required Func<Task<Guid?>> Start { get; init; }
        public required long Sequence { get; init; }
        public int PromotionRefusals { get; set; }
        /// <summary>
        /// The blocker last announced to the frontend for this waiter (id + display name).
        /// Both mutate only under <see cref="_gate"/>; the name is additionally read under
        /// <see cref="_sync"/> by the recovery-endpoint accessor.
        /// </summary>
        public Guid? LastBlockerId { get; set; }
        public string? LastBlockerName { get; set; }
    }

    private const int MaxPromotionRefusals = 300;
    private static readonly TimeSpan _promotionRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly IUnifiedOperationTracker _tracker;
    private readonly IOperationConflictChecker _conflictChecker;
    private readonly ISignalRNotificationService _notifications;
    private readonly ILogger<OperationQueueService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<Waiter> _waiters = new();
    private long _nextSequence;

    public OperationQueueService(
        IUnifiedOperationTracker tracker,
        IOperationConflictChecker conflictChecker,
        ISignalRNotificationService notifications,
        ILogger<OperationQueueService> logger)
    {
        _tracker = tracker;
        _conflictChecker = conflictChecker;
        _notifications = notifications;
        _logger = logger;

        // Single terminal hook: every op (success/failed/cancelled/force-killed) funnels
        // through CompleteOperation, so every terminal event can promote waiters.
        _tracker.OperationTerminal += op => { _ = PromoteEligibleAsync(); };
    }

    public async Task<QueuedOperationResponse> EnqueueAsync(
        OperationType type,
        ConflictScope scope,
        string displayName,
        Func<Task<Guid?>> start,
        CancellationToken ct,
        bool reportRefusal = false)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Dedup against the queue first: an identical request already parked is returned
            // as-is (never two queued copies of the same destructive work).
            lock (_sync)
            {
                var duplicateWaiter = _waiters.FirstOrDefault(w =>
                    w.Type == type
                    && w.Scope.Matches(scope)
                    && string.Equals(w.Name, displayName, StringComparison.Ordinal));
                if (duplicateWaiter != null)
                {
                    return new QueuedOperationResponse
                    {
                        OperationId = duplicateWaiter.WaitingId,
                        Queued = true,
                        AlreadyRunning = true,
                        Status = "waiting"
                    };
                }
            }

            // Re-check under the gate: the blocker may have finished before this enqueue
            // committed - in that case start immediately instead of parking forever.
            var conflict = await _conflictChecker.CheckAsync(type, scope, ct);

            var retryAfterParking = false;
            if (conflict == null)
            {
                Guid? startedId;
                try
                {
                    startedId = await start();
                }
                catch (DownloadInProgressException ex)
                {
                    // The same refusal the promotion path reports, arriving through the immediate
                    // door: the schedule gate said yes, then a download began before the start
                    // delegate ran its own check. Named here as a decline rather than left to the
                    // caller, whose only handler logs it as an error for something that did not go
                    // wrong. Rethrown unchanged, because an HTTP caller is waiting on this and its
                    // 400 with the reason is the answer: dropping the throw would turn that into a
                    // silent success, so this handler only looks redundant.
                    _logger.LogInformation(
                        "{Type} '{Name}' declined before it started: {Reason}", type, displayName, ex.Message);

                    // Announced only for a caller that swallows the exception, because the throw
                    // below already IS the report for everyone else: an HTTP route turns it into a
                    // 400 the click renders, and announcing as well shows two notices for one
                    // click, one of them describing the SCHEDULED run rather than what was clicked.
                    // Reported through the tracker, which is a seam this service and the schedule
                    // registry already share: the registry subscribes to the terminal hook and is
                    // the one place that knows which card this operation type's schedule owns. The
                    // dependency runs that way round on purpose, so nothing here has to know about
                    // schedules and nothing there has to be injected into the queue.
                    if (reportRefusal)
                    {
                        var declinedId = _tracker.RegisterOperation(
                            type,
                            displayName,
                            new CancellationTokenSource(),
                            metadata: new Dictionary<string, object?> { [DeclinedRunMetadata.Key] = true });
                        _tracker.CompleteOperation(declinedId, success: true, error: ex.Message, skipped: true);
                    }

                    throw;
                }

                if (startedId.HasValue)
                {
                    return new QueuedOperationResponse
                    {
                        OperationId = startedId.Value,
                        Queued = false,
                        Status = "started"
                    };
                }

                // A local service gate can briefly outlive the tracker operation that owned it.
                // Preserve this request as a real waiter and let the bounded promotion retry
                // acquire that gate after the previous worker finishes unwinding.
                retryAfterParking = true;
            }
            // Identical op already ACTIVE -> idempotent accept (never rejected, never doubled).
            else if (conflict.StageKey == "errors.conflict.duplicate"
                && conflict.ActiveOperationId is { } activeId && activeId != Guid.Empty
                && string.Equals(
                    _tracker.GetOperation(activeId)?.Name,
                    displayName,
                    StringComparison.Ordinal))
            {
                return new QueuedOperationResponse
                {
                    OperationId = activeId,
                    Queued = false,
                    AlreadyRunning = true,
                    Status = "alreadyRunning"
                };
            }

            // Park it: real tracker registration (status Waiting) so universal cancel works
            // and the frontend card has a real operationId (no ghost-notification shape).
            var cts = new CancellationTokenSource();
            Guid waitingId = default;
            var typeWire = type.ToWireString();
            waitingId = _tracker.RegisterOperation(
                type,
                displayName,
                cts,
                onTerminalCleanup: () => RemoveWaiter(waitingId),
                // Always close the waiting-card lifecycle. Usually the promoted op's Started
                // event has already replaced the card; Promoted also handles intentionally
                // silent scheduled operations by removing their purple card at handoff.
                // A declined run rides the success flag too, so it is excluded from Promoted and
                // reported on its own: nothing started and nothing replaced the card, and saying
                // otherwise removes the card without ever showing the reason.
                onTerminalEmit: info => _notifications.NotifyAllAsync(
                    SignalREvents.OperationWaitingComplete,
                    new OperationWaitingCompleteNotification(
                        waitingId,
                        typeWire,
                        info.Cancelled,
                        info.Error,
                        Promoted: info.Success && !info.Skipped,
                        Skipped: info.Skipped)),
                initialStatus: OperationStatus.Waiting);

            // A waiting op has no worker, so the queue is its worker: when the universal
            // cancel path cancels the CTS, complete the op as cancelled (CompletedFlag makes
            // a race with promotion's success-complete a safe who-wins).
            // Cancelled is passed explicitly rather than left to the tracker's latch: this token can
            // be cancelled by paths other than the cancel endpoint, and the card must read as
            // cancelled on all of them. No attribution — at this point the code cannot tell a person
            // clicking cancel from the app shutting down.
            var capturedWaitingId = waitingId;
            cts.Token.Register(() => _ = Task.Run(() =>
                _tracker.CompleteOperation(capturedWaitingId, success: false, cancelled: true)));

            var blockerName = ResolveBlockerName(conflict);
            lock (_sync)
            {
                _waiters.Add(new Waiter
                {
                    WaitingId = waitingId,
                    Type = type,
                    Scope = scope,
                    Name = displayName,
                    Start = start,
                    Sequence = Interlocked.Increment(ref _nextSequence),
                    LastBlockerId = conflict?.ActiveOperationId,
                    LastBlockerName = blockerName
                });
            }

            if (conflict == null)
            {
                _logger.LogInformation(
                    "Queued {Type} '{Name}' ({Id}) after its local start gate temporarily refused",
                    type,
                    displayName,
                    waitingId);
            }
            else
            {
                _logger.LogInformation(
                    "Queued {Type} '{Name}' ({Id}) behind active {ActiveType} ({ActiveId})",
                    type, displayName, waitingId, conflict.ActiveOperationType, conflict.ActiveOperationId);
            }

            await _notifications.NotifyAllAsync(
                SignalREvents.OperationWaiting,
                new OperationWaitingNotification(waitingId, typeWire, displayName, blockerName));

            if (retryAfterParking)
            {
                _ = PromoteAfterRetryDelayAsync();
            }

            return new QueuedOperationResponse
            {
                OperationId = waitingId,
                Queued = true,
                Status = "waiting"
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? GetWaitingBlockerName(Guid waitingOperationId)
    {
        lock (_sync)
        {
            return _waiters.FirstOrDefault(w => w.WaitingId == waitingOperationId)?.LastBlockerName;
        }
    }

    /// <summary>
    /// The blocker's display name comes from the live tracker rather than the conflict response:
    /// the response only carries the enum name, and a raw enum token is not something to show a
    /// person. A blocker that already left the tracker resolves to null (generic waiting text) -
    /// its terminal event is about to trigger a promotion pass anyway.
    /// </summary>
    private string? ResolveBlockerName(OperationConflictResponse? conflict)
    {
        if (conflict?.ActiveOperationId is not { } blockerId || blockerId == Guid.Empty)
        {
            return null;
        }
        return _tracker.GetOperation(blockerId)?.Name;
    }

    private async Task AnnounceBlockerChangeAsync(Waiter waiter, OperationConflictResponse conflict)
    {
        if (conflict.ActiveOperationId == waiter.LastBlockerId)
        {
            return;
        }
        var blockerName = ResolveBlockerName(conflict);
        lock (_sync)
        {
            waiter.LastBlockerId = conflict.ActiveOperationId;
            waiter.LastBlockerName = blockerName;
        }
        await _notifications.NotifyAllAsync(
            SignalREvents.OperationWaiting,
            new OperationWaitingNotification(
                waiter.WaitingId,
                waiter.Type.ToWireString(),
                waiter.Name,
                blockerName));
    }

    private bool RemoveWaiter(Guid waitingId)
    {
        lock (_sync)
        {
            var index = _waiters.FindIndex(w => w.WaitingId == waitingId);
            if (index < 0)
            {
                return false;
            }
            _waiters.RemoveAt(index);
            return true;
        }
    }

    private async Task PromoteAfterRetryDelayAsync()
    {
        await Task.Delay(_promotionRetryDelay);
        await PromoteEligibleAsync();
    }

    /// <summary>
    /// Promote every waiter whose conflicts have cleared, in FIFO order. Each promoted start
    /// is AWAITED before evaluating the next waiter so the newly-registered operation is
    /// visible to the next conflict check (no two same-scope waiters can co-promote).
    /// Serialized by <see cref="_gate"/>; re-entrant terminal events (the waiting op's own
    /// completion fires OperationTerminal too) simply run a later, idempotent pass.
    /// </summary>
    private async Task PromoteEligibleAsync()
    {
        try
        {
            var retryRequested = false;
            await _gate.WaitAsync();
            try
            {
                List<Waiter> snapshot;
                lock (_sync)
                {
                    snapshot = _waiters.ToList();
                }

                foreach (var waiter in snapshot)
                {
                    // Still parked? (cancel may have removed it since the snapshot)
                    lock (_sync)
                    {
                        if (!_waiters.Any(w => w.WaitingId == waiter.WaitingId))
                        {
                            continue;
                        }
                    }

                    var conflict = await _conflictChecker.CheckAsync(waiter.Type, waiter.Scope, CancellationToken.None);
                    if (conflict != null)
                    {
                        // Still blocked; independent-scope waiters behind it may still promote.
                        // The blocker may be a DIFFERENT operation than last announced (the one
                        // this waiter parked behind finished, and the next conflicting op took
                        // over) - re-announce so the waiting card names the current blocker.
                        await AnnounceBlockerChangeAsync(waiter, conflict);
                        continue;
                    }

                    // Claim the entry before starting so a concurrent cancel cannot double-drive it.
                    if (!RemoveWaiter(waiter.WaitingId))
                    {
                        continue;
                    }

                    Guid? startedId = null;
                    string? startError = null;
                    var startDeclined = false;
                    try
                    {
                        startedId = await waiter.Start();
                    }
                    catch (DownloadInProgressException ex)
                    {
                        // Only this one condition is a decline. The same request refused a moment
                        // earlier, at request time, would have been answered 400 and never queued,
                        // so being parked behind another operation must not turn it into a red
                        // failure. Every other stated precondition that throws the base
                        // ValidationException - a wrong PUID or PGID failing a write-permission
                        // re-check, a datasource that cannot map logical objects - is a real
                        // problem the reader has to see, and falls through to the handler below.
                        // This catch must stay above that one; the derived type is unreachable
                        // otherwise.
                        startDeclined = true;
                        startError = ex.Message;
                        _logger.LogInformation(
                            "Queued {Type} '{Name}' declined at promotion: {Reason}",
                            waiter.Type, waiter.Name, ex.Message);
                    }
                    catch (Exception ex)
                    {
                        startError = ex.Message;
                        _logger.LogError(ex, "Queued {Type} '{Name}' failed to start at promotion", waiter.Type, waiter.Name);
                    }

                    if (startedId.HasValue)
                    {
                        _logger.LogInformation(
                            "Promoted queued {Type} '{Name}': waiting op {WaitingId} -> running op {NewId}",
                            waiter.Type, waiter.Name, waiter.WaitingId, startedId.Value);

                        // Point the old id at the new one BEFORE closing the waiting card, so any
                        // cancel arriving from this moment on reaches the operation now doing the
                        // work rather than the parked card it replaced.
                        _tracker.RecordHandoff(waiter.WaitingId, startedId.Value);

                        // Successful handoff emits Promoted=true; the frontend keeps a running
                        // replacement card or removes the waiting card for a silent operation.
                        _tracker.CompleteOperation(waiter.WaitingId, success: true);

                        // A cancel that landed WHILE Start() was running hit a waiting operation
                        // that no longer drove anything: it ended the parked card and left the work
                        // it had just started running, so the user was told the operation was
                        // cancelled while it carried on. The Cancelled latch is set before the token
                        // is cancelled and is never cleared, so that click is still readable here.
                        if (_tracker.GetOperation(waiter.WaitingId)?.Cancelled == true)
                        {
                            _logger.LogInformation(
                                "Queued {Type} '{Name}' was cancelled during promotion; cancelling promoted op {NewId}",
                                waiter.Type, waiter.Name, startedId.Value);
                            _tracker.CancelOperation(startedId.Value);
                        }
                    }
                    else if (startError == null)
                    {
                        // A start path may have a short-lived local gate that outlives its tracker
                        // operation while the old worker unwinds. Keep the real waiting operation
                        // parked and retry outside the global queue mutex instead of dropping the
                        // scheduled request or blocking every enqueue call inside waiter.Start().
                        var requeued = false;
                        var retryLimitReached = false;
                        lock (_sync)
                        {
                            if (_tracker.GetOperation(waiter.WaitingId)?.Status == OperationStatus.Waiting)
                            {
                                waiter.PromotionRefusals++;
                                if (waiter.PromotionRefusals <= MaxPromotionRefusals)
                                {
                                    _waiters.Add(waiter);
                                    _waiters.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
                                    requeued = true;
                                }
                                else
                                {
                                    retryLimitReached = true;
                                }
                            }
                        }

                        if (requeued)
                        {
                            retryRequested = true;
                            _logger.LogDebug(
                                "Queued {Type} '{Name}' temporarily refused promotion; retry {Attempt}/{MaxAttempts}",
                                waiter.Type,
                                waiter.Name,
                                waiter.PromotionRefusals,
                                MaxPromotionRefusals);
                            break;
                        }

                        if (retryLimitReached)
                        {
                            _tracker.CompleteOperation(
                                waiter.WaitingId,
                                success: false,
                                error: "Queued operation could not acquire its local start gate");
                        }
                        // Otherwise the waiting operation was cancelled while promotion was in
                        // flight; its cancellation path already completed the card.
                    }
                    else
                    {
                        // Terminal either way; the card just has to say which. A decline carries its
                        // reason on a skipped card, everything else on a failed one.
                        _tracker.CompleteOperation(
                            waiter.WaitingId,
                            success: startDeclined,
                            error: startError,
                            skipped: startDeclined);
                    }
                }
            }
            finally
            {
                _gate.Release();
            }

            if (retryRequested)
            {
                _ = PromoteAfterRetryDelayAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Operation queue promotion pass failed");
        }
    }
}
