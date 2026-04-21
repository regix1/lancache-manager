namespace LancacheManager.Models;

/// <summary>
/// Response for cache information
/// </summary>
public class CacheInfoResponse
{
    public string Path { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public bool Writable { get; set; }
    public long? TotalBytes { get; set; }
}

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
/// Response for cache clear operation start
/// </summary>
public class CacheClearStartResponse
{
    public string Message { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public OperationStatus Status { get; set; } = OperationStatus.Running;
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
/// Response for service removal operation start
/// </summary>
public class ServiceRemovalStartResponse
{
    public string Message { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public OperationStatus Status { get; set; } = OperationStatus.Running;
}

/// <summary>
/// Response for corruption removal operation start
/// </summary>
public class CorruptionRemovalStartResponse
{
    public string Message { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public OperationStatus Status { get; set; } = OperationStatus.Running;
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
