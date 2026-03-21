namespace LancacheManager.Models;

/// <summary>
/// Paginated response for the Retro download view.
/// Groups downloads by DepotId + ClientIp and aggregates cache stats.
/// </summary>
public class RetroDownloadResponse
{
    public List<RetroDownloadDto> Items { get; set; } = new();
    public int TotalItems { get; set; }
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

    /// <summary>Steam depot ID, null if non-Steam</summary>
    public uint? DepotId { get; set; }

    /// <summary>Resolved game/app name from depot mapping or download record</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>Steam app ID for game image lookup</summary>
    public uint? SteamAppId { get; set; }

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
    public List<int> DownloadIds { get; set; } = new();
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
    public string Client { get; set; } = "all";
    public string Search { get; set; } = "";
    public bool HideLocalhost { get; set; } = false;
    public bool ShowZeroBytes { get; set; } = false;
    public bool HideUnknown { get; set; } = false;
}
