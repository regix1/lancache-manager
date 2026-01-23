using System.Text.Json.Serialization;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Models;

public class ClientStats : IUtcMarkable
{
    public string ClientIp { get; set; } = string.Empty;
    public long TotalCacheHitBytes { get; set; }
    public long TotalCacheMissBytes { get; set; }
    [JsonInclude]
    public long TotalBytes => TotalCacheHitBytes + TotalCacheMissBytes;

    [JsonInclude]
    public double CacheHitPercent => TotalBytes > 0 ? (TotalCacheHitBytes * 100.0) / TotalBytes : 0;
    public int TotalDownloads { get; set; }

    /// <summary>
    /// Total download duration in seconds across all sessions.
    /// Used to calculate average speed.
    /// </summary>
    public double TotalDurationSeconds { get; set; }

    /// <summary>
    /// Average download speed in bytes per second.
    /// Calculated as TotalBytes / TotalDurationSeconds.
    /// </summary>
    [JsonInclude]
    public double AverageBytesPerSecond => TotalDurationSeconds > 0 ? TotalBytes / TotalDurationSeconds : 0;

    // UTC timestamps - always stored in UTC for consistent querying
    public DateTime LastActivityUtc { get; set; }

    // Local timestamps - stored in the user's configured timezone for display
    public DateTime LastActivityLocal { get; set; }

    public void MarkDateTimesAsUtc()
    {
        LastActivityUtc = LastActivityUtc.AsUtc();
    }
}
