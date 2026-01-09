namespace LancacheManager.Models;

public class ClientGroupDto
{
    public int Id { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public List<string> MemberIps { get; set; } = new();
}

/// <summary>
/// Extended ClientStats that includes client group information for display
/// </summary>
public class ClientStatsWithGroup
{
    public string ClientIp { get; set; } = string.Empty;
    public string? DisplayName { get; set; } // Nickname if grouped, null if not
    public int? GroupId { get; set; }
    public bool IsGrouped { get; set; }
    public List<string>? GroupMemberIps { get; set; } // All IPs in group (if aggregated)
    public long TotalCacheHitBytes { get; set; }
    public long TotalCacheMissBytes { get; set; }
    public long TotalBytes { get; set; }
    public double CacheHitPercent { get; set; }
    public int TotalDownloads { get; set; }
    public double TotalDurationSeconds { get; set; }
    public double AverageBytesPerSecond { get; set; }

    /// <summary>
    /// Last activity timestamp in UTC. Frontend handles timezone conversion.
    /// </summary>
    public DateTime LastActivityUtc { get; set; }
}
