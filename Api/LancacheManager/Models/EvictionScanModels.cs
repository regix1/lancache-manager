namespace LancacheManager.Models;

/// <summary>
/// SignalR event payload emitted when an eviction scan operation starts.
/// </summary>
public record EvictionScanStarted(string Message, string OperationId);

/// <summary>
/// SignalR event payload emitted after each batch during an eviction scan.
/// </summary>
public record EvictionScanProgress(
    string OperationId,
    string Status,
    string Message,
    double PercentComplete,
    int Processed,
    int TotalEstimate,
    int Evicted,
    int UnEvicted);

/// <summary>
/// SignalR event payload emitted when an eviction scan operation completes.
/// </summary>
public record EvictionScanComplete(
    bool Success,
    string OperationId,
    string Message,
    int Processed,
    int Evicted,
    int UnEvicted,
    string? Error = null);
