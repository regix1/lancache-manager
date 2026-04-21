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
    /// Scope discriminator for game removals - "steam" or "epic".
    /// Null for non-game removal types (service, corruption).
    /// Mirrors the convention used by EvictionRemovalMetadata.Scope.
    /// </summary>
    public string? EntityKind { get; set; }

    /// <summary>
    /// Epic identifier (game name / slug) for Epic game removals.
    /// Null for Steam removals and non-game removal types.
    /// </summary>
    public string? EpicAppId { get; set; }

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
