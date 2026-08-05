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
    Waiting,

    /// <summary>
    /// The run started, found there was nothing for it to do, and stopped. Terminal exactly like
    /// <see cref="Completed"/>: it fires the tracker's terminal hook, so the wait-queue promotes
    /// behind it, and it is excluded from <c>GetActiveOperations</c> so it stops blocking. It is
    /// NOT a failure and carries no error. A signed-out Epic or Xbox catalog run is the case this
    /// exists for: it used to report "completed" after reading nothing at all.
    /// </summary>
    Skipped
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
        OperationStatus.Skipped => "skipped",
        _ => status.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// True when the status is one of the four terminal states
    /// (<see cref="OperationStatus.Completed"/>, <see cref="OperationStatus.Failed"/>,
    /// <see cref="OperationStatus.Cancelled"/>, <see cref="OperationStatus.Skipped"/>).
    /// Centralizes the terminal or-list so callers (e.g. <c>UnifiedOperationTracker</c>) no
    /// longer inline it.
    ///
    /// Every consumer of this helper depends on it staying complete. <c>GetActiveOperations</c>
    /// filters on it, and <c>OperationConflictChecker</c> asks <c>GetActiveOperations</c> whether
    /// anything conflicts, so a terminal status missing from this list would leave a finished
    /// operation blocking the wait-queue forever.
    /// </summary>
    public static bool IsTerminal(this OperationStatus status) =>
        status is OperationStatus.Completed
            or OperationStatus.Failed
            or OperationStatus.Cancelled
            or OperationStatus.Skipped;
}
