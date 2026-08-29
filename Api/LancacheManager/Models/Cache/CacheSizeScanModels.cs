using LancacheManager.Core.Interfaces;
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
/// A scan can be cancelled from its card, so <c>Cancelled</c> travels on the wire: without it the
/// frontend reads the run as a failure and paints the card red. <c>Status</c> is derived from it.
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
    bool ShowNotification = true,
    bool Cancelled = false) : IOperationComplete
{
    Guid? IOperationComplete.OperationId => OperationId;
    OperationStatus IOperationComplete.Status => Cancelled
        ? OperationStatus.Cancelled
        : (Success ? OperationStatus.Completed : OperationStatus.Failed);
}
