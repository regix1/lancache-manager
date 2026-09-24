using System.Collections.Concurrent;
using System.Diagnostics;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Unified operation tracker that manages all long-running operations across the application.
/// Provides cancellation support, progress tracking, and force kill capabilities.
/// </summary>
public class UnifiedOperationTracker : IUnifiedOperationTracker
{
    private readonly ConcurrentDictionary<Guid, OperationInfo> _operations = new();
    private readonly ConcurrentDictionary<(OperationType Type, string EntityKey), Guid> _entityKeyIndex = new();

    /// <summary>
    /// old operation id -> the operation that took over its work (see <see cref="RecordHandoff"/>).
    /// Entries outlive reaped waiting rows until their resolved terminal target is reaped.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Guid> _handoffs = new();

    /// <summary>
    /// Bound on how far <see cref="ResolveHandoff"/> will follow a chain. A handoff always points at
    /// a freshly-registered id so a cycle cannot form, but a bounded walk means a bug upstream
    /// degrades to "cancel did nothing" instead of hanging the request thread.
    /// </summary>
    private const int MaxHandoffDepth = 8;

    private readonly ProcessManager _processManager;
    private readonly ILogger<UnifiedOperationTracker> _logger;
    private readonly ISignalRNotificationService? _notifications;

    // Revision of the last row stamped. Written only under _pendingRunsLock; GetRuns reads it
    // before listing so the browser can tell a run that ended from one it has not heard of yet.
    private long _revision;
    // Rows waiting to be sent, in revision order. Lock order on every path: the schedule
    // registry's kept-endings lock -> an operation lock -> _pendingRunsLock, and an operation
    // lock -> a notice lock (through token callbacks). Nothing takes an operation lock while
    // holding _pendingRunsLock or a notice lock, and no new path holds two operation locks.
    private readonly Queue<OperationRun> _pendingRuns = new();
    private readonly object _pendingRunsLock = new();
    // Serializes the async sends so rows leave in the order their revisions were stamped.
    private readonly SemaphoreSlim _publishGate = new(1, 1);

    // The waiting operation whose start delegate is running in this async flow, so the run it
    // registers names it as its predecessor.
    private readonly AsyncLocal<(Guid WaitingId, OperationType Type)?> _promotion = new();

    // Notifications are optional so the unit tests that construct the tracker directly keep
    // compiling; at runtime the DI container always supplies the registered singleton.
    public UnifiedOperationTracker(ProcessManager processManager, ILogger<UnifiedOperationTracker> logger, ISignalRNotificationService? notifications = null)
    {
        _processManager = processManager;
        _logger = logger;
        _notifications = notifications;
    }

    /// <inheritdoc />
    public event Action<OperationInfo>? OperationTerminal;

    public Guid RegisterOperation(OperationType type, string name, CancellationTokenSource cts,
                                  object? metadata = null, Action? onTerminalCleanup = null,
                                  Func<OperationTerminalInfo, Task>? onTerminalEmit = null,
                                  OperationStatus initialStatus = OperationStatus.Running,
                                  Guid? parentOperationId = null, DateTime? startedAt = null,
                                  string? blockedByName = null, RunNotice? notice = null,
                                  bool liveIngest = false, Guid? ownerSessionId = null)
    {
        var operationId = Guid.NewGuid();
        var operation = new OperationInfo
        {
            Id = operationId,
            ParentOperationId = parentOperationId,
            Type = type,
            Name = name,
            Status = initialStatus,
            Message = $"Starting {name}...",
            StartedAt = startedAt ?? DateTime.UtcNow,
            CancellationTokenSource = cts,
            Metadata = metadata,
            OnTerminalCleanup = onTerminalCleanup,
            OnTerminalEmit = onTerminalEmit,
            BlockedByName = blockedByName,
            Notice = notice,
            LiveIngest = liveIngest,
            OwnerSessionId = ownerSessionId,
            WorkerStarted = initialStatus != OperationStatus.Waiting
        };

        // Held from before the insert until the first row is stamped, so a snapshot taken
        // meanwhile waits for it and never lists a run without a revision.
        lock (operation)
        {
            operation.PreviousOperationId = FindPreviousOperationId(type, notice);
            if (!_operations.TryAdd(operationId, operation))
            {
                _logger.LogError("Failed to register operation: {Name} (ID: {Id})", name, operationId);
                throw new InvalidOperationException($"Failed to register operation {operationId}");
            }

            var indexKey = BuildIndexKey(type, metadata);
            if (indexKey != null)
            {
                _entityKeyIndex[(type, indexKey)] = operationId;
            }

            Publish(operation);
        }

        _ = DrainRunsAsync();
        _logger.LogInformation("Registered {Type} operation: {Name} (ID: {Id})", type, name, operationId);
        return operationId;
    }

