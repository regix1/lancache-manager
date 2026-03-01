namespace LancacheManager.Models;

/// <summary>
/// Response for prefill cache status check
/// </summary>
public class PrefillCacheStatusResponse
{
    public List<string> UpToDateAppIds { get; set; } = new();
    public List<string> OutdatedAppIds { get; set; } = new();
    public string? Message { get; set; }
}

/// <summary>
/// Response for paginated prefill sessions
/// </summary>
public class PrefillSessionsResponse
{
    public List<PrefillSessionDto> Sessions { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

/// <summary>
/// DTO for prefill session information
/// </summary>
public class PrefillSessionDto
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string CreatedBySessionId { get; set; } = string.Empty;
    public string? ContainerId { get; set; }
    public string? ContainerName { get; set; }
    public string? SteamUsername { get; set; }
    public string Platform { get; set; } = "Steam";
    public string? Username { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsAuthenticated { get; set; }
    public bool IsPrefilling { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string? TerminationReason { get; set; }
    public string? TerminatedBy { get; set; }
    public bool IsLive { get; set; }
}

/// <summary>
/// DTO for banned Steam user information
/// </summary>
public class BannedSteamUserDto
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? BanReason { get; set; }
    public string? BannedBySessionId { get; set; }
    public DateTime BannedAtUtc { get; set; }
    public string? BannedBy { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsLifted { get; set; }
    public DateTime? LiftedAtUtc { get; set; }
    public string? LiftedBy { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// DTO for prefill history entries
/// </summary>
public class PrefillHistoryEntryDto
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public string? AppName { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// DTO for cached app information
/// </summary>
public class CachedAppDto
{
    public string AppId { get; set; } = string.Empty;
    public string? AppName { get; set; }
    public int DepotCount { get; set; }
    public long TotalBytes { get; set; }
    public DateTime CachedAtUtc { get; set; }
    public string? CachedBy { get; set; }
}

/// <summary>
/// Response for cache check operation
/// </summary>
public class CacheCheckResponse
{
    public List<string> CachedAppIds { get; set; } = new();
    public List<string> UncachedAppIds { get; set; } = new();
    public List<CachedAppDto> CacheInfo { get; set; } = new();
}

