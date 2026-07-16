namespace LancacheManager.Models;

/// <summary>
/// SignalR event payload emitted when an eviction scan operation starts.
/// <c>ShowNotification</c> is the display flag: lifecycle events are ALWAYS emitted so recovery and
/// progress stay coherent, and the frontend gates whether the card is shown. A silent scan (mode
/// or Remove-mode scan phase) sends the same events with the flag false.
/// </summary>
public record EvictionScanStarted(string StageKey, Guid OperationId, Dictionary<string, object?>? Context = null, bool ShowNotification = true);

/// <summary>
/// SignalR event payload emitted after each batch during an eviction scan.
/// </summary>
public record EvictionScanProgress(
    Guid OperationId,
    string Status,
    string StageKey,
    double PercentComplete,
    int Processed,
    int TotalEstimate,
    int Evicted,
    int UnEvicted,
    Dictionary<string, object?>? Context = null,
    bool ShowNotification = true);

/// <summary>
/// SignalR event payload emitted when an eviction scan operation completes.
/// Implements <see cref="IOperationComplete"/>; <c>Status</c>/<c>Cancelled</c> are derived (a scan has
/// no cancellation concept) via explicit interface implementation, so the wire shape is unchanged.
/// </summary>
public record EvictionScanComplete(
    bool Success,
    Guid OperationId,
    string StageKey,
    int Processed,
    int Evicted,
    int UnEvicted,
    int PrunedOrphans = 0,
    string? Error = null,
    Dictionary<string, object?>? Context = null,
    bool ShowNotification = true) : IOperationComplete
{
    Guid? IOperationComplete.OperationId => OperationId;
    OperationStatus IOperationComplete.Status => Success ? OperationStatus.Completed : OperationStatus.Failed;
    bool IOperationComplete.Cancelled => false;
}

/// <summary>
/// SignalR event payload emitted when an eviction removal operation starts.
/// </summary>
public record EvictionRemovalStarted(string StageKey, Guid OperationId, Dictionary<string, object?>? Context = null, string? GameName = null, string? GameAppId = null, string? EpicAppId = null, bool ShowNotification = true);

/// <summary>
/// SignalR event payload emitted during an eviction removal operation.
/// </summary>
public record EvictionRemovalProgress(
    Guid OperationId,
    string Status,
    string StageKey,
    double PercentComplete,
    int DownloadsRemoved,
    int LogEntriesRemoved,
    Dictionary<string, object?>? Context = null,
    bool ShowNotification = true);

/// <summary>
/// SignalR event payload emitted when an eviction removal operation completes.
/// Implements <see cref="IOperationComplete"/>; <c>Status</c> is derived from
/// <c>Success</c>/<c>Cancelled</c> via explicit interface implementation, so the wire shape is unchanged.
/// </summary>
public record EvictionRemovalComplete(
    bool Success,
    Guid OperationId,
    string StageKey,
    int DownloadsRemoved,
    int LogEntriesRemoved,
    string? Error = null,
    bool Cancelled = false,
    Dictionary<string, object?>? Context = null,
    bool ShowNotification = true) : IOperationComplete
{
    Guid? IOperationComplete.OperationId => OperationId;
    OperationStatus IOperationComplete.Status =>
        Cancelled ? OperationStatus.Cancelled : Success ? OperationStatus.Completed : OperationStatus.Failed;
}
