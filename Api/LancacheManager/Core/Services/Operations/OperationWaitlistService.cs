using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Scheduling;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Implements the operation wait-queue (see <see cref="IOperationWaitlist"/>).
///
/// Concurrency model:
///  - <see cref="_gate"/> (async mutex) serializes enqueue decisions and promotions, closing
///    the double-start race (a blocker completing while a second conflicting op is mid-enqueue).
///  - <see cref="_sync"/> (plain lock) protects the waiter list for the synchronous callbacks
///    (onTerminalCleanup, cancel) that must not await.
///  - Promotion is triggered by <see cref="IUnifiedOperationTracker.OperationTerminal"/>, which
///    fires exactly once per op for success, failure, cancel AND force-kill (CompletedFlag gate),
///    so a crashed/force-killed blocker still unblocks its waiters, and by
///    <see cref="IUnifiedOperationTracker.BlockerCleared"/>, which fires when a repair ends or a
///    saved repair outcome lands, because neither is an operation reaching its terminal.
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
///  - the waiting op's Canceled latch is re-read AFTER the handoff is published, which catches a
///    cancel that landed earlier, while the start delegate was still running.
/// Without them a cancel clicked during promotion returned 200, ended the waiting card, and left
/// the work it had just started running to completion.
/// </summary>
public sealed class OperationWaitlistService : IOperationWaitlist
{
    private sealed class Waiter
    {
        public required Guid WaitingId { get; init; }
        public required OperationType Type { get; init; }
        /// <summary>
        /// The scope this waiter re-checks. A start that found another of its scopes busy parks it again
        /// on that one. Mutates only under <see cref="_gate"/>.
        /// </summary>
        public required ConflictScope Scope { get; set; }
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

    /// <summary>
    /// Invoked when a parked operation ends, including a cancel. The cache controller drops the waiting
    /// Remove all whose id this is and reports an ending that ran no service. Synchronous, on the completion stack.
    /// </summary>
    internal static Action<Guid>? WaitingRemoveAllEnded;
    private static readonly TimeSpan _promotionRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly IUnifiedOperationTracker _tracker;
    private readonly IOperationConflictChecker _conflictChecker;
    private readonly ILogger<OperationWaitlistService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<Waiter> _waiters = new();
    private long _nextSequence;

    public OperationWaitlistService(
        IUnifiedOperationTracker tracker,
        IOperationConflictChecker conflictChecker,
        ILogger<OperationWaitlistService> logger)
    {
        _tracker = tracker;
        _conflictChecker = conflictChecker;
        _logger = logger;

        // Single terminal hook: every op (success/failed/canceled/force-killed) funnels
        // through CompleteOperation, so every terminal event can promote waiters.
        _tracker.OperationTerminal += op => { _ = PromoteEligibleAsync(); };
        // A waiter parked on a repair has no operation terminal to wait for.
        _tracker.BlockerCleared += () => { _ = PromoteEligibleAsync(); };
    }

