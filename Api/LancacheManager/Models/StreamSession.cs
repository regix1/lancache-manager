using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Represents a TCP/UDP stream session from stream-access.log.
/// Contains timing and speed data for bandwidth calculations.
/// </summary>
public class StreamSession
{
    public int Id { get; set; }

    /// <summary>
    /// Client IP address that initiated the stream session.
    /// </summary>
    public string ClientIp { get; set; } = string.Empty;

    /// <summary>
    /// Session start time in UTC (calculated from end time - duration).
    /// </summary>
    public DateTime SessionStartUtc { get; set; }

    /// <summary>
    /// Session end time in UTC (when the log entry was written).
    /// </summary>
    public DateTime SessionEndUtc { get; set; }

    /// <summary>
    /// Session start time in local timezone.
    /// </summary>
    public DateTime SessionStartLocal { get; set; }

    /// <summary>
    /// Session end time in local timezone.
    /// </summary>
    public DateTime SessionEndLocal { get; set; }

    /// <summary>
    /// Protocol used (TCP/UDP).
    /// </summary>
    public string Protocol { get; set; } = "TCP";

    /// <summary>
    /// HTTP status code of the session.
    /// </summary>
    public int Status { get; set; }

    /// <summary>
    /// Bytes sent to the client (download direction).
    /// </summary>
    public long BytesSent { get; set; }

    /// <summary>
    /// Bytes received from the client/upstream (upload direction).
    /// </summary>
    public long BytesReceived { get; set; }

    /// <summary>
    /// Duration of the session in seconds.
    /// </summary>
    public double DurationSeconds { get; set; }

    /// <summary>
    /// The upstream host this session connected to.
    /// </summary>
    public string UpstreamHost { get; set; } = string.Empty;

    /// <summary>
    /// Foreign key to the correlated Download record (if any).
    /// </summary>
    public int? DownloadId { get; set; }

    /// <summary>
    /// Navigation property to the correlated Download.
    /// </summary>
    public Download? Download { get; set; }

    /// <summary>
    /// The datasource this stream session belongs to.
    /// </summary>
    public string Datasource { get; set; } = "default";

    // Computed properties

    /// <summary>
    /// Download speed in bytes per second.
    /// </summary>
    [JsonInclude]
    public double DownloadSpeedBps => DurationSeconds > 0 ? BytesSent / DurationSeconds : 0;

    /// <summary>
    /// Upload speed in bytes per second.
    /// </summary>
    [JsonInclude]
    public double UploadSpeedBps => DurationSeconds > 0 ? BytesReceived / DurationSeconds : 0;

    /// <summary>
    /// Total bytes transferred in this session.
    /// </summary>
    [JsonInclude]
    public long TotalBytes => BytesSent + BytesReceived;

    /// <summary>
    /// Formatted download speed (e.g., "125.4 MB/s").
    /// </summary>
    [JsonInclude]
    public string DownloadSpeedFormatted => FormatSpeed(DownloadSpeedBps);

    /// <summary>
    /// Formatted upload speed (e.g., "45.2 MB/s").
    /// </summary>
    [JsonInclude]
    public string UploadSpeedFormatted => FormatSpeed(UploadSpeedBps);

    /// <summary>
    /// Formatted duration (e.g., "1m 45s").
    /// </summary>
    [JsonInclude]
    public string DurationFormatted => FormatDuration(DurationSeconds);

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
