namespace LancacheManager.Application.DTOs;

/// <summary>
/// Real-time download speed information for a single game/depot
/// </summary>
public class GameSpeedInfo
{
    /// <summary>
    /// Steam depot ID (or equivalent identifier for other services)
    /// </summary>
    public long DepotId { get; set; }

    /// <summary>
    /// Game name (if resolved from depot mapping)
    /// </summary>
    public string? GameName { get; set; }

    /// <summary>
    /// Game app ID (if resolved from depot mapping)
    /// </summary>
    public int? GameAppId { get; set; }

    /// <summary>
    /// Service name (steam, origin, epic, etc.)
    /// </summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>
    /// Current download speed in bytes per second
    /// </summary>
    public double BytesPerSecond { get; set; }

    /// <summary>
    /// Total bytes downloaded in the current window
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Number of requests in the current window
    /// </summary>
    public int RequestCount { get; set; }

    /// <summary>
    /// Cache hit bytes in the current window
    /// </summary>
    public long CacheHitBytes { get; set; }

    /// <summary>
    /// Cache miss bytes in the current window
    /// </summary>
    public long CacheMissBytes { get; set; }

    /// <summary>
    /// Cache hit percentage in the current window
    /// </summary>
    public double CacheHitPercent => TotalBytes > 0 ? (double)CacheHitBytes / TotalBytes * 100 : 0;
}

/// <summary>
/// Real-time download speed information for a single client
/// </summary>
public class ClientSpeedInfo
{
    /// <summary>
    /// Client IP address
    /// </summary>
    public string ClientIp { get; set; } = string.Empty;

    /// <summary>
    /// Current download speed in bytes per second
    /// </summary>
    public double BytesPerSecond { get; set; }

    /// <summary>
    /// Total bytes downloaded in the current window
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Number of active game downloads
    /// </summary>
    public int ActiveGames { get; set; }

    /// <summary>
    /// Cache hit bytes in the current window
    /// </summary>
    public long CacheHitBytes { get; set; }

    /// <summary>
    /// Cache miss bytes in the current window
    /// </summary>
    public long CacheMissBytes { get; set; }
}

/// <summary>
/// Complete snapshot of current download speeds
/// </summary>
public class DownloadSpeedSnapshot
{
    /// <summary>
    /// Timestamp when this snapshot was taken (UTC)
    /// </summary>
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Total download speed across all games/clients in bytes per second
    /// </summary>
    public double TotalBytesPerSecond { get; set; }

    /// <summary>
    /// Per-game speed breakdown
    /// </summary>
    public List<GameSpeedInfo> GameSpeeds { get; set; } = new();

    /// <summary>
    /// Per-client speed breakdown
    /// </summary>
    public List<ClientSpeedInfo> ClientSpeeds { get; set; } = new();

    /// <summary>
    /// Size of the rolling window in seconds
    /// </summary>
    public int WindowSeconds { get; set; }

    /// <summary>
    /// Number of log entries in the current window
    /// </summary>
    public int EntriesInWindow { get; set; }

    /// <summary>
    /// Whether active downloads are detected
    /// </summary>
    public bool HasActiveDownloads => EntriesInWindow > 0;
}

/// <summary>
/// Historical speed snapshot for a time period
/// </summary>
public class SpeedHistorySnapshot
{
    /// <summary>
    /// Start of the query period (UTC)
    /// </summary>
    public DateTime PeriodStartUtc { get; set; }

    /// <summary>
    /// End of the query period (UTC)
    /// </summary>
    public DateTime PeriodEndUtc { get; set; }

    /// <summary>
    /// Duration of the period in minutes
    /// </summary>
    public int PeriodMinutes { get; set; }

    /// <summary>
    /// Total bytes downloaded across all games
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Average speed across the entire period
    /// </summary>
    public double AverageBytesPerSecond { get; set; }

    /// <summary>
    /// Total number of download sessions in the period
    /// </summary>
    public int TotalSessions { get; set; }
}
