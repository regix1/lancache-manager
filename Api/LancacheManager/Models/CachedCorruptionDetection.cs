using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Database-persisted cache of corruption detection results
/// Stores results from corruption detection to survive backend restarts
/// </summary>
public class CachedCorruptionDetection
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Service name (e.g., "steam", "epic", etc.)
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// Number of corrupted chunks detected for this service
    /// </summary>
    public long CorruptedChunkCount { get; set; }

    /// <summary>
    /// When this corruption was last detected
    /// </summary>
    public DateTime LastDetectedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
