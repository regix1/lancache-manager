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
/// A single parsed log entry for speed calculation
/// </summary>
public class SpeedLogEntry
{
    public DateTime Timestamp { get; set; }
    public string ClientIp { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public long DepotId { get; set; }
    public long BytesSent { get; set; }
    public bool IsCacheHit { get; set; }
}

/// <summary>
/// Network interface bandwidth statistics
/// </summary>
public class NetworkBandwidthSnapshot
{
    /// <summary>
    /// Timestamp when this snapshot was taken (UTC)
    /// </summary>
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Network interface name (e.g., "eth0", "Ethernet")
    /// </summary>
    public string InterfaceName { get; set; } = string.Empty;

    /// <summary>
    /// Download speed in bytes per second (data received by the server)
    /// For LANCache: This is data FROM the internet (cache misses being filled)
    /// </summary>
    public double DownloadBytesPerSecond { get; set; }

    /// <summary>
    /// Upload speed in bytes per second (data sent by the server)
    /// For LANCache: This is data TO clients (cache hits + misses being served)
    /// </summary>
    public double UploadBytesPerSecond { get; set; }

    /// <summary>
    /// Total bytes received since system start
    /// </summary>
    public long TotalBytesReceived { get; set; }

    /// <summary>
    /// Total bytes sent since system start
    /// </summary>
    public long TotalBytesSent { get; set; }

    /// <summary>
    /// Whether the network interface was found and is being monitored
    /// </summary>
    public bool IsAvailable { get; set; }

    /// <summary>
    /// Error message if monitoring is not available
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Combined speed snapshot with both network interface and per-game data
/// </summary>
public class CombinedSpeedSnapshot
{
    /// <summary>
    /// Network interface bandwidth (total throughput)
    /// </summary>
    public NetworkBandwidthSnapshot NetworkBandwidth { get; set; } = new();

    /// <summary>
    /// Per-game/per-client speed breakdown from log analysis
    /// </summary>
    public DownloadSpeedSnapshot GameSpeeds { get; set; } = new();
}

/// <summary>
/// Historical speed information for a single game
/// </summary>
public class GameSpeedHistoryInfo
{
    /// <summary>
    /// Game app ID (if available)
    /// </summary>
    public int? GameAppId { get; set; }

    /// <summary>
    /// Game name (if available)
    /// </summary>
    public string? GameName { get; set; }

    /// <summary>
    /// Game header image URL
    /// </summary>
    public string? GameImageUrl { get; set; }

    /// <summary>
    /// Service name (steam, epic, etc.)
    /// </summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>
    /// Total bytes downloaded in the period
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Cache hit bytes
    /// </summary>
    public long CacheHitBytes { get; set; }

    /// <summary>
    /// Cache miss bytes
    /// </summary>
    public long CacheMissBytes { get; set; }

    /// <summary>
    /// Cache hit percentage
    /// </summary>
    public double CacheHitPercent => TotalBytes > 0 ? (double)CacheHitBytes / TotalBytes * 100 : 0;

    /// <summary>
    /// Average download speed in bytes per second over the download duration
    /// </summary>
    public double AverageBytesPerSecond { get; set; }

    /// <summary>
    /// Number of download sessions
    /// </summary>
    public int SessionCount { get; set; }

    /// <summary>
    /// First download start time in this period
    /// </summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>
    /// Last download end time in this period
    /// </summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>
    /// Total download duration in seconds
    /// </summary>
    public double TotalDurationSeconds { get; set; }

    /// <summary>
    /// Number of unique clients that downloaded this game
    /// </summary>
    public int UniqueClients { get; set; }
}

/// <summary>
/// Historical speed information for a single client
/// </summary>
public class ClientSpeedHistoryInfo
{
    /// <summary>
    /// Client IP address
    /// </summary>
    public string ClientIp { get; set; } = string.Empty;

    /// <summary>
    /// Total bytes downloaded in the period
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Cache hit bytes
    /// </summary>
    public long CacheHitBytes { get; set; }

    /// <summary>
    /// Cache miss bytes
    /// </summary>
    public long CacheMissBytes { get; set; }

    /// <summary>
    /// Average download speed in bytes per second
    /// </summary>
    public double AverageBytesPerSecond { get; set; }

    /// <summary>
    /// Number of games downloaded
    /// </summary>
    public int GamesDownloaded { get; set; }

    /// <summary>
    /// Number of download sessions
    /// </summary>
    public int SessionCount { get; set; }

    /// <summary>
    /// First activity time in this period
    /// </summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>
    /// Last activity time in this period
    /// </summary>
    public DateTime LastSeenUtc { get; set; }
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
    /// Per-game historical speeds
    /// </summary>
    public List<GameSpeedHistoryInfo> GameSpeeds { get; set; } = new();

    /// <summary>
    /// Per-client historical speeds
    /// </summary>
    public List<ClientSpeedHistoryInfo> ClientSpeeds { get; set; } = new();

    /// <summary>
    /// Total number of download sessions in the period
    /// </summary>
    public int TotalSessions { get; set; }
}
