namespace LancacheManager.Models;

/// <summary>
/// Unified DTO for session data (both authenticated and guest sessions)
/// </summary>
public class SessionDto
{
    public string Id { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? IpAddress { get; set; }
    public string? LocalIp { get; set; }
    public string? Hostname { get; set; }
    public string? OperatingSystem { get; set; }
    public string? Browser { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsExpired { get; set; }
    public bool IsRevoked { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public string Type { get; set; } = "authenticated";
    public bool PrefillEnabled { get; set; }
    public DateTime? PrefillExpiresAt { get; set; }
    public bool IsPrefillExpired { get; set; }
}

/// <summary>
/// Pagination metadata for paginated responses
/// </summary>
public class PaginationInfo
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public bool HasNextPage { get; set; }
    public bool HasPreviousPage { get; set; }
}

/// <summary>
/// Response DTO for paginated sessions list
/// </summary>
public class PaginatedSessionsResponse
{
    public List<SessionDto> Sessions { get; set; } = new();
    public PaginationInfo Pagination { get; set; } = new();
    public int Count { get; set; }
    public int AuthenticatedCount { get; set; }
    public int GuestCount { get; set; }
}
