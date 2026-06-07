using System.Collections.Concurrent;
using System.Diagnostics;
using LancacheManager.Core.Interfaces;
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
    private readonly ILogger<UnifiedOperationTracker> _logger;

    public UnifiedOperationTracker(ILogger<UnifiedOperationTracker> logger)
    {
        _logger = logger;
    }

    public Guid RegisterOperation(OperationType type, string name, CancellationTokenSource cts, object? metadata = null)
    {
        var operationId = Guid.NewGuid();
        var operation = new OperationInfo
        {
            Id = operationId,
            Type = type,
            Name = name,
            Status = OperationStatus.Running,
            Message = $"Starting {name}...",
            StartedAt = DateTime.UtcNow,
            CancellationTokenSource = cts,
            Metadata = metadata
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

    public bool TryRestoreOperation(Guid operationId, OperationType type, string name, CancellationTokenSource cts, object? metadata = null)
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
            Metadata = metadata
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

        if (operation.CancellationTokenSource == null)
        {
            _logger.LogWarning("Operation {Id} has no CancellationTokenSource", operationId);
            return false;
        }

        if (operation.CancellationTokenSource.IsCancellationRequested)
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
        operation.CancellationTokenSource.Cancel();
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

        _logger.LogWarning(
            "Force killing operation {Id} ({Type}: {Name})",
            operationId, operation.Type, operation.Name);

        operation.Status = OperationStatus.Cancelling;
        operation.Cancelled = true;
        operation.Message = "Force killed by user";

        if (TryKillAssociatedProcess(operation, operationId))
        {
            operation.AssociatedProcess = null;
        }

        operation.CancellationTokenSource?.Cancel();
        return true;
    }

    private bool TryKillAssociatedProcess(OperationInfo operation, Guid operationId)
    {
        var process = operation.AssociatedProcess;
        if (process == null || process.HasExited)
        {
            return false;
        }

        try
        {
            _logger.LogWarning(
                "Terminating process {ProcessName} (PID: {Pid}) for operation {Id} ({Type}: {Name})",
                process.ProcessName, process.Id, operationId, operation.Type, operation.Name);
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill process PID {Pid} for operation {Id}", process.Id, operationId);
            return false;
        }
    }

    public OperationInfo? GetOperation(Guid operationId)
    {
        return _operations.TryGetValue(operationId, out var operation) ? operation : null;
    }

    public IEnumerable<OperationInfo> GetActiveOperations(OperationType? filterType = null)
    {
        var operations = _operations.Values.Where(op =>
            op.Status != OperationStatus.Completed && op.Status != OperationStatus.Failed);

        if (filterType.HasValue)
        {
            operations = operations.Where(op => op.Type == filterType.Value);
        }

        return operations.ToList();
    }

    public void CompleteOperation(Guid operationId, bool success, string? error = null)
    {
        if (_operations.TryGetValue(operationId, out var operation))
        {
            operation.Status = success ? OperationStatus.Completed : OperationStatus.Failed;
            operation.Message = success ? "Operation completed successfully" : (error ?? "Operation failed");
            operation.Success = success;
            operation.CompletedAt = DateTime.UtcNow;

            // Dispose the CancellationTokenSource
            operation.CancellationTokenSource?.Dispose();
            operation.CancellationTokenSource = null;

            // Clear the process reference
            operation.AssociatedProcess = null;

            _logger.LogInformation("Completed operation {Id} ({Type}: {Name}), Success: {Success}",
                operationId, operation.Type, operation.Name, success);

            // Clean up after a short delay to allow final status queries
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(task =>
            {
                _operations.TryRemove(operationId, out OperationInfo? _removed);

                // Also clean up entity key index
                var keysToRemove = _entityKeyIndex.Where(kvp => kvp.Value == operationId).Select(kvp => kvp.Key).ToList();
                foreach (var key in keysToRemove)
                {
                    _entityKeyIndex.TryRemove(key, out _);
                }
            });
        }
        else
        {
            _logger.LogWarning("Attempted to complete non-existent operation {Id}", operationId);
        }
    }

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