    public bool TryRestoreOperation(Guid operationId, OperationType type, string name, CancellationTokenSource cts,
                                    object? metadata = null, Action? onTerminalCleanup = null,
                                    Func<OperationTerminalInfo, Task>? onTerminalEmit = null,
                                    Guid? parentOperationId = null, DateTime? startedAt = null,
                                    RunNotice? notice = null)
    {
        var operation = new OperationInfo
        {
            Id = operationId,
            ParentOperationId = parentOperationId,
            Type = type,
            Name = name,
            Status = OperationStatus.Running,
            Message = $"Starting {name}...",
            StartedAt = startedAt ?? DateTime.UtcNow,
            CancellationTokenSource = cts,
            Metadata = metadata,
            OnTerminalCleanup = onTerminalCleanup,
            OnTerminalEmit = onTerminalEmit,
            Notice = notice,
            WorkerStarted = true
        };

        var restored = false;
        // Same reason as RegisterOperation: no snapshot may list the run before its first row.
        lock (operation)
        {
            operation.PreviousOperationId = FindPreviousOperationId(type, notice);
            if (_operations.TryAdd(operationId, operation))
            {
                var indexKey = BuildIndexKey(type, metadata);
                if (indexKey != null)
                {
                    _entityKeyIndex[(type, indexKey)] = operationId;
                }

                Publish(operation);
                restored = true;
            }
        }

        if (restored)
        {
            _ = DrainRunsAsync();
            _logger.LogInformation("Restored {Type} operation: {Name} (ID: {Id})", type, name, operationId);
            return true;
        }

        // core-7: the tracker did NOT adopt this CTS (the ID was already in use). The tracker is the
        // single disposer ONLY for CTSs it adopts; an un-adopted CTS stays owned by the caller, which
        // disposes it in its restore false-branch (see RestoreInterruptedOperations / IUnifiedOperationTracker docs).
        _logger.LogWarning("Failed to restore operation: {Name} (ID: {Id}) - ID already registered", name, operationId);
        return false;
    }

    public void RecordHandoff(Guid fromOperationId, Guid toOperationId)
    {
        if (fromOperationId == toOperationId || toOperationId == Guid.Empty)
        {
            return;
        }

        _handoffs.TryAdd(fromOperationId, toOperationId);
        _logger.LogDebug(
            "Operation {FromId} handed its work to {ToId}; cancels aimed at the old id now follow it",
            fromOperationId, toOperationId);
    }

    public bool BeginQueuedOperation(
        Guid operationId,
        object? state,
        Action? onTerminalCleanup,
        Func<OperationTerminalInfo, Task>? onTerminalEmit)
    {
        if (!_operations.TryGetValue(operationId, out var operation))
        {
            return false;
        }

        lock (operation)
        {
            // A cancel already claimed this operation, so refusing here leaves the queue's
            // parked-cancel path to finish it rather than starting work that must stop at once.
            if (operation.CompletedFlag != 0 || operation.Status != OperationStatus.Waiting || operation.Cancelled)
            {
                return false;
            }

            operation.Status = OperationStatus.Running;
            operation.Message = $"Starting {operation.Name}...";
            operation.Metadata = state;
            operation.OnTerminalCleanup = onTerminalCleanup;
            operation.OnTerminalEmit = onTerminalEmit;
            operation.WorkerStarted = true;
            Publish(operation);
        }

        _ = DrainRunsAsync();
        _logger.LogInformation(
            "Operation {Id} ({Type}: {Name}) left the queue and is running",
            operationId, operation.Type, operation.Name);
        return true;
    }

    public bool CancelParkedOperation(Guid operationId)
    {
        if (!_operations.TryGetValue(operationId, out var operation))
        {
            return false;
        }

        lock (operation)
        {
            // Once an operation runs under its own id, its worker reports the terminal after
            // releasing whatever gate it holds. Ending it here would close the card while the
            // work is still unwinding and free the queue to promote into a held gate.
            if (operation.CompletedFlag != 0 || !IsParked(operation))
            {
                return false;
            }
        }

        CompleteOperation(operationId, success: false, cancelled: true);
        return true;
    }

    /// <summary>
    /// True while an operation is its parker's to finish: registered as waiting and never begun by a
    /// worker. A cancel moves it to <see cref="OperationStatus.Cancelling"/> without giving it one,
    /// so that state still counts as parked; once <see cref="BeginQueuedOperation"/> runs it, its
    /// worker reports the terminal.
    /// </summary>
    private static bool IsParked(OperationInfo operation) =>
        !operation.WorkerStarted &&
        (operation.Status == OperationStatus.Waiting || operation.Status == OperationStatus.Cancelling);

