using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Standardized operation status values for SignalR events and operation tracking.
/// Serialized as camelCase strings on the wire (e.g. "pending", "running", "cancelling")
/// to preserve the legacy JSON contract with the frontend and persisted state files.
/// </summary>
[JsonConverter(typeof(OperationStatusJsonConverter))]
public enum OperationStatus
{
    Pending,
    Running,
    Cancelling,
    Completed,
    Failed,
    Cancelled,

    /// <summary>
    /// Registered with the tracker but parked in the operation wait-queue behind a
    /// conflicting operation (see <c>OperationQueueService</c>). Waiting ops are excluded
    /// from <c>GetActiveOperations</c> so they never block other operations themselves.
    /// </summary>
    Waiting
}

/// <summary>
/// Serializes <see cref="OperationStatus"/> as lowercase/camelCase strings
/// ("pending", "running", "cancelling", "completed", "failed", "cancelled")
/// and accepts any casing on deserialization to match the pre-existing wire contract.
/// </summary>
internal sealed class OperationStatusJsonConverter : JsonStringEnumConverter<OperationStatus>
{
    public OperationStatusJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Helpers for interop between <see cref="OperationStatus"/> and legacy string-based state
/// records (e.g. generic <c>OperationState</c> rows shared with log-processing entries).
/// </summary>
public static class OperationStatusExtensions
{
    /// <summary>
    /// Returns the canonical camelCase wire value ("pending", "running", "cancelling", ...).
    /// </summary>
    public static string ToWireString(this OperationStatus status) => status switch
    {
        OperationStatus.Pending => "pending",
        OperationStatus.Running => "running",
        OperationStatus.Cancelling => "cancelling",
        OperationStatus.Completed => "completed",
        OperationStatus.Failed => "failed",
        OperationStatus.Cancelled => "cancelled",
        OperationStatus.Waiting => "waiting",
        _ => status.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// True when the status is one of the three terminal states
    /// (<see cref="OperationStatus.Completed"/>, <see cref="OperationStatus.Failed"/>,
    /// <see cref="OperationStatus.Cancelled"/>). Centralizes the terminal or-list so callers
    /// (e.g. <c>UnifiedOperationTracker</c>) no longer inline it.
    /// </summary>
    public static bool IsTerminal(this OperationStatus status) =>
        status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled;
}
