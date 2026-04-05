namespace LancacheManager.Models;

/// <summary>
/// SignalR event payload emitted when an eviction scan operation starts.
/// </summary>
public record EvictionScanStarted(string? StageKey, string OperationId, Dictionary<string, object?>? Context = null);

/// <summary>
/// SignalR event payload emitted after each batch during an eviction scan.
/// </summary>
public record EvictionScanProgress(
    string OperationId,
    string Status,
    string? StageKey,
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
    string OperationId,
    string? StageKey,
    int Processed,
    int Evicted,
    int UnEvicted,
    string? Error = null,
    Dictionary<string, object?>? Context = null);

/// <summary>
/// SignalR event payload emitted when an eviction removal operation starts.
/// </summary>
public record EvictionRemovalStarted(string? StageKey, string OperationId, Dictionary<string, object?>? Context = null);

/// <summary>
/// SignalR event payload emitted during an eviction removal operation.
/// </summary>
public record EvictionRemovalProgress(
    string OperationId,
    string Status,
    string? StageKey,
    double PercentComplete,
    int DownloadsRemoved,
    int LogEntriesRemoved,
    Dictionary<string, object?>? Context = null);

/// <summary>
/// SignalR event payload emitted when an eviction removal operation completes.
/// </summary>
public record EvictionRemovalComplete(
    bool Success,
    string OperationId,
    string? StageKey,
    int DownloadsRemoved,
    int LogEntriesRemoved,
    string? Error = null,
    Dictionary<string, object?>? Context = null);
