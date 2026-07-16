namespace LancacheManager.Models;

/// <summary>
/// SignalR event payload emitted when a cache file scan (Rust cache_size binary) starts.
/// <c>ShowNotification</c> is the run-stable display flag (stamped from the service's notification
/// mode + run trigger): the lifecycle event is always emitted so recovery/state stays accurate, and
/// the frontend gates whether the card is shown.
/// </summary>
public record CacheSizeScanStarted(string StageKey, Guid OperationId, Dictionary<string, object?>? Context = null, bool ShowNotification = true);

/// <summary>
/// SignalR event payload emitted while the cache file scan walks the cache directories
/// and runs the deletion-speed calibration tests.
/// </summary>
public record CacheSizeScanProgress(
    Guid OperationId,
    string Status,
    string StageKey,
    double PercentComplete,
    long DirectoriesScanned,
    long TotalDirectories,
    long TotalFiles,
    long TotalBytes,
    Dictionary<string, object?>? Context = null,
    bool ShowNotification = true);

/// <summary>
/// SignalR event payload emitted when a cache file scan operation completes.
/// Implements <see cref="IOperationComplete"/>; <c>Status</c>/<c>Cancelled</c> are derived (this scan
/// has no cancellation concept) via explicit interface implementation, so the wire shape is unchanged.
/// </summary>
public record CacheSizeScanComplete(
    bool Success,
    Guid OperationId,
    string StageKey,
    long TotalFiles,
    long TotalBytes,
    string? FormattedSize = null,
    string? Error = null,
    Dictionary<string, object?>? Context = null,
    bool ShowNotification = true) : IOperationComplete
{
    Guid? IOperationComplete.OperationId => OperationId;
    OperationStatus IOperationComplete.Status => Success ? OperationStatus.Completed : OperationStatus.Failed;
    bool IOperationComplete.Cancelled => false;
}
