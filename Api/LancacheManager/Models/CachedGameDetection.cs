using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Database-persisted cache of game detection results
/// Stores results from game cache detection to survive backend restarts
/// </summary>
public class CachedGameDetection
{
    [Key]
    public long Id { get; set; }

    public long GameAppId { get; set; }

    public string GameName { get; set; } = string.Empty;

    public int CacheFilesFound { get; set; }

    public ulong TotalSizeBytes { get; set; }

    /// <summary>
    /// JSON-serialized list of depot IDs
    /// </summary>
    public string DepotIdsJson { get; set; } = "[]";

    /// <summary>
    /// JSON-serialized list of sample URLs
    /// </summary>
    public string SampleUrlsJson { get; set; } = "[]";

    /// <summary>
    /// JSON-serialized list of cache file paths
    /// </summary>
    public string CacheFilePathsJson { get; set; } = "[]";

    /// <summary>
    /// JSON-serialized list of datasource names where this game was found
    /// </summary>
    public string DatasourcesJson { get; set; } = "[]";

    /// <summary>
    /// The service/platform this game belongs to (e.g. "steam", "epicgames").
    /// Null defaults to "steam" for backward compatibility.
    /// </summary>
    public string? Service { get; set; }

    /// <summary>
    /// The Epic Games store App ID (string). Only populated for Epic games.
    /// </summary>
    public string? EpicAppId { get; set; }

    /// <summary>
    /// When this game was last detected in cache
    /// </summary>
    public DateTime LastDetectedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Persisted. True when ALL associated downloads for this game are evicted.
    /// Games with no matching downloads are NOT considered evicted.
    /// Backfilled on migration: set to true where CacheFilesFound = 0.
    /// </summary>
    public bool IsEvicted { get; set; } = false;
}
