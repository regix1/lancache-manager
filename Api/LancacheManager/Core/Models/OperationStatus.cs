namespace LancacheManager.Core.Models;

/// <summary>
/// Standardized operation status values for SignalR events.
/// These values are used across all notification events for consistency.
/// </summary>
public static class OperationStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Cancelling = "cancelling";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
