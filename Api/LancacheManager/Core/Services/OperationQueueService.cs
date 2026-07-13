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
    }

    private readonly IUnifiedOperationTracker _tracker;
    private readonly IOperationConflictChecker _conflictChecker;
    private readonly ISignalRNotificationService _notifications;
    private readonly ILogger<OperationQueueService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<Waiter> _waiters = new();

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
        CancellationToken ct)
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

            if (conflict == null)
            {
                var startedId = await start();
                if (startedId.HasValue)
                {
                    return new QueuedOperationResponse
                    {
                        OperationId = startedId.Value,
                        Queued = false,
                        Status = "started"
                    };
                }

                // The start path internally refused (its own state says something is running).
                return new QueuedOperationResponse
                {
                    OperationId = Guid.Empty,
                    Queued = false,
                    AlreadyRunning = true,
                    Status = "alreadyRunning"
                };
            }

            // Identical op already ACTIVE -> idempotent accept (never rejected, never doubled).
            if (conflict.StageKey == "errors.conflict.duplicate"
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
                onTerminalEmit: info => info.Success
                    // Promotion: silent handover - the promoted op's own Started event
                    // replaces the waiting card.
                    ? Task.CompletedTask
                    // Cancelled from the card / failed to start at promotion: tell the
                    // frontend so the purple card terminates instead of lingering.
                    : _notifications.NotifyAllAsync(
                        SignalREvents.OperationWaitingComplete,
                        new OperationWaitingCompleteNotification(waitingId, typeWire, info.Cancelled, info.Error)),
                initialStatus: OperationStatus.Waiting);

            // A waiting op has no worker, so the queue is its worker: when the universal
            // cancel path cancels the CTS, complete the op as cancelled (CompletedFlag makes
            // a race with promotion's success-complete a safe who-wins).
            var capturedWaitingId = waitingId;
            cts.Token.Register(() => _ = Task.Run(() =>
                _tracker.CompleteOperation(capturedWaitingId, success: false, error: "Cancelled by user")));

            lock (_sync)
            {
                _waiters.Add(new Waiter
                {
                    WaitingId = waitingId,
                    Type = type,
                    Scope = scope,
                    Name = displayName,
                    Start = start
                });
            }

            _logger.LogInformation(
                "Queued {Type} '{Name}' ({Id}) behind active {ActiveType} ({ActiveId})",
                type, displayName, waitingId, conflict.ActiveOperationType, conflict.ActiveOperationId);

            await _notifications.NotifyAllAsync(
                SignalREvents.OperationWaiting,
                new OperationWaitingNotification(waitingId, typeWire, displayName));

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
                        continue; // still blocked; independent-scope waiters behind it may still promote
                    }

                    // Claim the entry before starting so a concurrent cancel cannot double-drive it.
                    if (!RemoveWaiter(waiter.WaitingId))
                    {
                        continue;
                    }

                    Guid? startedId = null;
                    string? startError = null;
                    try
                    {
                        startedId = await waiter.Start();
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
                        // Silent success handover (onTerminalEmit emits nothing for Success).
                        _tracker.CompleteOperation(waiter.WaitingId, success: true);
                    }
                    else
                    {
                        // Failed start -> terminal emit notifies the frontend card (failed state).
                        _tracker.CompleteOperation(
                            waiter.WaitingId,
                            success: false,
                            error: startError ?? "Queued operation failed to start");
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Operation queue promotion pass failed");
        }
    }
}
