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

    // Blizzard-specific fields for TACT system
    public string? BlizzardProduct { get; set; } // Product code: "wow", "pro", "hs", etc.
    public int? BlizzardArchiveIndex { get; set; } // Which archive file (0, 1, 2, ...)
    public uint? BlizzardByteOffset { get; set; } // Byte offset within archive (4KB-aligned)
    public string? BlizzardFileName { get; set; } // Resolved file name from chunk mapping

    // Computed properties need [JsonInclude] to be serialized
    [JsonInclude]
    public long TotalBytes => CacheHitBytes + CacheMissBytes;

    [JsonInclude]
    public double CacheHitPercent => TotalBytes > 0 ? (CacheHitBytes * 100.0) / TotalBytes : 0;
}