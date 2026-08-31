using LancacheManager.Core.Interfaces;
namespace LancacheManager.Models;

/// <summary>
/// SignalR event payload emitted when an eviction scan operation starts.
/// <c>ShowNotification</c> is the display flag: lifecycle events are ALWAYS emitted so recovery and
/// progress stay coherent, and the frontend gates whether the card is shown. A silent scan (schedule
/// notification mode) sends the same events with the flag false.
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
/// A scan can be cancelled from its card, so <c>Cancelled</c> travels on the wire: without it the
/// frontend reads the run as a failure and paints the card red. <c>Status</c> is derived from it.
/// </summary>
public record EvictionScanComplete(
    bool Success,
    Guid OperationId,
    // Nullable because a terminal that always carries its own sentence has nothing for a key to
    // say: the reader's message resolver prefers the sentence, so a key sent beside one can never
    // render, and one that can never render still costs a translated entry in every locale.
    string? StageKey,
    int Processed,
    int Evicted,
    int UnEvicted,
    int PrunedOrphans = 0,
    string? Error = null,
    Dictionary<string, object?>? Context = null,
    bool ShowNotification = true,
    bool Cancelled = false,
    bool Skipped = false) : IOperationComplete
{
    Guid? IOperationComplete.OperationId => OperationId;

    /// <summary>
    /// Declared public rather than as an explicit interface implementation so it is serialized:
    /// an explicit implementation is invisible to System.Text.Json, which left the frontend
    /// reading no status at all and falling back to the success wording.
    /// </summary>
    public OperationStatus Status => Cancelled
        ? OperationStatus.Cancelled
        : Skipped
            ? OperationStatus.Skipped
            : (Success ? OperationStatus.Completed : OperationStatus.Failed);
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
