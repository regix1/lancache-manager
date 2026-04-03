namespace LancacheManager.Models;

public class GameCacheInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("game_app_id")]
    public long GameAppId { get; set; }

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

    /// <summary>
    /// The service/platform this game belongs to (e.g. "steam", "epicgames").
    /// Defaults to "steam" for backward compatibility with Rust-detected games.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("service")]
    public string? Service { get; set; }

    /// <summary>
    /// Banner URL from Downloads.GameImageUrl (and EpicGameMappings when needed), set when serving cached detection.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    /// <summary>
    /// The Epic Games store App ID, used by the frontend GameImage component to proxy
    /// image requests through /api/game-images/epic/{epicAppId}/header.
    /// Only populated for Epic games (Service == "epicgames").
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("epic_app_id")]
    public string? EpicAppId { get; set; }

    /// <summary>
    /// True when ALL associated downloads for this game are evicted.
    /// Games with no matching downloads are NOT considered evicted.
    /// Computed at query time — not persisted to the database.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("is_evicted")]
    public bool IsEvicted { get; set; } = false;
}
