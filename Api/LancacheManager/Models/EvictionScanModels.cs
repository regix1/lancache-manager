namespace LancacheManager.Models;

/// <summary>
/// SignalR event payload emitted when an eviction scan operation starts.
/// </summary>
public record EvictionScanStarted(string StageKey, Guid OperationId, Dictionary<string, object?>? Context = null);

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
    Dictionary<string, object?>? Context = null);

/// <summary>
/// SignalR event payload emitted when an eviction scan operation completes.
/// </summary>
public record EvictionScanComplete(
    bool Success,
    Guid OperationId,
    string StageKey,
    int Processed,
    int Evicted,
    int UnEvicted,
    string? Error = null,
    Dictionary<string, object?>? Context = null);

/// <summary>
/// SignalR event payload emitted when an eviction removal operation starts.
/// </summary>
public record EvictionRemovalStarted(string StageKey, Guid OperationId, Dictionary<string, object?>? Context = null, string? GameName = null, string? GameAppId = null, string? EpicAppId = null);

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
    Dictionary<string, object?>? Context = null);

/// <summary>
/// SignalR event payload emitted when an eviction removal operation completes.
/// </summary>
public record EvictionRemovalComplete(
    bool Success,
    Guid OperationId,
    string StageKey,
    int DownloadsRemoved,
    int LogEntriesRemoved,
    string? Error = null,
    bool Cancelled = false,
    Dictionary<string, object?>? Context = null);