    /// <summary>
    /// Follow any recorded handoff to the operation actually doing the work. Returns the id
    /// unchanged when nothing has taken over.
    /// </summary>
    private Guid ResolveHandoff(Guid operationId)
    {
        var resolved = operationId;
        for (var hop = 0; hop < MaxHandoffDepth; hop++)
        {
            if (!_handoffs.TryGetValue(resolved, out var next) || next == resolved)
            {
                return resolved;
            }
            resolved = next;
        }

        _logger.LogWarning(
            "Handoff chain from operation {Id} exceeded {MaxDepth} hops — cancelling the last one reached",
            operationId, MaxHandoffDepth);
        return resolved;
    }

    public OperationCancelResult CancelOperation(Guid requestedOperationId, bool followHandoff = true)
    {
        // The caller's card may still carry the id of an operation that has already handed its work
        // to another one (the wait-queue promoting a parked operation is the case that matters).
        // Cancelling the id as given would stop nothing while reporting success.
        var operationId = followHandoff ? ResolveHandoff(requestedOperationId) : requestedOperationId;
        if (operationId != requestedOperationId)
        {
            _logger.LogInformation(
                "Cancel for operation {RequestedId} follows its handoff to {ActualId}",
                requestedOperationId, operationId);
        }

        if (!_operations.TryGetValue(operationId, out var operation))
        {
            _logger.LogWarning("Operation {Id} not found for cancellation", operationId);
            return OperationCancelResult.NotFound;
        }

        CancellationTokenSource? cts;
        Process? process;
        lock (operation)
        {
            if (operation.CompletedFlag != 0 || operation.Status.IsTerminal())
            {
                return OperationCancelResult.AlreadyFinished;
            }
            cts = operation.CancellationTokenSource;
            process = operation.AssociatedProcess;
            operation.Status = OperationStatus.Cancelling;
            operation.Cancelled = true;
            operation.Message = "Cancellation requested...";
            Publish(operation);
        }

        _ = DrainRunsAsync();
        try
        {
            _logger.LogInformation(
                "Requesting aggressive cancellation for operation {Id} ({Type}: {Name})",
                operationId, operation.Type, operation.Name);

            // Cancel before killing so the token is already signaled by the time the child dies.
            // Killing first leaves a window where the run sees a non-zero exit with a live token and
            // reports a process failure. Cancel() itself kills through the token-cancel registration
            // every tracked run installs, and the explicit kill below covers a run without one.
            cts?.Cancel();
            TryKillAssociatedProcess(operation, operationId, process);
        }
        catch (ObjectDisposedException)
        {
            // P2-C: the operation completed concurrently and CompleteOperation disposed the CTS.
            // The op is already terminal — cancellation is a benign no-op.
            // The operation reached a terminal state while we were cancelling, so the request
            // stopped nothing the caller could still see.
            _logger.LogDebug("Operation {Id} completed concurrently during cancel — CTS already disposed", operationId);
            return OperationCancelResult.AlreadyFinished;
        }

        // The token callback can complete the operation before Cancel returns. That completion
        // does not undo the cancellation accepted by this call.
        return OperationCancelResult.Requested;
    }

    public void AssociateProcess(Guid operationId, Process process)
    {
        if (_operations.TryGetValue(operationId, out var operation))
        {
            lock (operation)
            {
                if (operation.CompletedFlag != 0 || operation.Status.IsTerminal()) return;
                operation.AssociatedProcess = process;
            }
            _logger.LogDebug(
                "Associated process {ProcessName} (PID: {Pid}) with operation {Id}",
                process.ProcessName, process.Id, operationId);
        }
    }

    public void DisassociateProcess(Guid operationId, Process process)
    {
        if (_operations.TryGetValue(operationId, out var operation))
        {
            lock (operation)
            {
                if (operation.CompletedFlag == 0 && !operation.Status.IsTerminal()
                    && ReferenceEquals(operation.AssociatedProcess, process))
                {
                    operation.AssociatedProcess = null;
                }
            }
        }
    }

