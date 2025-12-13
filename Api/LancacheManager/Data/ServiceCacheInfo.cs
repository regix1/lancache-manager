namespace LancacheManager.Data;

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
}
