using LancacheManager.Core.Interfaces;
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
        public RunNotice? Notice { get; init; }
        /// <summary>
        /// The blocker last recorded for this waiter. Mutates only under <see cref="_gate"/>.
        /// </summary>
        public Guid? LastBlockerId { get; set; }
    }

    private const int MaxPromotionRefusals = 300;
    private static readonly TimeSpan _promotionRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly IUnifiedOperationTracker _tracker;
    private readonly IOperationConflictChecker _conflictChecker;
    private readonly ILogger<OperationQueueService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<Waiter> _waiters = new();
    private long _nextSequence;

    public OperationQueueService(
        IUnifiedOperationTracker tracker,
        IOperationConflictChecker conflictChecker,
        ILogger<OperationQueueService> logger)
    {
        _tracker = tracker;
        _conflictChecker = conflictChecker;
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
        bool reportRefusal = false,
        RunNotice? notice = null)
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
                    notice?.Attach(_tracker, duplicateWaiter.WaitingId);
                    if (notice?.Trigger == RunTrigger.Manual && duplicateWaiter.Notice is { } retained)
                    {
                        retained.Trigger = RunTrigger.Manual;
                        _tracker.RefreshRun(duplicateWaiter.WaitingId);
                    }
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
                            metadata: new Dictionary<string, object?> { [DeclinedRunMetadata.Key] = true },
                            notice: notice);
                        // No error text. The card prints this field VERBATIM and only translates the
                        // stage key beside it, so the gate's English would reach every locale as-is
                        // and a translation key would show as the key itself. Leaving it null lets
                        // the card say why in the reader's own language.
                        _tracker.CompleteOperation(declinedId, success: true, skipped: true);
                    }

                    throw;
                }

                if (startedId.HasValue)
                {
                    notice?.Attach(_tracker, startedId.Value);
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
                conflict = await _conflictChecker.CheckAsync(type, scope, ct);
            }
            // Identical op already ACTIVE -> idempotent accept (never rejected, never doubled).
            if (GetDuplicateId(conflict, displayName) is { } activeId)
            {
                notice?.Attach(_tracker, activeId);
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
            var blockerName = ResolveBlockerName(conflict);
            waitingId = _tracker.RegisterOperation(
                type,
                displayName,
                cts,
                onTerminalCleanup: () => RemoveWaiter(waitingId),
                initialStatus: OperationStatus.Waiting,
                metadata: new Dictionary<string, object?> { ["waiting"] = true },
                blockedByName: blockerName,
                notice: notice);

            // A waiting op has no worker, so the queue is its worker: when the universal
            // cancel path cancels the CTS, complete the op as cancelled (CompletedFlag makes
            // a race with promotion's success-complete a safe who-wins).
            // Cancelled is passed explicitly rather than left to the tracker's latch: this token can
            // be cancelled by paths other than the cancel endpoint, and the card must read as
            // cancelled on all of them. No attribution — at this point the code cannot tell a person
            // clicking cancel from the app shutting down.
            // A type that keeps its parked id when it starts running shares this token with the
            // work, so the completion is asked for rather than taken: once the operation is
            // running its worker owns the terminal and reports it after releasing its own gate.
            // Whoever takes the waiter out of the queue owns its terminal: a cancel that lands after
            // promotion claimed it leaves the ending to the promotion, which names the operation
            // doing the work, so the browser never sees the waiting run end as a second card.
            var capturedWaitingId = waitingId;
            cts.Token.Register(() =>
            {
                notice?.Cancel(_tracker, capturedWaitingId);
                _ = Task.Run(() =>
                {
                    if (RemoveWaiter(capturedWaitingId)) _tracker.CancelParkedOperation(capturedWaitingId);
                });
            });

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
                    Notice = notice
                });
            }

            notice?.Attach(_tracker, waitingId);

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

    // The waiting run's row carries the blocker's name, so recording it on the tracker is what
    // updates the card, the recovery endpoint and the run list together.
    private void AnnounceBlockerChange(Waiter waiter, OperationConflictResponse conflict)
    {
        if (conflict.ActiveOperationId == waiter.LastBlockerId)
        {
            return;
        }
        waiter.LastBlockerId = conflict.ActiveOperationId;
        _tracker.SetBlockedByName(waiter.WaitingId, ResolveBlockerName(conflict));
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
    private Guid? GetDuplicateId(OperationConflictResponse? conflict, string displayName)
    {
        if (conflict?.StageKey != "errors.conflict.duplicate"
            || conflict.ActiveOperationId is not { } activeId || activeId == Guid.Empty)
        {
            return null;
        }

        var operation = _tracker.GetOperation(activeId);
        return operation != null && !operation.Status.IsTerminal() && operation.Status != OperationStatus.Waiting
            && string.Equals(operation.Name, displayName, StringComparison.Ordinal)
                ? activeId
                : null;
    }

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
                    var startedId = GetDuplicateId(conflict, waiter.Name);
                    if (conflict != null && !startedId.HasValue)
                    {
                        // Still blocked; independent-scope waiters behind it may still promote.
                        // The blocker may be a DIFFERENT operation than last announced (the one
                        // this waiter parked behind finished, and the next conflicting op took
                        // over) - re-announce so the waiting card names the current blocker.
                        AnnounceBlockerChange(waiter, conflict);
                        continue;
                    }

                    // Claim the entry before starting so a concurrent cancel cannot double-drive it.
                    if (!RemoveWaiter(waiter.WaitingId))
                    {
                        continue;
                    }

                    string? startError = null;
                    var startDeclined = false;
                    try
                    {
                        if (!startedId.HasValue)
                        {
                            if (waiter.Notice != null)
                                waiter.Notice.BlockedByOperationId = waiter.LastBlockerId;
                            using (_tracker.BeginPromotion(waiter.WaitingId, waiter.Type))
                            {
                                startedId = await waiter.Start();
                            }
                        }
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
                        // Set for the control flow below, which reads a null startError as the
                        // transient local-start-gate case and parks the waiter for another 30
                        // seconds. It never reaches the card: the terminal emit drops the text on a
                        // skipped completion, because that field is rendered verbatim.
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

                    if (startedId == waiter.WaitingId)
                    {
                        // The parked record itself is now running. Completing it here would finish
                        // the work that just started and leave the card with nothing to follow.
                        _logger.LogInformation(
                            "Promoted queued {Type} '{Name}' in place ({Id})",
                            waiter.Type, waiter.Name, waiter.WaitingId);
                        if (_tracker.GetOperation(waiter.WaitingId)?.Cancelled == true)
                        {
                            _tracker.CancelOperation(waiter.WaitingId);
                        }
                    }
                    else if (startedId.HasValue)
                    {
                        _logger.LogInformation(
                            "Promoted queued {Type} '{Name}': waiting op {WaitingId} -> running op {NewId}",
                            waiter.Type, waiter.Name, waiter.WaitingId, startedId.Value);

                        // Point the old id at the new one BEFORE closing the waiting card, so any
                        // cancel arriving from this moment on reaches the operation now doing the
                        // work rather than the parked card it replaced.
                        _tracker.RecordHandoff(waiter.WaitingId, startedId.Value);
                        waiter.Notice?.Attach(_tracker, startedId.Value);

                        // The waiting row ends as completed and names the running operation, so the
                        // browser folds it into that run's own entry.
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
                            // Skipped rather than failed: nothing went wrong, the start gate simply
                            // never freed up inside the retry window. Failed paints the card red with
                            // an internal sentence no reader can act on, and skipped also lets the
                            // schedule registry hold the run when the reason turns out to be a
                            // download - the one cause that outlasts this window by hours.
                            _tracker.CompleteOperation(
                                waiter.WaitingId,
                                success: true,
                                error: "Queued operation could not acquire its local start gate",
                                skipped: true);
                        }
                        else
                        {
                            // The waiting operation was cancelled while promotion was in flight.
                            // Promotion claimed it, so its cancel callback left the ending here.
                            _tracker.CompleteOperation(waiter.WaitingId, success: false, cancelled: true);
                        }
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