    public bool ForceKillOperation(Guid requestedOperationId, bool followHandoff = true)
    {
        // Same reasoning as CancelOperation: follow the work, not the id the caller happens to hold.
        var operationId = followHandoff ? ResolveHandoff(requestedOperationId) : requestedOperationId;
        if (!_operations.TryGetValue(operationId, out var operation))
        {
            _logger.LogWarning("Operation {Id} not found for force kill", operationId);
            return false;
        }

        CancellationTokenSource? cts;
        Process? process;
        lock (operation)
        {
            if (operation.CompletedFlag != 0 || operation.Status.IsTerminal()) return true;
            cts = operation.CancellationTokenSource;
            process = operation.AssociatedProcess;
            operation.Status = OperationStatus.Cancelling;
            operation.Cancelled = true;
            operation.Message = "Force killed by user";
            Publish(operation);
        }

        _ = DrainRunsAsync();
        _logger.LogWarning(
            "Force killing operation {Id} ({Type}: {Name})",
            operationId, operation.Type, operation.Name);

        try
        {
            cts?.Cancel();
            TryKillAssociatedProcess(operation, operationId, process);
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("Operation {Id} completed concurrently during force kill — CTS already disposed", operationId);
        }

        return true;
    }

    private bool TryKillAssociatedProcess(OperationInfo operation, Guid operationId, Process? process)
    {
        if (process == null)
        {
            return false;
        }

        // No HasExited pre-check here: the run's own thread disposes the process the moment its work
        // ends, and HasExited throws on a disposed process, so the guard threw out of a method whose
        // name promises it cannot. KillProcessTree makes the same check safely and returns false for
        // an exited or disposed process, which drops through to the same cleanup below.
        var killed = _processManager.KillProcessTree(
            process,
            $"operation {operationId} ({operation.Type}: {operation.Name})");
        if (!killed)
        {
            DisassociateProcess(operationId, process);
        }

        return killed;
    }

    public OperationInfo? GetOperation(Guid operationId, bool followHandoff = false)
    {
        if (followHandoff) operationId = ResolveHandoff(operationId);
        return _operations.TryGetValue(operationId, out var operation) ? operation : null;
    }

    public IEnumerable<OperationInfo> GetActiveOperations(OperationType? filterType = null)
    {
        // Waiting ops are queued, not running: they must not block conflict checks and must
        // stay invisible to the per-type status/recovery endpoints (see IUnifiedOperationTracker).
        var operations = _operations.Values.Where(op => !op.Status.IsTerminal() && !IsParked(op));

        if (filterType.HasValue)
        {
            operations = operations.Where(op => op.Type == filterType.Value);
        }

        return operations.ToList();
    }

    public IEnumerable<OperationInfo> GetWaitingOperations()
    {
        return _operations.Values.Where(IsParked).ToList();
    }

    public OperationRunsSnapshot GetRuns()
    {
        // Read before listing: a run that is missing from the list is dropped by the browser only
        // when its own last revision is not newer than this one.
        var revision = Volatile.Read(ref _revision);
        var runs = new List<OperationRun>();
        foreach (var operation in _operations.Values)
        {
            lock (operation)
            {
                if (ToRun(operation) is { } run) runs.Add(run);
            }
        }

        return new OperationRunsSnapshot(runs, revision);
    }

    public void SetBlockedByName(Guid operationId, string? name)
    {
        if (!_operations.TryGetValue(operationId, out var operation)) return;
        lock (operation)
        {
            if (operation.Status.IsTerminal() || operation.BlockedByName == name) return;
            operation.BlockedByName = name;
            Publish(operation);
        }

        _ = DrainRunsAsync();
    }

    public void RefreshRun(Guid operationId)
    {
        if (!_operations.TryGetValue(operationId, out var operation)) return;
        lock (operation)
        {
            if (operation.Status.IsTerminal()) return;
            Publish(operation);
        }

        _ = DrainRunsAsync();
    }

    public void UpdateKeptEnding(Guid operationId, int consecutiveFailures, bool latestRunSucceeded)
    {
        if (!_operations.TryGetValue(operationId, out var operation)) return;
        lock (operation)
        {
            if (!KeepsUntilClosed(operation) || operation.Closed
                || (operation.ConsecutiveFailures == consecutiveFailures && operation.LatestRunSucceeded == latestRunSucceeded))
            {
                return;
            }

            operation.ConsecutiveFailures = consecutiveFailures;
            operation.LatestRunSucceeded = latestRunSucceeded;
            Publish(operation);
        }

        _ = DrainRunsAsync();
    }

    public bool CloseRun(Guid operationId)
    {
        if (!_operations.TryGetValue(operationId, out var operation)) return false;
        lock (operation)
        {
            if (!KeepsUntilClosed(operation) || operation.Closed) return false;
            operation.Closed = true;
            Publish(operation);
        }

        _ = DrainRunsAsync();
        ReapOperation(operationId);
        return true;
    }

    public IDisposable BeginPromotion(Guid waitingOperationId, OperationType type)
    {
        // Set here, in a synchronous call, so the value flows into the start delegate the caller
        // awaits next and is restored when the scope is disposed.
        var scope = new PromotionScope(_promotion, _promotion.Value);
        _promotion.Value = (waitingOperationId, type);
        return scope;
    }

