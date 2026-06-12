namespace LancacheManager.Models;

/// <summary>
/// SignalR event payload emitted when a cache file scan (Rust cache_size binary) starts.
/// </summary>
public record CacheSizeScanStarted(string StageKey, Guid OperationId, Dictionary<string, object?>? Context = null);

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
    Dictionary<string, object?>? Context = null);

/// <summary>
/// SignalR event payload emitted when a cache file scan operation completes.
/// </summary>
public record CacheSizeScanComplete(
    bool Success,
    Guid OperationId,
    string StageKey,
    long TotalFiles,
    long TotalBytes,
    string? FormattedSize = null,
    string? Error = null,
    Dictionary<string, object?>? Context = null);
