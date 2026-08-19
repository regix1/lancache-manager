namespace LancacheManager.Models;

public class ServiceCacheInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("service_name")]
    public string ServiceName { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("cache_files_found")]
    public int CacheFilesFound { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("total_size_bytes")]
    public ulong TotalSizeBytes { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("sample_urls")]
    public List<string> SampleUrls { get; set; } = new List<string>();

    [System.Text.Json.Serialization.JsonPropertyName("cache_file_paths")]
    public List<string> CacheFilePaths { get; set; } = new List<string>();

    /// <summary>
    /// List of datasource names where this service's cache files were found.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("datasources")]
    public List<string> Datasources { get; set; } = new List<string>();

    [System.Text.Json.Serialization.JsonPropertyName("is_evicted")]
    public bool IsEvicted { get; set; } = false;

    /// <summary>
    /// Number of Downloads rows with IsEvicted = true for this service.
    /// Non-zero even when the service still has active cache files (partial eviction).
    /// Populated in GetCachedDetectionAsync via GROUP BY query.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("evicted_downloads_count")]
    public int EvictedDownloadsCount { get; set; } = 0;

    /// <summary>
    /// Sum of (CacheHitBytes + CacheMissBytes) for all evicted Downloads rows for this service.
    /// Represents the data volume of the evicted portion.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("evicted_bytes")]
    public ulong EvictedBytes { get; set; } = 0;

    /// <summary>
    /// Sample URLs from LogEntries associated with evicted Downloads for this service.
    /// Populated in GetCachedDetectionAsync. Empty when no evicted downloads exist.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("evicted_sample_urls")]
    public List<string> EvictedSampleUrls { get; set; } = new List<string>();
}