    public async Task<OperationStartResponse> EnqueueAsync(
        OperationType type,
        ConflictScope scope,
        string displayName,
        Func<Task<Guid?>> start,
        CancellationToken ct,
        OperationRequestKey requestKey,
        bool reportRefusal = false,
        RunNotice? notice = null,
        string? target = null)
    {
        RequireKeyOfType(type, requestKey);
        await _gate.WaitAsync(ct);
        try
        {
            // Dedup against the queue first: an identical request already parked is returned
            // as-is (never two waiting copies of the same destructive work).
            lock (_sync)
            {
                // A count is read-only and belongs to the dialog that asked for it, so each request
                // waits on its own: a joined count would end when any one of its dialogs closed.
                var duplicateWaiter = _waiters.FirstOrDefault(w =>
                    w.Type == type
                    && type != OperationType.CacheFileCount
                    // A corruption removal with no target covers a set of services and parks on whichever is
                    // busy, so its scope does not name it: its endpoint matches it by scan and services.
                    && (type != OperationType.CorruptionRemoval || target != null)
                    && w.Scope.Matches(scope)
                    // A waiter being canceled leaves this list a moment later, on another thread; a new
                    // request that joined it would end with it.
                    // Two requests are the same when type, scope, target and request key match; a request
                    // asking for other work (other tables, other limits, another crawl mode, other scan settings)
                    // waits on its own card and runs after. A schedule's requests carry no target, so they match
                    // by type, which names the schedule, and by key. The English name is for logs.
                    && _tracker.GetOperation(w.WaitingId) is { Status: OperationStatus.Waiting } waiting
                    && string.Equals(waiting.Target, target, StringComparison.Ordinal)
                    && waiting.RequestKey == requestKey);
                // A request whose join was refused (that waiter is being canceled, or its own card
                // stopped waiting) is not a duplicate: it goes on to the conflict check and waits or starts
                // on its own. The waiting run's own lock is taken by Adopt, so a person's click raises its
                // trigger and actor together, and the scan type the person pressed is kept.
                if (duplicateWaiter != null
                    && (notice is not { OperationId: not null, Canceled: false }
                        || notice.CloseInto(_tracker, duplicateWaiter.WaitingId))
                    && (notice is null || duplicateWaiter.Notice is not { } retained || retained.Adopt(_tracker, notice)))
                {
                    return new OperationStartResponse
                    {
                        OperationId = duplicateWaiter.WaitingId,
                        Status = OperationStartStatus.Waiting
                    };
                }
            }

            // Re-check under the gate: the blocker may have finished before this enqueue
            // committed - in that case start immediately instead of parking forever.
            var conflict = await _conflictChecker.CheckAsync(type, scope, ct);

            var parkScope = scope;
            var retryAfterParking = false;
            if (conflict == null)
            {
                Guid? startedId;
                // The card the notice named before the start. A start that re-held its run on a new
                // card (a download began while it ran) leaves the notice on that card, and attaching
                // it to the started run afterwards would close the hold's card into a run that ended.
                var cardBeforeStart = notice?.OperationId;
                try
                {
                    startedId = await start();
                }
                catch (DownloadInProgressException ex)
                {
                    // The same refusal the promotion path reports, arriving through the immediate
                    // door: the schedule gate said yes, then a download began before the start
                    // delegate ran its own check. Named here as a decline so it is recorded the same
                    // way whoever called, and not left to each caller to notice. Rethrown unchanged,
                    // because an HTTP caller is waiting on this and its
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
                        // A request that already has a waiting card hands it on to this row, so the run's
                        // end finds no waiting card to end as skipped and a hold that follows links to this row.
                        if (notice is { OperationId: not null }) notice.Attach(_tracker, declinedId);
                        // No error text. The card prints this field VERBATIM and only translates the
                        // stage key beside it, so the gate's English would reach every locale as-is
                        // and a translation key would show as the key itself. Leaving it null lets
                        // the card say why in the reader's own language.
                        // Marked as a download refusal on its own row, so the schedule holds the run on the
                        // refusal itself and not on the download gate asked again later.
                        _tracker.CompleteOperation(declinedId, success: true, skipped: true, onCompleting: MarkSkippedForDownload);
                    }

                    throw;
                }
                catch (OperationBlockedException blocked)
                {
                    // The start touches more than this scope and another of them is busy: the request
                    // waits on that one like any other parked request.
                    startedId = null;
                    parkScope = blocked.Scope;
                    conflict = blocked.Conflict;
                }

                if (startedId.HasValue)
                {
                    RecordRequestKey(startedId.Value, requestKey);
                    if (notice is not null && notice.OperationId == cardBeforeStart) notice.Attach(_tracker, startedId.Value);
                    return new OperationStartResponse
                    {
                        OperationId = startedId.Value,
                        Status = OperationStartStatus.Started
                    };
                }

                if (conflict == null)
                {
                    // A local service gate can briefly outlive the tracker operation that owned it.
                    // Preserve this request as a real waiter and let the bounded promotion retry
                    // acquire that gate after the previous worker finishes unwinding.
                    retryAfterParking = true;
                    conflict = await _conflictChecker.CheckAsync(type, scope, ct);
                }
            }
            // Identical op already ACTIVE -> idempotent accept (never rejected, never doubled).
            // Refused: the running duplicate turned Canceling after GetDuplicateId read it. The request parks
            // below and runs once that run ends.
            if (GetDuplicateId(conflict, requestKey) is { } activeId
                && (notice is not { OperationId: not null, Canceled: false } || notice.CloseInto(_tracker, activeId)))
            {
                return new OperationStartResponse
                {
                    OperationId = activeId,
                    Status = OperationStartStatus.AlreadyRunning
                };
            }

            // Park it: real tracker registration (status Waiting) so universal cancel works
            // and the frontend card has a real operationId (no ghost-notification shape).
            var cts = new CancellationTokenSource();
            Guid waitingId = default;
            var blocker = ResolveBlocker(conflict);
            // The waiting row carries the scan settings its key names, so the card names the scan it waits to run.
            var scanKey = requestKey as CorruptionDetectionRequestKey;
            waitingId = _tracker.RegisterOperation(
                type,
                displayName,
                cts,
                onTerminalCleanup: () =>
                {
                    RemoveWaiter(waitingId);
                    WaitingRemoveAllEnded?.Invoke(waitingId);
                },
                initialStatus: OperationStatus.Waiting,
                metadata: new Dictionary<string, object?> { ["waiting"] = true },
                blockedByName: blocker?.Name,
                notice: notice,
                target: target,
                blockedByType: blocker?.Type,
                blockedByTarget: blocker?.Target,
                scanMode: scanKey?.Mode,
                scanThreshold: scanKey?.Threshold,
                scanLookbackDays: scanKey?.LookbackDays,
                blockedByFullRepair: blocker?.Metadata is CacheClearingRepair { FullRepair: true },
                detectionScanType: (requestKey as GameDetectionRequestKey)?.ScanType);
            RecordRequestKey(waitingId, requestKey);

            // A waiting op has no worker, so the queue is its worker: when the universal
            // cancel path cancels the CTS, complete the op as canceled (CompletedFlag makes
            // a race with promotion's success-complete a safe who-wins).
            // Canceled is passed explicitly rather than left to the tracker's latch: this token can
            // be canceled by paths other than the cancel endpoint, and the card must read as
            // canceled on all of them. No attribution — at this point the code cannot tell a person
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
                    Scope = parkScope,
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
                    "Waiting {Type} '{Name}' ({Id}) after its local start gate temporarily refused",
                    type,
                    displayName,
                    waitingId);
            }
            else
            {
                _logger.LogInformation(
                    "Waiting {Type} '{Name}' ({Id}) behind active {ActiveType} ({ActiveId})",
                    type, displayName, waitingId, conflict.ActiveOperationType, conflict.ActiveOperationId);
            }

