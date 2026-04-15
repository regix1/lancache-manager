using System.Text.Json.Serialization;

namespace LancacheManager.Models.Responses;

/// <summary>
/// Slim DTO for dashboard-only detection payloads.
/// Mirrors the JSON shape of <see cref="GameCacheInfo"/> for the subset of fields
/// the Dashboard tab actually reads, intentionally dropping unbounded list fields
/// (<c>cache_file_paths</c>, <c>sample_urls</c>, <c>evicted_sample_urls</c>,
/// <c>datasources</c>, <c>depot_ids</c>, <c>evicted_depot_ids</c>) and the less
/// frequently read <c>evicted_bytes</c>. The Management tab keeps consuming the
/// full <see cref="GameCacheInfo"/> via <c>/api/games/cached-detection</c>.
/// Retained fields (verified via dashboard/utils/context consumer scan):
///   <list type="bullet">
///     <item><description><c>game_app_id</c> — Map lookup key (<c>DashboardDataContext</c>)</description></item>
///     <item><description><c>game_name</c> — Map lookup + chart labels</description></item>
///     <item><description><c>cache_files_found</c> — chart slice extras</description></item>
///     <item><description><c>total_size_bytes</c> — games-on-disk aggregate + chart values</description></item>
///     <item><description><c>service</c> — chart slice extras + badges</description></item>
///     <item><description><c>image_url</c> — optional game art for chart tooltips / future dashboard consumers</description></item>
///     <item><description><c>epic_app_id</c> — Epic image proxy key paired with image_url</description></item>
///     <item><description><c>is_evicted</c> — evicted-games filter + count</description></item>
///     <item><description><c>evicted_downloads_count</c> — partial-eviction detection</description></item>
///   </list>
/// </summary>
public class DashboardGameSummary
{
    [JsonPropertyName("game_app_id")]
    public long GameAppId { get; set; }

    [JsonPropertyName("game_name")]
    public string GameName { get; set; } = string.Empty;

    [JsonPropertyName("cache_files_found")]
    public int CacheFilesFound { get; set; }

    [JsonPropertyName("total_size_bytes")]
    public ulong TotalSizeBytes { get; set; }

    [JsonPropertyName("service")]
    public string? Service { get; set; }

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("epic_app_id")]
    public string? EpicAppId { get; set; }

    [JsonPropertyName("is_evicted")]
    public bool IsEvicted { get; set; }

    [JsonPropertyName("evicted_downloads_count")]
    public int EvictedDownloadsCount { get; set; }
}

/// <summary>
/// Slim DTO for dashboard-only detection service aggregates.
/// Mirrors the JSON shape of <see cref="ServiceCacheInfo"/> for the subset the
/// Dashboard tab reads, intentionally dropping unbounded list fields
/// (<c>cache_file_paths</c>, <c>sample_urls</c>, <c>evicted_sample_urls</c>,
/// <c>datasources</c>) and the less frequently read <c>evicted_bytes</c>.
/// Retained fields (verified via dashboard/utils/context consumer scan):
///   <list type="bullet">
///     <item><description><c>service_name</c> — Map lookup key</description></item>
///     <item><description><c>cache_files_found</c> — service-level fallback aggregate</description></item>
///     <item><description><c>total_size_bytes</c> — service-level fallback aggregate</description></item>
///     <item><description><c>is_evicted</c> — service evicted state</description></item>
///     <item><description><c>evicted_downloads_count</c> — partial-eviction detection</description></item>
///   </list>
/// </summary>
public class DashboardServiceSummary
{
    [JsonPropertyName("service_name")]
    public string ServiceName { get; set; } = string.Empty;

    [JsonPropertyName("cache_files_found")]
    public int CacheFilesFound { get; set; }

    [JsonPropertyName("total_size_bytes")]
    public ulong TotalSizeBytes { get; set; }

    [JsonPropertyName("is_evicted")]
    public bool IsEvicted { get; set; }

    [JsonPropertyName("evicted_downloads_count")]
    public int EvictedDownloadsCount { get; set; }
}
