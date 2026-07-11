using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Database-persisted cache of corruption detection results
/// Stores results from corruption detection to survive backend restarts
/// </summary>
public class CachedCorruptionDetection
{
    [Key]
    public long Id { get; set; }

    /// <summary>The authoritative completed scan that produced this evidence.</summary>
    public Guid ScanId { get; set; }

    public CachedCorruptionScan? Scan { get; set; }

    /// <summary>
    /// Service name (e.g., "steam", "epic", etc.)
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>The datasource whose log/cache roots own the candidate paths.</summary>
    [MaxLength(100)]
    public string DatasourceName { get; set; } = "default";

    /// <summary>
    /// Number of corrupted chunks detected for this service
    /// </summary>
    public long CorruptedChunkCount { get; set; }

    /// <summary>
    /// Versioned JSON array of immutable candidates using the canonical Rust
    /// snake_case contract.
    /// </summary>
    public string CandidatesJson { get; set; } = "[]";

    /// <summary>True when this row contains at least one removable candidate.</summary>
    public bool RemovalAllowed { get; set; }

    /// <summary>
    /// When this corruption was last detected
    /// </summary>
    public DateTime LastDetectedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
