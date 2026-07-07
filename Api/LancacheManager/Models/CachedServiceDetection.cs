using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Database-persisted cache of service detection results
/// Stores results from service cache detection to survive backend restarts
/// </summary>
/// <remarks>
/// Adding a column? GameCacheDetectionDataService.LoadServiceEntitiesAsync projects this entity
/// column-by-column for the paths-excluded load - list the new column there too, or it will
/// silently read as default on that path.
/// </remarks>
public class CachedServiceDetection
{
    [Key]
    public long Id { get; set; }

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
    /// JSON-serialized list of datasource names where this service was found
    /// </summary>
    public string DatasourcesJson { get; set; } = "[]";

    /// <summary>
    /// When this service was last detected in cache
    /// </summary>
    public DateTime LastDetectedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Persisted. True when the service's cache files have been evicted (first removal step completed).
    /// The DB row is retained until the user finalizes the removal (second step).
    /// </summary>
    public bool IsEvicted { get; set; } = false;
}
