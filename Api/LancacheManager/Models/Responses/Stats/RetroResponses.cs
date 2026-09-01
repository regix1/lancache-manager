namespace LancacheManager.Models;

/// <summary>
/// Paginated response for the Retro download view.
/// Groups downloads by DepotId + ClientIp and aggregates cache stats.
/// </summary>
public class RetroDownloadResponse
{
    public List<RetroDownloadDto> Items { get; set; } = new();
    public int TotalItems { get; set; }
    /// <summary>Individual downloads behind every group in the filtered set, across all pages.
    /// TotalItems counts groups, and the pager shows both: how many rows it is paging through and
    /// how many downloads those rows stand for.</summary>
    public int TotalDownloads { get; set; }
    public int TotalPages { get; set; }
    public int CurrentPage { get; set; }
    public int PageSize { get; set; }
}

/// <summary>
/// A single grouped download row in the Retro view.
/// Each row represents all downloads for a specific depot + client IP combination.
/// </summary>
public class RetroDownloadDto
{
    /// <summary>Composite key: depotId_clientIp or nodepot_service_clientIp_downloadId</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Earliest download start time in the group (UTC)</summary>
    public DateTime StartTimeUtc { get; set; }

    /// <summary>Latest download end time in the group (UTC)</summary>
    public DateTime EndTimeUtc { get; set; }

    /// <summary>Latest download start time in the group (UTC). Distinct from EndTimeUtc: the
    /// grouped Downloads views order and label their groups by the newest member's start.</summary>
    public DateTime LastStartTimeUtc { get; set; }

    /// <summary>Steam depot ID, null if non-Steam</summary>
    public long? DepotId { get; set; }

    /// <summary>Resolved game/app name from depot mapping or download record</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>Steam app ID for game image lookup</summary>
    public long? SteamAppId { get; set; }

    /// <summary>Epic Games app ID for game image lookup</summary>
    public string? EpicAppId { get; set; }

    /// <summary>Service name (steam, epic, wsus, etc.)</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>Datasource name for multi-datasource support</summary>
    public string Datasource { get; set; } = string.Empty;

    /// <summary>Client IP address</summary>
    public string ClientIp { get; set; } = string.Empty;

    /// <summary>Weighted average download speed in bytes per second</summary>
    public double AverageBytesPerSecond { get; set; }

    /// <summary>Total cache hit bytes across all downloads in group</summary>
    public long CacheHitBytes { get; set; }

    /// <summary>Total cache miss bytes across all downloads in group</summary>
    public long CacheMissBytes { get; set; }

    /// <summary>Cache hit percentage (0-100)</summary>
    public double CacheHitPercent { get; set; }

    /// <summary>Total bytes (hit + miss) across all downloads in group</summary>
    public long TotalBytes { get; set; }

    /// <summary>Number of individual download sessions in this group</summary>
    public int RequestCount { get; set; }

    /// <summary>List of original download IDs for event association lookups</summary>
    public List<long> DownloadIds { get; set; } = new();

    /// <summary>All distinct client IPs in this group (single-element for non-merged rows)</summary>
    public List<string> ClientIps { get; set; } = new();

    /// <summary>All distinct depot IDs in this group (single-element, zero excluded, for non-merged rows)</summary>
    public List<uint> DepotIds { get; set; } = new();

    /// <summary>True when every download in the group has been evicted from the cache</summary>
    public bool IsEvicted { get; set; }

    /// <summary>True when some, but not all, downloads in the group have been evicted from the cache</summary>
    public bool IsPartiallyEvicted { get; set; }

    /// <summary>The group's newest download by start time, which is the member the grouped views
    /// render a collapsed row from. One row per group, never the whole membership. Null for every
    /// grouping other than MergeAcrossServices.</summary>
    public Download? PrimaryDownload { get; set; }

    /// <summary>True when any member of the group carries a resolved game name, or when the group
    /// is the Unknown/Other bucket. The views hide the group's title when it is false, and the
    /// first member alone cannot answer it.</summary>
    public bool HasRealGameName { get; set; }

    /// <summary>"game" or "content" for a row produced with MergeAcrossServices, which decides the
    /// icon and label the grouped views render. Empty for every other grouping. Never "metadata":
    /// that type belongs to a zero-byte group, and no completed zero-byte row reaches this
    /// endpoint.</summary>
    public string GroupType { get; set; } = string.Empty;
}

/// <summary>
/// Query parameters for the Retro download view endpoint
/// </summary>
public class RetroDownloadQuery
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string Sort { get; set; } = "latest";
    public string Service { get; set; } = "all";
    /// <summary>Comma-separated client addresses, because a dropdown entry can name a client
    /// group, which is several addresses; a single address is that list with one member.</summary>
    public string Client { get; set; } = "all";
    public string Search { get; set; } = "";
    public bool HideLocalhost { get; set; } = false;
    public bool ShowZeroBytes { get; set; } = true;
    /// <summary>Drops downloads under 1 MB</summary>
    public bool HideSmallFiles { get; set; } = false;
    /// <summary>Drops evicted downloads for this reader alone. The stored evicted-data mode hides
    /// them for every reader and is applied separately, so either one is enough to hide a row.</summary>
    public bool HideEvicted { get; set; } = false;
    public bool HideUnknown { get; set; } = false;
    /// <summary>Keeps downloads that are still running in the list. Retro is a history view and
    /// leaves them out, which is the default; the grouped Downloads views ask for them, because on
    /// a cache box the running download is the row the reader is most likely watching.</summary>
    public bool IncludeActive { get; set; } = false;
    public bool GroupByGame { get; set; } = false;
    /// <summary>When true, merges all rows for the same Service into one row, overriding GroupByGame</summary>
    public bool GroupByService { get; set; } = false;
    /// <summary>Read only when GroupByGame is set. Keys each game bucket on the game identity
    /// alone, so one title seen under more than one service is a single row, and nameless rows
    /// fall back to a per-service bucket. This is what the grouped Downloads views show.</summary>
    public bool MergeAcrossServices { get; set; } = false;
    /// <summary>Read only when MergeAcrossServices is set. Collapses unmapped Steam rows into one
    /// "Unknown/Other" bucket instead of leaving them in the per-service one.</summary>
    public bool GroupUnknownGames { get; set; } = false;
    /// <summary>Sorts groups with more than one download ahead of single-download groups, each
    /// part ordered by Sort. Ignored by the sorts that impose their own full order.</summary>
    public bool GroupByFrequency { get; set; } = false;
    /// <summary>Unix timestamp (seconds) - filter downloads with StartTimeUtc &gt;= this value</summary>
    public long? StartTime { get; set; }
    /// <summary>Unix timestamp (seconds) - filter downloads with StartTimeUtc &lt;= this value</summary>
    public long? EndTime { get; set; }
    /// <summary>When set, only include downloads tagged to this event</summary>
    public long? EventId { get; set; }
    /// <summary>Byte-weighted hit/miss bucket filter: "hit" (CacheHitPercent &gt;= 50), "miss" (&lt; 50), or "all"/empty for no filtering</summary>
    public string HitMiss { get; set; } = "all";
}
