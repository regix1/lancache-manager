namespace LancacheManager.Models;

/// <summary>
/// Metadata for removal operations (game, service, corruption) stored in OperationInfo.Metadata.
/// Extends UnifiedOperationTracker with removal-specific metrics.
/// </summary>
public class RemovalMetrics
{
    /// <summary>
    /// Entity key for composite lookups - appId.ToString() for games, serviceName.ToLowerInvariant() for services/corruption.
    /// </summary>
    public string EntityKey { get; set; } = string.Empty;

    /// <summary>
    /// Display name - game name or service name.
    /// </summary>
    public string? EntityName { get; set; }

    /// <summary>
    /// Number of cache files deleted so far.
    /// </summary>
    public int FilesDeleted { get; set; }

    /// <summary>
    /// Total bytes freed by deletion.
    /// </summary>
    public long BytesFreed { get; set; }

    /// <summary>
    /// Files processed so far (used by corruption removal).
    /// </summary>
    public int FilesProcessed { get; set; }

    /// <summary>
    /// Total files to process (used by corruption removal).
    /// </summary>
    public int TotalFiles { get; set; }
}