            if (retryAfterParking)
            {
                _ = PromoteAfterRetryDelayAsync();
            }

            return new OperationStartResponse
            {
                OperationId = waitingId,
                Status = OperationStartStatus.Waiting
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationConflictResponse?> StartUnlessBusyAsync(
        OperationType type,
        ConflictScope scope,
        Func<Task<Guid?>> start,
        CancellationToken ct,
        OperationRequestKey requestKey)
    {
        RequireKeyOfType(type, requestKey);
        await _gate.WaitAsync(ct);
        try
        {
            var conflict = await _conflictChecker.CheckAsync(type, scope, ct);
            if (conflict != null)
            {
                return conflict;
            }

            // Recorded on the run the start registered and on no other: a run of the same type that registers
            // meanwhile (a depot Apply mappings click) did not ask for this work.
            if (await start() is { } startedId)
            {
                RecordRequestKey(startedId, requestKey);
            }
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The blocker comes from the live tracker rather than the conflict response: the response
    /// carries neither the blocker's name nor its target. A blocker that already left the tracker
    /// resolves to null (generic waiting text) - its terminal event is about to trigger a promotion
    /// pass anyway.
    /// </summary>
    private OperationInfo? ResolveBlocker(OperationConflictResponse? conflict)
    {
        if (conflict?.ActiveOperationId is not { } blockerId || blockerId == Guid.Empty)
        {
            return null;
        }
        // A child step (an eviction scan's own detection) is not a job of its own, so a request waiting on
        // it names the scan that owns it.
        var blocker = _tracker.GetOperation(blockerId);
        return blocker?.ParentOperationId is { } parentId ? _tracker.GetOperation(parentId) : blocker;
    }

    // The waiting run's row carries the blocker's name, type and target, so recording them on the
    // tracker is what updates the card, the recovery endpoint and the run list together.
    private void AnnounceBlockerChange(Waiter waiter, OperationConflictResponse conflict)
    {
        if (conflict.ActiveOperationId == waiter.LastBlockerId)
        {
            return;
        }
        waiter.LastBlockerId = conflict.ActiveOperationId;
        var blocker = ResolveBlocker(conflict);
        _tracker.SetBlockedByName(waiter.WaitingId, blocker?.Name, blocker?.Type, blocker?.Target,
            blocker?.Metadata is CacheClearingRepair { FullRepair: true });
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
    private Guid? GetDuplicateId(OperationConflictResponse? conflict, OperationRequestKey requestKey)
    {
        if (conflict?.StageKey != "errors.conflict.duplicate"
            || conflict.ActiveOperationId is not { } activeId || activeId == Guid.Empty)
        {
            return null;
        }

        var operation = _tracker.GetOperation(activeId);
        // Neither a run being canceled nor an image fetch pass answers a new request: the canceled run
        // stops before it does the work, and a pass read its work list when it started. The request waits
        // and runs once that operation ends.
        // A running operation answers a new request only when it is known to do that work: a schedule's
        // run, or a corruption scan with the same mode, miss threshold and lookback, the values the scan
        // service compares. Other running operations do not carry the target of the request they serve,
        // so the request waits and runs after them. A child step (an eviction scan's own detection) shares a
        // schedule's type but serves only its parent, so it never answers a schedule's request. A game
        // detection answers only a request for the same scan type: the other type waits and runs its own
        // scan after. Any other request is answered only by a run whose key is equal; a run started without
        // the queue carries the plain key, so a request with a key of its own waits and runs after it.
        return operation != null && !operation.Status.IsTerminal() && operation.Status != OperationStatus.Waiting
            && operation.Status != OperationStatus.Canceling
            && operation.Type != OperationType.GameImageFetch
            && Describes(operation, requestKey)
            && (operation.ParentOperationId is null && ScheduleOperationTypes.FindServiceKey(operation.Type) is not null
                || operation.Type == OperationType.CorruptionDetection)
                ? activeId
                : null;
    }

    // Whether a running operation does the work a request's key asks for. A scan is matched on the settings it
    // was registered with, because a scan started without the queue carries them too: a corruption scan's mode,
    // miss threshold and lookback, the values the scan service compares (CorruptionDetectionService.StartDetectionAsync),
    // and a game detection's scan type. Every other key is matched on the key the queue recorded on the run.
    private static bool Describes(OperationInfo operation, OperationRequestKey requestKey) => requestKey switch
    {
        CorruptionDetectionRequestKey scan => operation.ScanMode == scan.Mode
            && operation.ScanThreshold == scan.Threshold
            && operation.ScanLookbackDays == scan.LookbackDays,
        GameDetectionRequestKey detection => operation.DetectionScanType == detection.ScanType,
        _ => operation.RequestKey == requestKey
    };

    // A job type whose requests differ takes its own key, so a caller of that type that passed the plain key, or
    // another type's key, would join or answer work it did not ask for.
    private static void RequireKeyOfType(OperationType type, OperationRequestKey requestKey)
    {
        var expected = type switch
        {
            OperationType.DatabaseReset => typeof(DatabaseResetRequestKey),
            OperationType.UnmappedFileRemoval => typeof(UnmappedFileRemovalRequestKey),
            OperationType.CorruptionRemoval => typeof(CorruptionRemovalRequestKey),
            OperationType.CorruptionDetection => typeof(CorruptionDetectionRequestKey),
            OperationType.GameDetection => typeof(GameDetectionRequestKey),
            OperationType.GameImageFetch => typeof(GameImageFetchRequestKey),
            OperationType.DepotMapping => typeof(DepotMappingRequestKey),
            _ => typeof(OperationRequestKey)
        };
        if (requestKey.GetType() != expected)
        {
            throw new ArgumentException(
                $"A {type} request takes a {expected.Name}, not a {requestKey.GetType().Name}.", nameof(requestKey));
        }
    }

    // Written under _gate, where every join reads it.
    private void RecordRequestKey(Guid operationId, OperationRequestKey requestKey)
    {
        if (_tracker.GetOperation(operationId) is { } operation)
        {
            operation.RequestKey = requestKey;
        }
    }

    // Set only where the refusal is a DownloadInProgressException, never for a start gate that stayed busy.
    private static void MarkSkippedForDownload(OperationInfo operation) => operation.SkippedForDownload = true;

    private async Task PromoteEligibleAsync()
    {
        // A pass raised from a repair's flow carries the repair's silence limit, and a promoted job
        // is never that repair's own step. Cleared here, so it does not flow back to the caller.
        RustProcessHelper.ChildSilenceLimit.Value = null;
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

                    // The waiting run's own record holds the request key, written when it parked; a record
                    // already gone asks for the plain work of its type.
                    var waiterKey = _tracker.GetOperation(waiter.WaitingId)?.RequestKey ?? OperationRequestKey.Plain;
                    var conflict = await _conflictChecker.CheckAsync(waiter.Type, waiter.Scope, CancellationToken.None);
                    var duplicateId = GetDuplicateId(conflict, waiterKey);
                    if (conflict != null && !duplicateId.HasValue)
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

                    Guid? startedId = null;
                    string? startError = null;
                    string? startErrorDetail = null;
                    RunWarning? startFailureReason = null;
                    var startDeclined = false;
                    var nothingToDo = false;
                    OperationBlockedException? stillBlocked = null;
                    if (duplicateId is { } runningId)
                    {
                        // A run of the same work is already going: the waiting card closes into it as a join, so
                        // dismissing the card never stops that run. Refused (the run is ending, or the card was
                        // canceled): the card takes the re-park branch below.
                        if (_tracker.TryCloseInto(waiter.WaitingId, runningId))
                        {
                            _logger.LogInformation(
                                "Waiting {Type} '{Name}' joined running op {RunningId}", waiter.Type, waiter.Name, runningId);
                            continue;
                        }
                    }
                    else
                    {
                        try
                        {
                            if (waiter.Notice != null)
                                waiter.Notice.BlockedByOperationId = waiter.LastBlockerId;
                            using (_tracker.BeginPromotion(waiter.WaitingId, waiter.Type))
                            {
                                startedId = await waiter.Start();
                            }
                        }
                        catch (DownloadInProgressException ex)
                        {
                            // Only this one condition is a decline. Being parked behind another
                            // operation must not turn a download into a red failure: the row ends
                            // skipped and marked, and the schedule holds the run on a waiting card until
                            // downloads end. Every other stated precondition that throws the base
                            // ValidationException - a wrong PUID or PGID failing a write-permission
                            // re-check, a datasource that cannot map logical objects - is a real
                            // problem the reader has to see, and falls through to the handler below.
                            // This catch must stay above that one; the derived type is unreachable
                            // otherwise.
                            startDeclined = true;
                            // Set for the control flow below, which reads a null startError as the
                            // transient local-start-gate case and parks the waiter for another 30
                            // seconds. It is the skipped row's message: the decline's own key, which
                            // every refusal carries.
                            startError = ex.StageKey;
                            _logger.LogInformation(
                                "Waiting {Type} '{Name}' declined at promotion: {Reason}",
                                waiter.Type, waiter.Name, ex.Message);
                        }
                        catch (OperationBlockedException blocked)
                        {
                            stillBlocked = blocked;
                        }
                        catch (NothingToDoException)
                        {
                            // The work was done while the request waited: the card ends skipped on the
                            // nothing-to-do line, not red.
                            nothingToDo = true;
                            startError = ScheduledRunReporter.NothingToDoStageKey;
                        }
                        catch (Exception ex)
                        {
                            // A refusal's key reaches the card, which shows it in the reader's language; its values
                            // travel as the run's failure reason, which the card words the same way. An error with no
                            // key is not worded for a reader: it ends on the generic key, and the row keeps its text as
                            // the error detail.
                            startError = ex switch
                            {
                                ApiException { StageKey: { } refusalKey } => refusalKey,
                                _ => "signalr.generic.failed"
                            };
                            if (ex is ApiException { StageKey: { } key, Context: { } context })
                            {
                                startFailureReason = new RunWarning(key, context);
                            }
                            // A keyed refusal is worded by its key; every ending on the generic key keeps the text as detail.
                            startErrorDetail = ex is ApiException { StageKey: not null } ? null : ex.Message;
                            _logger.LogError(ex, "Waiting {Type} '{Name}' failed to start at promotion", waiter.Type, waiter.Name);
                        }
                    }

                    if (stillBlocked != null)
                    {
                        // The start touches more than the scope this waiter re-checked and another of them is
                        // busy: it parks again on that scope and waits for that blocker's end, with no retry
                        // limit, like any parked request. A cancel that landed during the start left the
                        // ending to this pass.
                        var reparked = false;
                        lock (_sync)
                        {
                            if (_tracker.GetOperation(waiter.WaitingId)?.Status == OperationStatus.Waiting)
                            {
                                waiter.Scope = stillBlocked.Scope;
                                _waiters.Add(waiter);
                                _waiters.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
                                reparked = true;
                            }
                        }

                        if (reparked)
                        {
                            AnnounceBlockerChange(waiter, stillBlocked.Conflict);
                            _logger.LogInformation(
                                "Waiting {Type} '{Name}' parked again on {Scope} behind {ActiveType} ({ActiveId})",
                                waiter.Type, waiter.Name, stillBlocked.Scope.ToTrackerKey(),
                                stillBlocked.Conflict.ActiveOperationType, stillBlocked.Conflict.ActiveOperationId);
                        }
                        else
                        {
                            _tracker.CompleteOperation(waiter.WaitingId, success: false, canceled: true);
                        }
                        continue;
                    }

                    if (startedId == waiter.WaitingId)
                    {
                        // The parked record itself is now running. Completing it here would finish
                        // the work that just started and leave the card with nothing to follow.
                        _logger.LogInformation(
                            "Promoted waiting {Type} '{Name}' in place ({Id})",
                            waiter.Type, waiter.Name, waiter.WaitingId);
                        if (_tracker.GetOperation(waiter.WaitingId)?.Canceled == true)
                        {
                            _tracker.CancelOperation(waiter.WaitingId);
                        }
                    }
                    else if (startedId.HasValue)
                    {
                        _logger.LogInformation(
                            "Promoted waiting {Type} '{Name}': waiting op {WaitingId} -> running op {NewId}",
                            waiter.Type, waiter.Name, waiter.WaitingId, startedId.Value);

                        // Point the old id at the new one BEFORE closing the waiting card, so any
                        // cancel arriving from this moment on reaches the operation now doing the
                        // work rather than the parked card it replaced.
                        _tracker.RecordHandoff(waiter.WaitingId, startedId.Value);
                        RecordRequestKey(startedId.Value, waiterKey);
                        waiter.Notice?.Attach(_tracker, startedId.Value);

                        // The waiting row ends as completed and names the running operation, so the
                        // browser folds it into that run's own entry.
                        _tracker.CompleteOperation(waiter.WaitingId, success: true);

                        // A cancel that landed WHILE Start() was running hit a waiting operation
                        // that no longer drove anything: it ended the parked card and left the work
                        // it had just started running, so the user was told the operation was
                        // canceled while it carried on. The Canceled latch is set before the token
                        // is canceled and is never cleared, so that click is still readable here.
                        if (_tracker.GetOperation(waiter.WaitingId)?.Canceled == true)
                        {
                            _logger.LogInformation(
                                "Waiting {Type} '{Name}' was canceled during promotion; canceling promoted op {NewId}",
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
                        var parkedAgain = false;
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
                                    parkedAgain = true;
                                }
                                else
                                {
                                    retryLimitReached = true;
                                }
                            }
                        }

                        if (parkedAgain)
                        {
                            retryRequested = true;
                            _logger.LogDebug(
                                "Waiting {Type} '{Name}' temporarily refused promotion; retry {Attempt}/{MaxAttempts}",
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
                                error: "signalr.generic.skipped",
                                skipped: true);
                        }
                        else
                        {
                            // The waiting operation was canceled while promotion was in flight.
                            // Promotion claimed it, so its cancel callback left the ending here.
                            _tracker.CompleteOperation(waiter.WaitingId, success: false, canceled: true);
                        }
                    }
                    else
                    {
                        // Terminal either way; the card just has to say which. A decline carries its
                        // reason on a skipped card, everything else on a failed one.
                        _tracker.CompleteOperation(
                            waiter.WaitingId,
                            success: startDeclined || nothingToDo,
                            error: startError,
                            skipped: startDeclined || nothingToDo,
                            onCompleting: startDeclined
                                ? MarkSkippedForDownload
                                : startErrorDetail is { } detail
                                    ? (OperationInfo operation) => operation.ErrorDetail = detail
                                    : startFailureReason is { } reason
                                        ? (OperationInfo operation) => operation.FailureReason = reason
                                        : null);
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

/// <summary>
/// Thrown by a start path that touches more than one scope when a scope other than the one its request
/// waited on is busy. The queue parks the request again on <see cref="Scope"/> instead of starting it.
/// </summary>
public sealed class OperationBlockedException(ConflictScope scope, OperationConflictResponse conflict) : Exception
{
    public ConflictScope Scope => scope;
    public OperationConflictResponse Conflict => conflict;
}

/// <summary>
/// Thrown by a start path that finds the work it was asked for already done. A request started from the
/// queue ends skipped; a direct request answers 404 like any other missing resource.
/// </summary>
public sealed class NothingToDoException(string resource) : NotFoundException(resource);
