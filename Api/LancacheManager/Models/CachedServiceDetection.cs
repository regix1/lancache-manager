using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Database-persisted cache of service detection results
/// Stores results from service cache detection to survive backend restarts
/// </summary>
public class CachedServiceDetection
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string ServiceName { get; set; } = string.Empty;

    public int CacheFilesFound { get; set; }

    public ulong TotalSizeBytes { get; set; }

    /// <summary>
    /// JSON-serialized list of sample URLs
    /// </summary>
    public string SampleUrlsJson { get; set; } = "[]";

    /// <summary>
    /// JSON-serialized list of cache file paths
    /// </summary>
    public string CacheFilePathsJson { get; set; } = "[]";

    /// <summary>
    /// When this service was last detected in cache
    /// </summary>
    public DateTime LastDetectedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
