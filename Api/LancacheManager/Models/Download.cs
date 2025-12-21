using System.Text.Json.Serialization;

namespace LancacheManager.Models;

public class Download
{
    public int Id { get; set; }
    public string Service { get; set; } = string.Empty;
    public string ClientIp { get; set; } = string.Empty;

    // UTC timestamps - always stored in UTC for consistent querying
    public DateTime StartTimeUtc { get; set; }

    // Local timestamps - stored in the user's configured timezone for display
    public DateTime StartTimeLocal { get; set; }

    public DateTime EndTimeUtc { get; set; }
    public DateTime EndTimeLocal { get; set; }

    public long CacheHitBytes { get; set; }
    public long CacheMissBytes { get; set; }
    public bool IsActive { get; set; }

    // New fields for game information
    public uint? GameAppId { get; set; }
    public string? GameName { get; set; }
    public string? GameImageUrl { get; set; }
    public string? LastUrl { get; set; } // Store the last URL to extract game info
    public uint? DepotId { get; set; } // Steam depot ID extracted from URLs

    /// <summary>
    /// The datasource this download belongs to (for multi-datasource support).
    /// Defaults to "default" for backward compatibility.
    /// </summary>
    public string Datasource { get; set; } = "default";

    // Speed data from stream-access.log correlation
    /// <summary>
    /// Average download speed in bytes per second (data sent to client).
    /// Calculated from stream-access.log session data.
    /// </summary>
    public double? DownloadSpeedBps { get; set; }

    /// <summary>
    /// Average upload speed in bytes per second (data received from origin).
    /// Calculated from stream-access.log session data.
    /// </summary>
    public double? UploadSpeedBps { get; set; }

    /// <summary>
    /// Total session duration in seconds from stream-access.log.
    /// This is the aggregate duration of all stream sessions correlated to this download.
    /// </summary>
    public double? SessionDurationSeconds { get; set; }

    /// <summary>
    /// Number of stream sessions correlated to this download.
    /// Multiple TCP sessions may be used for a single download.
    /// </summary>
    public int? StreamSessionCount { get; set; }

    // Computed properties need [JsonInclude] to be serialized
    [JsonInclude]
    public long TotalBytes => CacheHitBytes + CacheMissBytes;

    [JsonInclude]
    public double CacheHitPercent => TotalBytes > 0 ? (CacheHitBytes * 100.0) / TotalBytes : 0;

    /// <summary>
    /// Formatted download speed (e.g., "125.4 MB/s")
    /// </summary>
    [JsonInclude]
    public string? DownloadSpeedFormatted => DownloadSpeedBps.HasValue ? FormatSpeed(DownloadSpeedBps.Value) : null;

    /// <summary>
    /// Formatted upload speed (e.g., "45.2 MB/s")
    /// </summary>
    [JsonInclude]
    public string? UploadSpeedFormatted => UploadSpeedBps.HasValue ? FormatSpeed(UploadSpeedBps.Value) : null;

    /// <summary>
    /// Formatted session duration (e.g., "1m 45s")
    /// </summary>
    [JsonInclude]
    public string? SessionDurationFormatted => SessionDurationSeconds.HasValue ? FormatDuration(SessionDurationSeconds.Value) : null;

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1_000_000_000)
            return $"{bytesPerSecond / 1_000_000_000:F2} GB/s";
        if (bytesPerSecond >= 1_000_000)
            return $"{bytesPerSecond / 1_000_000:F2} MB/s";
        if (bytesPerSecond >= 1_000)
            return $"{bytesPerSecond / 1_000:F2} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }

    private static string FormatDuration(double seconds)
    {
        var timeSpan = TimeSpan.FromSeconds(seconds);
        if (timeSpan.TotalHours >= 1)
            return $"{(int)timeSpan.TotalHours}h {timeSpan.Minutes}m {timeSpan.Seconds}s";
        if (timeSpan.TotalMinutes >= 1)
            return $"{(int)timeSpan.TotalMinutes}m {timeSpan.Seconds}s";
        return $"{timeSpan.TotalSeconds:F1}s";
    }
}