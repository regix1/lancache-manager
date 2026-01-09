namespace LancacheManager.Models;

public class GameCacheInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("game_app_id")]
    public uint GameAppId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("game_name")]
    public string GameName { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("cache_files_found")]
    public int CacheFilesFound { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("total_size_bytes")]
    public ulong TotalSizeBytes { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("depot_ids")]
    public List<uint> DepotIds { get; set; } = new List<uint>();

    [System.Text.Json.Serialization.JsonPropertyName("sample_urls")]
    public List<string> SampleUrls { get; set; } = new List<string>();

    [System.Text.Json.Serialization.JsonPropertyName("cache_file_paths")]
    public List<string> CacheFilePaths { get; set; } = new List<string>();

    /// <summary>
    /// List of datasource names where this game's cache files were found.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("datasources")]
    public List<string> Datasources { get; set; } = new List<string>();
}