    private sealed class PromotionScope(
        AsyncLocal<(Guid WaitingId, OperationType Type)?> promotion,
        (Guid WaitingId, OperationType Type)? previous) : IDisposable
    {
        public void Dispose() => promotion.Value = previous;
    }

    /// <summary>
    /// The waiting operation a new registration takes over from: the promotion running in this
    /// async flow for the same type, otherwise the still-waiting operation its run notice was
    /// attached to (a held or acknowledged schedule run).
    /// </summary>
    private Guid? FindPreviousOperationId(OperationType type, RunNotice? notice)
    {
        if (_promotion.Value is { } promotion && promotion.Type == type) return promotion.WaitingId;
        return notice?.OperationId is { } noticeId
            && _operations.TryGetValue(noticeId, out var previous)
            && previous.Status == OperationStatus.Waiting
                ? noticeId
                : null;
    }

    public void CompleteOperation(
        Guid operationId,
        bool success,
        string? error = null,
        bool cancelled = false,
        bool skipped = false,
        Action<OperationInfo>? onCompleting = null,
        Action? commit = null)
    {
        if (!_operations.TryGetValue(operationId, out var operation))
        {
            _logger.LogWarning("Attempted to complete non-existent operation {Id}", operationId);
            return;
        }

        // A waiting run that handed its work on lifts the run doing it to its own visibility when
        // that is more visible, and that row goes out before this run's terminal row, so the
        // browser merges the waiting card into an entry that is already a card. The two locks are
        // taken one after the other, never nested; an ended successor reads its frozen
        // visibility, so the floor changes nothing there.
        var successorId = ResolveHandoff(operationId);
        if (successorId != operationId && _operations.TryGetValue(successorId, out var successor))
        {
            RunVisibility handedOn;
            lock (operation) handedOn = ReadVisibility(operation);
            lock (successor)
            {
                var before = ReadVisibility(successor);
                if (handedOn < before)
                {
                    successor.VisibilityFloor = handedOn;
                    if (ReadVisibility(successor) != before) Publish(successor);
                }
            }
            _ = DrainRunsAsync();
        }

        CancellationTokenSource? cts;
        Func<OperationTerminalInfo, Task>? emit;
        Action? cleanup;
        OperationTerminalInfo terminal;
        Action<OperationInfo>? terminalSubscribers;
        Exception? publicationError = null;
        bool keep;
        lock (operation)
        {
            if (commit != null)
            {
                if (operation.CompletedFlag != 0 || operation.Status.IsTerminal()) return;
                if (!success || cancelled || skipped)
                    throw new InvalidOperationException("A checkpoint requires successful completed work");
                if (operation.Cancelled || operation.CancellationTokenSource?.IsCancellationRequested == true)
                    throw new OperationCanceledException("Operation cancelled before its checkpoint");
                commit();
            }
            if (Interlocked.CompareExchange(ref operation.CompletedFlag, 1, 0) != 0) return;

            // Frozen now so no later write (the terminal callback below replacing the metadata the
            // flags live in, a notice trigger raised without this lock, a handoff floor, the link
            // removed when the successor is reaped) changes how the ended run is drawn or whether it
            // is kept; every later KeepsUntilClosed call gives this answer. [55]
            operation.CompletedVisibility = ReadVisibility(operation);
            try { onCompleting?.Invoke(operation); }
            catch (Exception ex) { publicationError = ex; }

            if (cancelled) operation.Cancelled = true;

            // Skipped is successful but did no work. Cancellation keeps its own terminal outcome.
            operation.Status = success
                ? (skipped ? OperationStatus.Skipped : OperationStatus.Completed)
                : (operation.Cancelled ? OperationStatus.Cancelled : OperationStatus.Failed);
            operation.Message = success
                ? (skipped ? (error ?? ScheduledRunReporter.NothingToDoStageKey) : "Operation completed successfully")
                : (error ?? (operation.Cancelled ? "Operation cancelled" : "Operation failed"));
            operation.Success = success;
            operation.CompletedAt = DateTime.UtcNow;

            var nextId = ResolveHandoff(operationId);
            operation.NextOperationId = nextId != operationId ? nextId : null;
            keep = KeepsUntilClosed(operation);
            Publish(operation);

            // Detach resources and callbacks atomically; disposal and outward effects happen below.
            cts = operation.CancellationTokenSource;
            operation.CancellationTokenSource = null;
            operation.AssociatedProcess = null;
            emit = operation.OnTerminalEmit;
            operation.OnTerminalEmit = null;
            cleanup = operation.OnTerminalCleanup;
            operation.OnTerminalCleanup = null;
            terminal = new OperationTerminalInfo(success, operation.Cancelled, error, skipped);
            terminalSubscribers = OperationTerminal;
        }

        _ = DrainRunsAsync();
        // Live log ingest keeps one failure card. This failure's row is already queued, so closing
        // the older ones after it lets the browser replace the older card in the same update. [55]
        if (keep && operation.LiveIngest)
        {
            foreach (var other in _operations.Values)
            {
                bool older;
                lock (other) older = other.CompletedRevision < operation.CompletedRevision && other.LiveIngest;
                if (older) CloseRun(other.Id);
            }
        }
        if (publicationError != null)
        {
            _logger.LogWarning(publicationError, "Terminal publication threw for operation {Id}", operationId);
        }
        cts?.Dispose();
        if (emit != null)
        {
            _ = SafeEmitTerminalAsync(operationId, emit, terminal);
        }

        // Invoke the owning service's local-state reset BEFORE we log/remove. Best-effort: never throw.
        try { cleanup?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "OnTerminalCleanup threw for operation {Id}", operationId); }

