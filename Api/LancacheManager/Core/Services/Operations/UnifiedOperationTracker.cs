using System.Collections.Concurrent;
using System.Diagnostics;
using LancacheManager.Core.Interfaces;
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

    public UnifiedOperationTracker(ProcessManager processManager, ILogger<UnifiedOperationTracker> logger)
    {
        _processManager = processManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public event Action<OperationInfo>? OperationTerminal;

    public Guid RegisterOperation(OperationType type, string name, CancellationTokenSource cts,
                                  object? metadata = null, Action? onTerminalCleanup = null,
                                  Func<OperationTerminalInfo, Task>? onTerminalEmit = null,
                                  OperationStatus initialStatus = OperationStatus.Running,
                                  Guid? parentOperationId = null, DateTime? startedAt = null)
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
            OnTerminalEmit = onTerminalEmit
        };

        if (_operations.TryAdd(operationId, operation))
        {
            var indexKey = BuildIndexKey(type, metadata);
            if (indexKey != null)
            {
                _entityKeyIndex[(type, indexKey)] = operationId;
            }

            _logger.LogInformation("Registered {Type} operation: {Name} (ID: {Id})", type, name, operationId);
            return operationId;
        }

        _logger.LogError("Failed to register operation: {Name} (ID: {Id})", name, operationId);
        throw new InvalidOperationException($"Failed to register operation {operationId}");
    }

    public bool TryRestoreOperation(Guid operationId, OperationType type, string name, CancellationTokenSource cts,
                                    object? metadata = null, Action? onTerminalCleanup = null,
                                    Func<OperationTerminalInfo, Task>? onTerminalEmit = null,
                                    Guid? parentOperationId = null, DateTime? startedAt = null)
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
            OnTerminalEmit = onTerminalEmit
        };

        if (_operations.TryAdd(operationId, operation))
        {
            var indexKey = BuildIndexKey(type, metadata);
            if (indexKey != null)
            {
                _entityKeyIndex[(type, indexKey)] = operationId;
            }

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

    public OperationCancelResult CancelOperation(Guid requestedOperationId)
    {
        // The caller's card may still carry the id of an operation that has already handed its work
        // to another one (the wait-queue promoting a parked operation is the case that matters).
        // Cancelling the id as given would stop nothing while reporting success.
        var operationId = ResolveHandoff(requestedOperationId);
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
        }

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

    public bool ForceKillOperation(Guid requestedOperationId)
    {
        // Same reasoning as CancelOperation: follow the work, not the id the caller happens to hold.
        var operationId = ResolveHandoff(requestedOperationId);
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
        }

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
        var operations = _operations.Values.Where(op =>
            !op.Status.IsTerminal() && op.Status != OperationStatus.Waiting &&
            !(op.Status == OperationStatus.Cancelling &&
              (RunNotice.ReadRunNotice(op.Metadata)?.PendingId == op.Id ||
               op.Metadata is IReadOnlyDictionary<string, object?> values && values.TryGetValue("waiting", out var waiting) && waiting is true)));

        if (filterType.HasValue)
        {
            operations = operations.Where(op => op.Type == filterType.Value);
        }

        return operations.ToList();
    }

    public IEnumerable<OperationInfo> GetWaitingOperations()
    {
        return _operations.Values.Where(op => op.Status == OperationStatus.Waiting ||
            op.Status == OperationStatus.Cancelling &&
            (RunNotice.ReadRunNotice(op.Metadata)?.PendingId == op.Id ||
             op.Metadata is IReadOnlyDictionary<string, object?> values && values.TryGetValue("waiting", out var waiting) && waiting is true)).ToList();
    }

    public void CompleteOperation(
        Guid operationId,
        bool success,
        string? error = null,
        bool cancelled = false,
        bool skipped = false,
        Action<OperationInfo>? onCompleting = null)
    {
        if (!_operations.TryGetValue(operationId, out var operation))
        {
            _logger.LogWarning("Attempted to complete non-existent operation {Id}", operationId);
            return;
        }

        CancellationTokenSource? cts;
        Func<OperationTerminalInfo, Task>? emit;
        Action? cleanup;
        OperationTerminalInfo terminal;
        Action<OperationInfo>? terminalSubscribers;
        Exception? publicationError = null;
        lock (operation)
        {
            if (Interlocked.CompareExchange(ref operation.CompletedFlag, 1, 0) != 0) return;

            try { onCompleting?.Invoke(operation); }
            catch (Exception ex) { publicationError = ex; }

            if (cancelled) operation.Cancelled = true;

            // Skipped is successful but did no work. Cancellation keeps its own terminal outcome.
            operation.Status = success
                ? (skipped ? OperationStatus.Skipped : OperationStatus.Completed)
                : (operation.Cancelled ? OperationStatus.Cancelled : OperationStatus.Failed);
            operation.Message = success
                ? (skipped ? (error ?? "Operation skipped - nothing to do") : "Operation completed successfully")
                : (error ?? (operation.Cancelled ? "Operation cancelled" : "Operation failed"));
            operation.Success = success;
            operation.CompletedAt = DateTime.UtcNow;

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

        ScheduleReaper(operationId); // core-2: delayed cleanup, see ScheduleReaper
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
