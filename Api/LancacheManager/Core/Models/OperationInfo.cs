using System.Diagnostics;

namespace LancacheManager.Core.Models;

public class OperationInfo
{
    public required string Id { get; set; }
    public required OperationType Type { get; set; }
    public required string Name { get; set; }
    public string Status { get; set; } = OperationStatus.Pending;
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
}
