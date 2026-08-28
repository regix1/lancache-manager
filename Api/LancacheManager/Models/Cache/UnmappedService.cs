namespace LancacheManager.Models;

/// <summary>
/// Per-service totals for cache files on disk that no detected game or service claims.
/// Grouped by the service named in each file's cache key during a full detection scan.
/// </summary>
public class UnmappedService
{
    [System.Text.Json.Serialization.JsonPropertyName("service")]
    public string Service { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("file_count")]
    public long FileCount { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("total_bytes")]
    public ulong TotalSizeBytes { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("sample_urls")]
    public List<string> SampleUrls { get; set; } = new List<string>();
}
