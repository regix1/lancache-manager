using System.Diagnostics;

namespace LancacheManager.Models;

public class OperationInfo
{
    public required Guid Id { get; set; }
    public required OperationType Type { get; set; }
    public required string Name { get; set; }
    public OperationStatus Status { get; set; } = OperationStatus.Pending;
    public string Message { get; set; } = "";
    public double PercentComplete { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Indicates if the operation completed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Indicates if the operation was cancelled.
    /// </summary>
    public bool Cancelled { get; set; }

    /// <summary>
    /// CancellationTokenSource for cancelling the operation.
    /// Not serialized to JSON responses.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CancellationTokenSource? CancellationTokenSource { get; set; }

    /// <summary>
    /// Reference to an associated process for force kill capability.
    /// Not serialized to JSON responses.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Process? AssociatedProcess { get; set; }

    /// <summary>
    /// Indicates if the operation is currently being cancelled.
    /// </summary>
    public bool IsCancelling => CancellationTokenSource?.IsCancellationRequested ?? false;

    /// <summary>
    /// Additional metadata specific to the operation type.
    /// Not serialized to JSON responses.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public object? Metadata { get; set; }

    /// <summary>Invoked exactly once when the operation reaches a terminal state, so the owning
    /// service can reset its local mutable state (e.g. null _currentOperationId / _cts) regardless
    /// of which path completed the op (worker finally vs universal force-kill).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Action? OnTerminalCleanup { get; set; }

    /// <summary>Invoked EXACTLY ONCE inside <c>UnifiedOperationTracker.CompleteOperation</c>
    /// (CompletedFlag-gated), fire-and-forget, so the owning service emits its terminal SignalR
    /// event from a single place regardless of which path completed the op (worker success,
    /// worker OCE-catch, or universal force-kill). Receives a strongly-typed
    /// <see cref="OperationTerminalInfo"/>. Must not throw (exceptions are swallowed/logged like
    /// <see cref="OnTerminalCleanup"/>). When this is non-null the op is "migrated" — the legacy
    /// <c>OperationCancellationService.NotifyForceKillCompleteAsync</c> switch no-ops for it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<OperationTerminalInfo, Task>? OnTerminalEmit { get; set; }

    /// <summary>0 = not yet completed, 1 = completed. Guards CompleteOperation against double-fire.
    /// Use Interlocked.CompareExchange.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int CompletedFlag; // plain int field (NOT a property) so Interlocked can take a ref
}
