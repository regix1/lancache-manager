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
    public string? OperationId { get; set; }
    public string? ServiceName { get; set; }
    public string? Service { get; set; }
    public string Status { get; set; } = "running";
}

/// <summary>
/// Response for active cache operations
/// </summary>
public class ActiveOperationsResponse
{
    public bool IsProcessing { get; set; }
    public IEnumerable<object>? Operations { get; set; }
}

/// <summary>
/// Response for removal status check
/// </summary>
public class RemovalStatusResponse
{
    public bool IsProcessing { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
    public string? OperationId { get; set; }
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
    public string OperationId { get; set; } = string.Empty;
    public string Status { get; set; } = "running";
}

/// <summary>
/// Response for cache delete mode update
/// </summary>
public class CacheDeleteModeResponse
{
    public string Message { get; set; } = string.Empty;
    public string DeleteMode { get; set; } = string.Empty;
}

/// <summary>
/// Response for service removal operation start
/// </summary>
public class ServiceRemovalStartResponse
{
    public string Message { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string Status { get; set; } = "running";
}

/// <summary>
/// Response for corruption removal operation start
/// </summary>
public class CorruptionRemovalStartResponse
{
    public string Message { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string Status { get; set; } = "running";
}

/// <summary>
/// Response for active corruption removals
/// </summary>
public class ActiveCorruptionRemovalsResponse
{
    public bool IsProcessing { get; set; }
    public IEnumerable<CorruptionRemovalInfo>? Operations { get; set; }
}

public class CorruptionRemovalInfo
{
    public string Service { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public DateTime? StartedAt { get; set; }
}


public class CacheClearingInfo
{
    public string DatasourceName { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public DateTime? StartedAt { get; set; }
    public int DirectoriesProcessed { get; set; }
    public int TotalDirectories { get; set; }
    public double PercentComplete { get; set; }
}

/// <summary>
/// Response for active service removals
/// </summary>
public class ActiveServiceRemovalsResponse
{
    public bool IsProcessing { get; set; }
    public IEnumerable<ServiceRemovalInfo>? Operations { get; set; }
}

public class ServiceRemovalInfo
{
    public string ServiceName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public DateTime? StartedAt { get; set; }
}

/// <summary>
/// Response for active game removals
/// </summary>
public class ActiveGameRemovalsResponse
{
    public bool IsProcessing { get; set; }
    public IEnumerable<GameRemovalInfo>? Operations { get; set; }
}

public class GameRemovalInfo
{
    public int GameAppId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public DateTime? StartedAt { get; set; }
}

/// <summary>
/// Response for all active removals (games, services, corruption)
/// </summary>
public class AllActiveRemovalsResponse
{
    public bool IsProcessing { get; set; }
    public IEnumerable<GameRemovalInfo>? GameRemovals { get; set; }
    public IEnumerable<ServiceRemovalInfo>? ServiceRemovals { get; set; }
    public IEnumerable<CorruptionRemovalInfo>? CorruptionRemovals { get; set; }
    public IEnumerable<CacheClearingInfo>? CacheClearings { get; set; }
}

/// <summary>
/// Response for rsync availability check
/// </summary>
public class RsyncAvailableResponse
{
    public bool Available { get; set; }
}
