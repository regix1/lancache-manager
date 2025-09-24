using System.Text.Json.Serialization;

namespace LancacheManager.Models;

public class Download
{
    public int Id { get; set; }
    public string Service { get; set; } = string.Empty;
    public string ClientIp { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public long CacheHitBytes { get; set; }
    public long CacheMissBytes { get; set; }
    public bool IsActive { get; set; }
    
    // New fields for game information
    public uint? GameAppId { get; set; }
    public string? GameName { get; set; }
    public string? GameImageUrl { get; set; }
    public string? LastUrl { get; set; } // Store the last URL to extract game info
    public uint? DepotId { get; set; } // Steam depot ID extracted from URLs
    
    // Computed properties need [JsonInclude] to be serialized
    [JsonInclude]
    public long TotalBytes => CacheHitBytes + CacheMissBytes;

    [JsonInclude]
    public double CacheHitPercent => TotalBytes > 0 ? (CacheHitBytes * 100.0) / TotalBytes : 0;
}