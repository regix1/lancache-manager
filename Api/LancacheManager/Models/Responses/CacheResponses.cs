namespace LancacheManager.Models;

/// <summary>
/// Response for cache operations (clear, removal)
/// </summary>
public class CacheOperationResponse
{
    public string Message { get; set; } = string.Empty;
    public Guid? OperationId { get; set; }
    public string? ServiceName { get; set; }
    public string? Service { get; set; }
    public OperationStatus Status { get; set; } = OperationStatus.Running;
}

/// <summary>Accepted response for a corruption detection operation.</summary>
public sealed class CorruptionDetectionStartResponse
{
    public Guid OperationId { get; set; }
    public string Message { get; set; } = string.Empty;
    public OperationStatus Status { get; set; } = OperationStatus.Running;
    public string DetectionMethod { get; set; } = string.Empty;
    public string? ScanMode { get; set; }
}

/// <summary>
/// Generic response wrapper for active operations with IsProcessing flag.
/// Use typed variants (ActiveGameRemovalsResponse, etc.) for strongly-typed Operations.
/// </summary>
public class ActiveOperationsResponse<T>
{
    public bool IsProcessing { get; set; }
    public IEnumerable<T>? Operations { get; set; }
}

/// <summary>
/// Response for active cache operations (untyped, for backward compatibility)
/// </summary>
public class ActiveOperationsResponse : ActiveOperationsResponse<object>
{
}

/// <summary>
/// Response for removal status check
/// </summary>
public class RemovalStatusResponse
{
    public bool IsProcessing { get; set; }
    public OperationStatus? Status { get; set; }
    public string? Message { get; set; }
    public Guid? OperationId { get; set; }
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public DateTime? StartedAt { get; set; }
    public string? Error { get; set; }
    public string? GameName { get; set; }
    public string? ServiceName { get; set; }
    public string? Service { get; set; }
    public string? DetectionMethod { get; set; }
}

/// <summary>Recovery response for an active corruption detection operation.</summary>
public sealed class CorruptionDetectionStatusResponse
{
    public bool IsRunning { get; set; }
    public Guid? OperationId { get; set; }
    public OperationStatus? Status { get; set; }
    public string? Message { get; set; }
    public string? StageKey { get; set; }
    public IReadOnlyDictionary<string, object?> Context { get; set; } = new Dictionary<string, object?>();
    public double? PercentComplete { get; set; }
    public string? StartTime { get; set; }
    public string? DetectionMethod { get; set; }
    public string? ScanMode { get; set; }
    public string? EffectiveScanMode { get; set; }
    public string? BaselineStatus { get; set; }
    public bool? Resumed { get; set; }
    public StructuralScanStatusResponse? ScanSummary { get; set; }
}

/// <summary>Durable structural state and work counters for active/terminal recovery.</summary>
public sealed class StructuralScanStatusResponse
{
    public string ScanMode { get; set; } = string.Empty;
    public string EffectiveScanMode { get; set; } = string.Empty;
    public string BaselineStatus { get; set; } = string.Empty;
    public bool Resumed { get; set; }
    public long FilesDiscovered { get; set; }
    public long FilesProcessed { get; set; }
    public long FilesReused { get; set; }
    public long FilesInspected { get; set; }
    public long FilesRevalidated { get; set; }
    public long InvalidFiles { get; set; }
    public long FilesPendingRetry { get; set; }
    public long FilesPruned { get; set; }
    public long StateEntries { get; set; }
    public bool StateCommitted { get; set; }
}

/// <summary>
/// Response for cache size calculation
/// </summary>
public class CacheSizeResponse
{
    public long TotalBytes { get; set; }
    public long TotalFiles { get; set; }
    public long TotalDirectories { get; set; }
    public int HexDirectories { get; set; }
    public long ScanDurationMs { get; set; }
    public EstimatedDeletionTimes EstimatedDeletionTimes { get; set; } = new();
    public string FormattedSize { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool IsCached { get; set; }
}

/// <summary>
/// Response for GET /api/cache/size when no result is available yet because a cache size
/// scan is actively running. The frontend should treat this as a waiting state (poll
/// GET /api/cache/size/scan/status or retry) instead of an error.
/// </summary>
public class CacheSizeScanningResponse
{
    public bool Scanning { get; set; } = true;
    public Guid? OperationId { get; set; }
}

/// <summary>
/// Response for an ordinary cache-size read when no scheduled or manual scan has produced a
/// persisted result yet. This is an expected empty state, not a calculation failure.
/// </summary>
public class CacheSizeUnavailableResponse
{
    public bool Available { get; set; } = false;
}

/// <summary>
/// Outcomes for a null cache-size-scan result (<c>CacheManagementService.GetCacheSizeAsync</c>):
/// an active scan wins (report scanning), else a previously persisted stale result if one
/// exists, else the expected unavailable state until the next scheduled or manual scan.
/// </summary>
public enum CacheSizeNullOutcomeKind
{
    Scanning,
    Stale,
    Unavailable
}

/// <summary>
/// Pure mapping from "GetCacheSizeAsync returned null" plus the caller's active-scan/stale-cache
/// lookups to the response the controller should send. Kept dependency-free so the decision is
/// unit-testable without constructing the controller or service.
/// </summary>
public class CacheSizeNullOutcome
{
    public CacheSizeNullOutcomeKind Kind { get; private init; }
    public Guid? ScanOperationId { get; private init; }
    public CacheSizeResponse? StaleResult { get; private init; }

