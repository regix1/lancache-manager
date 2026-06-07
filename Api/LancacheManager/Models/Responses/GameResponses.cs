using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Response for game removal operation start
/// </summary>
public class GameRemovalStartResponse
{
    public string Message { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public OperationStatus Status { get; set; } = OperationStatus.Running;
}

/// <summary>
/// Response for game detection start
/// </summary>
public class GameDetectionStartResponse
{
    public string Message { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public OperationStatus Status { get; set; } = OperationStatus.Running;
}

/// <summary>
/// Response for active detection status
/// </summary>
public class ActiveDetectionResponse
{
    public bool IsProcessing { get; set; }
    public object? Operation { get; set; }
}

/// <summary>
/// Response for cached detection results
/// </summary>
public class CachedDetectionResponse
{
    public bool HasCachedResults { get; set; }
    public object? Games { get; set; }
    public object? Services { get; set; }
    public int TotalGamesDetected { get; set; }
    public int TotalServicesDetected { get; set; }
    public string? LastDetectionTime { get; set; }

    /// <summary>
    /// Deduplicated total size of active (non-evicted) game cache files on disk.
    /// </summary>
    [JsonPropertyName("games_on_disk_bytes")]
    public ulong GamesOnDiskBytes { get; set; }

    /// <summary>
    /// Number of non-evicted games with cache files on disk.
    /// </summary>
    [JsonPropertyName("games_on_disk_count")]
    public int GamesOnDiskCount { get; set; }

    /// <summary>
    /// Deduplicated total size of matched game and service cache files on disk.
    /// </summary>
    [JsonPropertyName("identified_cache_bytes")]
    public ulong IdentifiedCacheBytes { get; set; }

    /// <summary>
    /// Portion of <see cref="IdentifiedCacheBytes"/> attributed to non-game services.
    /// </summary>
    [JsonPropertyName("identified_service_bytes")]
    public ulong IdentifiedServiceBytes { get; set; }
}

/// <summary>
/// Response for cached corruption detection results
/// </summary>
public class CachedCorruptionResponse
{
    public bool HasCachedResults { get; set; }
    public Dictionary<string, long>? CorruptionCounts { get; set; }
    public int TotalServicesWithCorruption { get; set; }
    public long TotalCorruptedChunks { get; set; }
    public string? LastDetectionTime { get; set; }
}

/// <summary>
/// Response for game image errors
/// </summary>
public class GameImageErrorResponse
{
    public string Error { get; set; } = string.Empty;
}
