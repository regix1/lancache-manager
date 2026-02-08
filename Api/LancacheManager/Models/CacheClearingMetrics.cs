namespace LancacheManager.Models;

/// <summary>
/// Metadata for cache clearing operations stored in OperationInfo.Metadata.
/// Extends UnifiedOperationTracker with cache-clearing-specific metrics.
/// </summary>
public class CacheClearingMetrics
{
    /// <summary>
    /// Entity key for composite lookups - appId.ToString() for games, serviceName.ToLowerInvariant() for services.
    /// </summary>
    public string EntityKey { get; set; } = string.Empty;

    /// <summary>
    /// Name of the datasource being cleared.
    /// </summary>
    public string? DatasourceName { get; set; }

    /// <summary>
    /// Number of cache directories processed so far.
    /// </summary>
    public int DirectoriesProcessed { get; set; }

    /// <summary>
    /// Total number of cache directories to process.
    /// </summary>
    public int TotalDirectories { get; set; }

    /// <summary>
    /// Total bytes deleted so far.
    /// </summary>
    public long BytesDeleted { get; set; }

    /// <summary>
    /// Total files deleted so far.
    /// </summary>
    public long FilesDeleted { get; set; }
}