    public static CacheSizeNullOutcome Resolve(Guid? activeScanOperationId, CacheSizeResponse? staleResult)
    {
        if (activeScanOperationId.HasValue)
        {
            return new CacheSizeNullOutcome { Kind = CacheSizeNullOutcomeKind.Scanning, ScanOperationId = activeScanOperationId };
        }

        if (staleResult != null)
        {
            return new CacheSizeNullOutcome { Kind = CacheSizeNullOutcomeKind.Stale, StaleResult = staleResult };
        }

        return new CacheSizeNullOutcome { Kind = CacheSizeNullOutcomeKind.Unavailable };
    }
}

/// <summary>
/// Estimated deletion times for different methods
/// </summary>
public class EstimatedDeletionTimes
{
    public double PreserveSeconds { get; set; }
    public double FullSeconds { get; set; }
    public double RsyncSeconds { get; set; }
    public string PreserveFormatted { get; set; } = string.Empty;
    public string FullFormatted { get; set; } = string.Empty;
    public string RsyncFormatted { get; set; } = string.Empty;
}

/// <summary>
/// Response for cache delete mode update
/// </summary>
public class CacheDeleteModeResponse
{
    public string Message { get; set; } = string.Empty;
    public CacheDeleteMode DeleteMode { get; set; } = CacheDeleteMode.Preserve;
}

/// <summary>
/// Response for active corruption removals
/// </summary>
public class ActiveCorruptionRemovalsResponse : ActiveOperationsResponse<CorruptionRemovalInfo>
{
}

public class CorruptionRemovalInfo
{
    public string Service { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public OperationStatus Status { get; set; }
    public string? Message { get; set; }
    public DateTime? StartedAt { get; set; }
    public string? DetectionMethod { get; set; }
}

/// <summary>
/// Response for active service removals
/// </summary>
public class ActiveServiceRemovalsResponse : ActiveOperationsResponse<ServiceRemovalInfo>
{
}

public class ServiceRemovalInfo
{
    public string ServiceName { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public OperationStatus Status { get; set; }
    public string? Message { get; set; }
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public DateTime? StartedAt { get; set; }
}

/// <summary>
/// Response for active game removals
/// </summary>
public class ActiveGameRemovalsResponse : ActiveOperationsResponse<GameRemovalInfo>
{
}

public class GameRemovalInfo
{
    public long? GameAppId { get; set; }
    public string? EpicAppId { get; set; }
    public string? EntityKind { get; set; }
    public string GameName { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public OperationStatus Status { get; set; }
    public string? Message { get; set; }
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public DateTime? StartedAt { get; set; }
}

/// <summary>
/// Response item for a single active eviction removal operation.
/// Scope/key/gameName are derived from EvictionRemovalMetadata stored at registration time.
/// </summary>
public class EvictionRemovalInfo
{
    /// <summary>"steam" | "epic" | "service" | null (bulk)</summary>
    public string? Scope { get; set; }

    /// <summary>Entity key within scope: steamAppId string, epicAppId, service name, or null for bulk.</summary>
    public string? Key { get; set; }

    /// <summary>Resolved display name for steam/epic scopes. Null for service/bulk.</summary>
    public string? GameName { get; set; }

    public Guid OperationId { get; set; }
    public OperationStatus Status { get; set; }
    public string? Message { get; set; }
    public DateTime? StartedAt { get; set; }
}

/// <summary>
/// Response for all active removals (games, services, corruption, eviction)
/// </summary>
public class AllActiveRemovalsResponse
{
    public bool IsProcessing { get; set; }
    public IEnumerable<GameRemovalInfo>? GameRemovals { get; set; }
    public IEnumerable<ServiceRemovalInfo>? ServiceRemovals { get; set; }
    public IEnumerable<CorruptionRemovalInfo>? CorruptionRemovals { get; set; }
    public IEnumerable<EvictionRemovalInfo>? EvictionRemovals { get; set; }
}

/// <summary>
/// Response for rsync availability check
/// </summary>
public class RsyncAvailableResponse
{
    public bool Available { get; set; }
}
