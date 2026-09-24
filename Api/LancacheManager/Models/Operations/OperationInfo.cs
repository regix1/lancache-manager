using System.Diagnostics;

namespace LancacheManager.Models;

public class OperationInfo
{
    public required Guid Id { get; set; }
    public Guid? ParentOperationId { get; set; }
    public required OperationType Type { get; set; }
    public required string Name { get; set; }
    public OperationStatus Status { get; set; } = OperationStatus.Pending;
    public string Message { get; set; } = "";
    public double PercentComplete { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    /// <summary>The waiting operation this run took over from, when it started from one.</summary>
    public Guid? PreviousOperationId { get; set; }

    /// <summary>Display name of the operation a waiting run is parked behind; null when unknown.</summary>
    public string? BlockedByName { get; set; }

    /// <summary>The notice the run was admitted with, which decides how it is drawn; null draws a full card.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public RunNotice? Notice { get; init; }

    /// <summary>True for a live log ingest pass, which has no row until it ends with a kept failure.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LiveIngest { get; init; }

    /// <summary>The auth session that started the run, when only that session's browser draws it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? OwnerSessionId { get; init; }

    /// <summary>Revision of the last row sent for this operation.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long Revision { get; set; }

    /// <summary>Revision of the operation's first terminal row; 0 while it is live.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long CompletedRevision { get; set; }

    /// <summary>True once a worker runs this operation, so a cancel no longer finishes it as parked.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool WorkerStarted { get; set; }

    /// <summary>The least visible this run may be drawn, raised when a more visible waiting run hands
    /// it its work.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public RunVisibility VisibilityFloor { get; set; } = RunVisibility.Hidden;

    /// <summary>The visibility frozen at completion, so a later notice change cannot redraw an ended run.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public RunVisibility? CompletedVisibility { get; set; }

    /// <summary>The operation doing this run's work at completion, frozen then because the handoff link
    /// is removed once that operation is reaped.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? NextOperationId { get; set; }

    /// <summary>True once someone closed this kept ending; it is reaped right after.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Closed { get; set; }

    /// <summary>Failures in a row for the schedule this kept ending belongs to; written by the schedule registry.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ConsecutiveFailures { get; set; }

    /// <summary>True when a later run of the same schedule succeeded after this kept ending.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LatestRunSucceeded { get; set; }

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
    /// <see cref="OnTerminalCleanup"/>). This is the sole source of an operation's terminal SignalR
    /// event on every completion path (worker success, worker OCE-catch, and universal force-kill).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<OperationTerminalInfo, Task>? OnTerminalEmit { get; set; }

    /// <summary>0 = not yet completed, 1 = completed. Guards CompleteOperation against double-fire.
    /// Use Interlocked.CompareExchange.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int CompletedFlag; // plain int field (NOT a property) so Interlocked can take a ref
}
