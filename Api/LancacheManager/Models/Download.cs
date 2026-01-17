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

    /// <summary>
    /// Duration in seconds calculated from LogEntries (more accurate than EndTime - StartTime).
    /// This is populated by the repository when fetching downloads.
    /// Not stored in the database - marked with NotMapped.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public double? DurationSeconds { get; set; }


    // Computed properties need [JsonInclude] to be serialized
    [JsonInclude]
    public long TotalBytes => CacheHitBytes + CacheMissBytes;

    [JsonInclude]
    public double CacheHitPercent => TotalBytes > 0 ? (CacheHitBytes * 100.0) / TotalBytes : 0;

    /// <summary>
    /// Average download speed in bytes per second, calculated from total bytes and duration.
    /// Uses DurationSeconds from LogEntries if available, otherwise falls back to EndTime - StartTime.
    /// Returns 0 if duration is zero or negative.
    /// </summary>
    [JsonInclude]
    public double AverageBytesPerSecond
    {
        get
        {
            // Prefer duration calculated from LogEntries (more accurate)
            var duration = DurationSeconds ?? (EndTimeUtc - StartTimeUtc).TotalSeconds;
            return duration > 0 ? TotalBytes / duration : 0;
        }
    }
}
