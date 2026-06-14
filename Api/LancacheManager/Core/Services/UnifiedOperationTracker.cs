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
                                  OperationStatus initialStatus = OperationStatus.Running)
    {
        var operationId = Guid.NewGuid();
        var operation = new OperationInfo
        {
            Id = operationId,
            Type = type,
            Name = name,
            Status = initialStatus,
            Message = $"Starting {name}...",
            StartedAt = DateTime.UtcNow,
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
                                    Func<OperationTerminalInfo, Task>? onTerminalEmit = null)
    {
        var operation = new OperationInfo
        {
            Id = operationId,
            Type = type,
            Name = name,
            Status = OperationStatus.Running,
            Message = $"Starting {name}...",
            StartedAt = DateTime.UtcNow,
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

    public bool CancelOperation(Guid operationId)
    {
        if (!_operations.TryGetValue(operationId, out var operation))
        {
            _logger.LogWarning("Operation {Id} not found for cancellation", operationId);
            return false;
        }

        if (operation.Status.IsTerminal())
        {
            _logger.LogDebug("Operation {Id} already terminal ({Status}) — cancel is a no-op", operationId, operation.Status);
            return true;
        }

        // P2-C: snapshot the CTS into a local ONCE so a concurrent CompleteOperation cannot null/dispose
        // it out from under us between the null-check and the .Cancel() call.
        var cts = operation.CancellationTokenSource;

        if (cts == null)
        {
            _logger.LogDebug("Operation {Id} has no CancellationTokenSource — attempting process kill only", operationId);
            TryKillAssociatedProcess(operation, operationId);
            return true;
        }

        try
        {
            if (cts.IsCancellationRequested)
            {
                _logger.LogDebug("Cancellation already in progress for operation {Id} — re-attempting process kill", operationId);
                TryKillAssociatedProcess(operation, operationId);
                return true;
            }

            _logger.LogInformation(
                "Requesting aggressive cancellation for operation {Id} ({Type}: {Name})",
                operationId, operation.Type, operation.Name);

            operation.Status = OperationStatus.Cancelling;
            operation.Cancelled = true;
            operation.Message = "Cancellation requested...";

            TryKillAssociatedProcess(operation, operationId);
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // P2-C: the operation completed concurrently and CompleteOperation disposed the CTS.
            // The op is already terminal — cancellation is a benign no-op.
            _logger.LogDebug("Operation {Id} completed concurrently during cancel — CTS already disposed", operationId);
        }

        return true;
    }

    public void AssociateProcess(Guid operationId, Process process)
    {
        if (_operations.TryGetValue(operationId, out var operation))
        {
            operation.AssociatedProcess = process;
            _logger.LogDebug(
                "Associated process {ProcessName} (PID: {Pid}) with operation {Id}",
                process.ProcessName, process.Id, operationId);
        }
    }

    public void DisassociateProcess(Guid operationId, Process process)
    {
        if (_operations.TryGetValue(operationId, out var operation)
            && ReferenceEquals(operation.AssociatedProcess, process))
        {
            operation.AssociatedProcess = null;
        }
    }

    public bool ForceKillOperation(Guid operationId)
    {
        if (!_operations.TryGetValue(operationId, out var operation))
        {
            _logger.LogWarning("Operation {Id} not found for force kill", operationId);
            return false;
        }

        if (operation.Status.IsTerminal())
        {
            _logger.LogDebug("Operation {Id} already terminal ({Status}) — force kill is a no-op", operationId, operation.Status);
            return true;
        }

        _logger.LogWarning(
            "Force killing operation {Id} ({Type}: {Name})",
            operationId, operation.Type, operation.Name);

        operation.Status = OperationStatus.Cancelling;
        operation.Cancelled = true;
        operation.Message = "Force killed by user";

        TryKillAssociatedProcess(operation, operationId);

        // P2-C: snapshot the CTS into a local ONCE and guard against a concurrent CompleteOperation
        // having already disposed it.
        var cts = operation.CancellationTokenSource;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("Operation {Id} completed concurrently during force kill — CTS already disposed", operationId);
        }

        return true;
    }

    private bool TryKillAssociatedProcess(OperationInfo operation, Guid operationId)
    {
        var process = operation.AssociatedProcess;
        if (process == null)
        {
            return false;
        }

        if (process.HasExited)
        {
            operation.AssociatedProcess = null;
            return false;
        }

        var killed = _processManager.KillProcessTree(
            process,
            $"operation {operationId} ({operation.Type}: {operation.Name})");
        if (!killed)
        {
            operation.AssociatedProcess = null;
        }

        return killed;
    }

    public OperationInfo? GetOperation(Guid operationId)
    {
        return _operations.TryGetValue(operationId, out var operation) ? operation : null;
    }

    public IEnumerable<OperationInfo> GetActiveOperations(OperationType? filterType = null)
    {
        // Waiting ops are queued, not running: they must not block conflict checks and must
        // stay invisible to the per-type status/recovery endpoints (see IUnifiedOperationTracker).
        var operations = _operations.Values.Where(op =>
            !op.Status.IsTerminal() && op.Status != OperationStatus.Waiting);

        if (filterType.HasValue)
        {
            operations = operations.Where(op => op.Type == filterType.Value);
        }

        return operations.ToList();
    }

    public IEnumerable<OperationInfo> GetWaitingOperations()
    {
        return _operations.Values.Where(op => op.Status == OperationStatus.Waiting).ToList();
    }

    public void CompleteOperation(Guid operationId, bool success, string? error = null)
    {
        if (!_operations.TryGetValue(operationId, out var operation))
        {
            _logger.LogWarning("Attempted to complete non-existent operation {Id}", operationId);
            return;
        }

        // who-completes-wins: only the first caller proceeds. Second caller is a benign no-op.
        // This makes the terminal transition + SignalR-completion-trigger + cleanup callback fire
        // at most once even when the worker finally and the universal force-kill race.
        if (Interlocked.CompareExchange(ref operation.CompletedFlag, 1, 0) != 0)
        {
            _logger.LogDebug("Operation {Id} already completed — ignoring duplicate complete", operationId);
            return;
        }

        operation.Status = success
            ? OperationStatus.Completed
            : (operation.Cancelled ? OperationStatus.Cancelled : OperationStatus.Failed); // C.1: Cancelled is terminal
        operation.Message = success ? "Operation completed successfully" : (error ?? "Operation failed");
        operation.Success = success;
        operation.CompletedAt = DateTime.UtcNow;

        // Tracker is the single disposer of the CTS it adopted (core-3 / core-7 ownership).
        operation.CancellationTokenSource?.Dispose();
        operation.CancellationTokenSource = null;

        // Clear the process reference
        operation.AssociatedProcess = null;

        // Fire the owning service's terminal SignalR emit EXACTLY ONCE (gated by CompletedFlag above),
        // fire-and-forget — CompleteOperation stays synchronous and never awaits the emit. This is the
        // SINGLE place a migrated op's terminal event is produced (worker success, OCE-catch, and
        // universal force-kill all funnel here), eliminating ordering/double-emit divergence.
        // Capture+null first so it can never re-run, and run it BEFORE OnTerminalCleanup (cleanup may
        // null service state the closure captured by value, so emit-first is safe).
        var emit = operation.OnTerminalEmit;
        operation.OnTerminalEmit = null;
        if (emit != null)
        {
            _ = SafeEmitTerminalAsync(operationId, emit,
                new OperationTerminalInfo(success, operation.Cancelled, error));
        }

        // Invoke the owning service's local-state reset BEFORE we log/remove. Best-effort: never throw.
        try { operation.OnTerminalCleanup?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "OnTerminalCleanup threw for operation {Id}", operationId); }
        finally { operation.OnTerminalCleanup = null; } // drop the delegate so it cannot re-run

        _logger.LogInformation("Completed operation {Id} ({Type}: {Name}), Success: {Success}",
            operationId, operation.Type, operation.Name, success);

        // Notify queue/listeners that an operation reached terminal state (exactly once via the
        // CompletedFlag gate above). Fire-and-forget off this stack; handler faults are contained.
        var terminalSubscribers = OperationTerminal;
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
                _operations.TryRemove(operationId, out _);
                foreach (var key in _entityKeyIndex.Where(kvp => kvp.Value == operationId).Select(kvp => kvp.Key).ToList())
                {
                    _entityKeyIndex.TryRemove(key, out _);
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Reaper cleanup failed for {Id}", operationId); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    public void UpdateProgress(Guid operationId, double percent, string message)
    {
        if (_operations.TryGetValue(operationId, out var operation))
        {
            operation.PercentComplete = Math.Clamp(percent, 0, 100);
            operation.Message = message;
        }
        else
        {
            _logger.LogDebug("Attempted to update progress for non-existent operation {Id}", operationId);
        }
    }

    public OperationInfo? GetOperationByEntityKey(OperationType type, string entityKey)
    {
        // Phase 3: the secondary index is now prefixed with kind ("steam:480", "service:steam", ...).
        // Preserve the raw-key signature used by existing callers by probing the common prefixes
        // in priority order. This mirrors the canonicalization rules in ConflictScope.
        // Priority matches historical usage: steam appIds > epic app ids > service names.
        string[] candidateKeys = type switch
        {
            OperationType.GameRemoval => new[] { $"steam:{entityKey}", $"epic:{entityKey}" },
            OperationType.ServiceRemoval => new[] { $"service:{entityKey}" },
            OperationType.CorruptionRemoval => new[] { $"service:{entityKey}" },
            OperationType.EvictionRemoval => new[] { $"steam:{entityKey}", $"epic:{entityKey}", $"service:{entityKey}" },
            _ => new[] { $"steam:{entityKey}", $"epic:{entityKey}", $"service:{entityKey}" }
        };

        foreach (var candidate in candidateKeys)
        {
            if (_entityKeyIndex.TryGetValue((type, candidate), out var operationId))
            {
                return GetOperation(operationId);
            }
        }

        return null;
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
        if (_operations.TryGetValue(operationId, out var operation) && operation.Metadata != null)
        {
            updater(operation.Metadata);
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