        _logger.LogInformation("Completed operation {Id} ({Type}: {Name}), Status: {Status}",
            operationId, operation.Type, operation.Name, operation.Status);

        // Notify queue/listeners that an operation reached terminal state (exactly once via the
        // CompletedFlag gate above). Fire-and-forget off this stack; handler faults are contained.
        if (terminalSubscribers != null)
        {
            _ = Task.Run(() =>
            {
                try { terminalSubscribers(operation); }
                catch (Exception ex) { _logger.LogWarning(ex, "OperationTerminal handler threw for {Id}", operationId); }
            });
        }

        // A kept ending stays tracked, so the run list still returns it after a reload or
        // reconnect, until someone closes its card (CloseRun reaps it).
        if (!keep) ScheduleReaper(operationId); // core-2: delayed cleanup, see ScheduleReaper
    }

    /// <summary>
    /// Fire-and-forget wrapper around an operation's <see cref="OperationInfo.OnTerminalEmit"/>:
    /// awaits the emit Task off the CompleteOperation call stack and swallows/logs any exception so a
    /// faulty terminal-emit closure can never crash the tracker or leave the op un-reaped. Mirrors the
    /// best-effort handling of <see cref="OperationInfo.OnTerminalCleanup"/>.
    /// </summary>
    private async Task SafeEmitTerminalAsync(Guid operationId,
        Func<OperationTerminalInfo, Task> emit, OperationTerminalInfo info)
    {
        try
        {
            await emit(info).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OnTerminalEmit threw for operation {Id}", operationId);
        }
    }

    /// <summary>
    /// core-2: removes the operation (and its entity-index entries) after a short delay so final
    /// status queries can still observe the terminal state. The removal body is wrapped in try/catch
    /// and runs on <see cref="TaskScheduler.Default"/> so it cannot leak unobserved exceptions.
    /// </summary>
    private void ScheduleReaper(Guid operationId) =>
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_reaperTask =>
        {
            try
            {
                ReapOperation(operationId);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Reaper cleanup failed for {Id}", operationId); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private void ReapOperation(Guid operationId)
    {
        if (!_operations.TryGetValue(operationId, out var operation)) return;
        var links = _handoffs.ToArray();
        var stale = links.Where(link => ResolveHandoff(link.Key) == operationId).ToArray();
        lock (operation)
        {
            if (operation.CompletedFlag == 0 || !operation.Status.IsTerminal()) return;
            ((ICollection<KeyValuePair<Guid, OperationInfo>>)_operations).Remove(new(operationId, operation));
        }
        foreach (var entry in _entityKeyIndex.Where(entry => entry.Value == operationId).ToArray())
        {
            ((ICollection<KeyValuePair<(OperationType Type, string EntityKey), Guid>>)_entityKeyIndex).Remove(entry);
        }
        foreach (var link in stale)
        {
            ((ICollection<KeyValuePair<Guid, Guid>>)_handoffs).Remove(link);
        }
    }

    /// <summary>
    /// Stamps the next revision on the operation and queues its row. The caller holds
    /// <c>lock (operation)</c>, so the row and its revision describe one state, and runs
    /// <see cref="DrainRunsAsync"/> after releasing it. A revision is stamped only for a row that is
    /// sent, so the admin group sees revisions rise by exactly one in send order.
    /// </summary>
    private void Publish(OperationInfo operation)
    {
        if (HasNoRow(operation)) return;
        lock (_pendingRunsLock)
        {
            operation.Revision = ++_revision;
            if (operation.Status.IsTerminal() && operation.CompletedRevision == 0)
            {
                operation.CompletedRevision = operation.Revision;
            }

            if (_notifications != null) _pendingRuns.Enqueue(ToRun(operation)!);
        }
    }

    // Same shape as ServiceScheduleRegistry.BroadcastSchedulesAsync: whichever caller wins the gate
    // sends the whole queue in order, including rows other callers queued while it waited.
    private async Task DrainRunsAsync()
    {
        if (_notifications is null) return;
        await _publishGate.WaitAsync();
        try
        {
            while (true)
            {
                OperationRun? next;
                lock (_pendingRunsLock)
                {
                    next = _pendingRuns.Count > 0 ? _pendingRuns.Dequeue() : null;
                }
                if (next is null)
                {
                    break;
                }

                // NotifyAdminAsync already swallows its own failures, so this only guards a future
                // change that lets one through: it must not strand every row queued behind this one.
                try
                {
                    await _notifications.NotifyAdminAsync(SignalREvents.OperationUpdated, next);
                }
                catch
                {
                    // Non-fatal - move on to whatever else is queued rather than losing it too.
                }
            }
        }
        finally
        {
            _publishGate.Release();
        }
    }

    /// <summary>
    /// The browser's row for an operation, read under <c>lock (operation)</c>; null for an operation
    /// that is neither listed nor sent (see <see cref="HasNoRow"/>).
    /// </summary>
    private OperationRun? ToRun(OperationInfo operation)
    {
        if (HasNoRow(operation)) return null;
        var terminal = operation.Status.IsTerminal();
        Guid? nextId = operation.NextOperationId;
        if (!terminal)
        {
            var resolved = ResolveHandoff(operation.Id);
            nextId = resolved != operation.Id ? resolved : null;
        }

        var prefill = operation.Metadata as ScheduledPrefillServiceRunState;
        return new OperationRun(
            operation.Id,
            operation.Type.ToWireString(),
            operation.Name,
            operation.Status.ToWireString(),
            ReadVisibility(operation),
            operation.PercentComplete,
            operation.Message,
            operation.Status == OperationStatus.Failed ? operation.Message : null,
            operation.BlockedByName,
            operation.PreviousOperationId,
            operation.ParentOperationId,
            nextId,
            ReadWarning(operation.Metadata),
            KeepsUntilClosed(operation),
            operation.Closed,
            operation.ConsecutiveFailures,
            operation.LatestRunSucceeded,
            prefill?.ScheduleId,
            operation.Metadata switch
            {
                ScheduledPrefillServiceRunState run => run.ServiceId,
                // The prefill sign-in registers its platform as its metadata.
                PrefillPlatform platform => platform,
                _ => null
            },
            operation.LiveIngest,
            ReadIntegrationLogin(operation.Metadata) is not null,
            operation.OwnerSessionId,
            terminal ? operation.CompletedRevision : null,
            operation.StartedAt,
            operation.Revision);
    }

    /// <summary>
    /// The three operation types the notification bar never draws a card for, so none of their
    /// endings is kept until closed.
    /// </summary>
    private static readonly HashSet<OperationType> _operationTypesWithoutCard = new() { OperationType.StatusCheck, OperationType.CacheFileCount, OperationType.PerformanceOptimization };

    /// <summary>
    /// True for an ending the browser draws as a red or amber card that stays until someone closes
    /// it: a failure, a success with a warning, or a skip that showed a full card. A cancel and a
    /// plain success are not kept; the browser lets them leave on their own unless Keep
    /// Notifications Visible holds them. Phases (operations with a parent) are not kept, except a
    /// per-platform scheduled prefill run, which owns its card even when restored under its
    /// run-level container.
    /// </summary>
    internal static bool KeepsUntilClosed(OperationInfo operation) =>
        (operation.ParentOperationId == null || operation.Metadata is ScheduledPrefillServiceRunState)
        && !(operation.Metadata is ScheduledPrefillOperationMetadata)
        && !_operationTypesWithoutCard.Contains(operation.Type)
        && operation.Status switch
        {
            OperationStatus.Failed => true,
            OperationStatus.Completed => ReadWarning(operation.Metadata) != null,
            OperationStatus.Skipped => ReadVisibility(operation) == RunVisibility.Card,
            _ => false
        };

    /// <summary>
    /// True for an operation the browser gets no row for: the scheduled-prefill run-level container,
    /// whose platforms carry the cards.
    /// </summary>
    private static bool HasNoRow(OperationInfo operation) =>
        operation.Metadata is ScheduledPrefillOperationMetadata;

    /// <summary>
    /// The one place that turns an operation into a <see cref="RunVisibility"/>: the notice it was
    /// admitted with (none draws a full card), raised to <see cref="OperationInfo.VisibilityFloor"/>
    /// when that is more visible, or the value frozen at completion once it ended.
    /// </summary>
    private static RunVisibility ReadVisibility(OperationInfo operation)
    {
        if (operation.CompletedVisibility is { } frozen) return frozen;
        var own = operation.Notice is not { } notice ? RunVisibility.Card
            : notice.HideNotification ? RunVisibility.Hidden
            : notice.ShowNotification ? RunVisibility.Card
            : RunVisibility.Background;
        return own < operation.VisibilityFloor ? own : operation.VisibilityFloor;
    }

    // A run that succeeded with a warning: today the eviction scan whose game detection phase
    // failed, which writes the raw error into its progress context.
    private static string? ReadWarning(object? state) =>
        ReadContext(state)?.GetValueOrDefault("detectionError") is string { Length: > 0 } warning ? warning : null;

    // The reporter mirrors each run's latest interpolation context into the operation metadata under
    // "context" so a mid-run page refresh can rehydrate the card with its {{processed}}/{{total}}
    // values instead of rendering a bare stage key.
    internal static IReadOnlyDictionary<string, object?>? ReadContext(object? state)
    {
        var value = state switch
        {
            IReadOnlyDictionary<string, object?> readOnly when readOnly.TryGetValue("context", out var v) => v,
            IDictionary<string, object> mutable when mutable.TryGetValue("context", out var v) => v,
            _ => null,
        };

        return value as IReadOnlyDictionary<string, object?>;
    }

    // A mapping sign-in run carries its login under "integrationLogin"; such a run belongs to no
    // schedule, and only its signed-in caller may cancel it.
    internal static IntegrationLogin? ReadIntegrationLogin(object? state)
    {
        var value = state switch
        {
            IReadOnlyDictionary<string, object?> readOnly when readOnly.TryGetValue("integrationLogin", out var v) => v,
            IDictionary<string, object> mutable when mutable.TryGetValue("integrationLogin", out var v) => v,
            _ => null,
        };

        return value as IntegrationLogin;
    }

    public void UpdateProgress(Guid operationId, double percent, string message, Action<OperationInfo>? onProgress = null)
    {
        if (_operations.TryGetValue(operationId, out var operation))
        {
            lock (operation)
            {
                if (operation.CompletedFlag != 0 || operation.Status.IsTerminal()) return;
                operation.PercentComplete = Math.Clamp(percent, 0, 100);
                operation.Message = message;
                onProgress?.Invoke(operation);
            }
        }
        else
        {
            _logger.LogDebug("Attempted to update progress for non-existent operation {Id}", operationId);
        }
    }

    public OperationInfo? GetOperationByScope(OperationType type, ConflictScope scope)
    {
        if (_entityKeyIndex.TryGetValue((type, scope.ToTrackerKey()), out var operationId))
        {
            return GetOperation(operationId);
        }
        return null;
    }

    public void UpdateMetadata(Guid operationId, Action<object> updater)
    {
        if (_operations.TryGetValue(operationId, out var operation))
        {
            lock (operation)
            {
                if (operation.CompletedFlag == 0 && !operation.Status.IsTerminal() && operation.Metadata != null)
                {
                    updater(operation.Metadata);
                }
            }
        }
    }

    /// <summary>
    /// Build the kind-prefixed secondary index key for an operation's metadata.
    /// Returns <c>null</c> if the op has no indexable entity (bulk, global, unknown metadata).
    /// Handles both <see cref="RemovalMetrics"/> and <see cref="EvictionRemovalMetadata"/> -
    /// this is the Phase 3 change that lets per-entity <c>EvictionRemoval</c> populate the index.
    /// </summary>
    private static string? BuildIndexKey(OperationType type, object? metadata)
    {
        switch (metadata)
        {
            case RemovalMetrics m when !string.IsNullOrEmpty(m.EntityKey):
                {
                    var kind = string.IsNullOrEmpty(m.EntityKind)
                        ? type switch
                        {
                            OperationType.ServiceRemoval => "service",
                            OperationType.CorruptionRemoval => "service",
                            OperationType.GameRemoval => "steam",
                            _ => "bulk"
                        }
                        : m.EntityKind!;
                    return $"{kind}:{m.EntityKey}";
                }

            case EvictionRemovalMetadata e
                when !string.IsNullOrEmpty(e.Scope) && !string.IsNullOrEmpty(e.Key):
                return $"{e.Scope}:{e.Key}";

            default:
                return null;
        }
    }
}
