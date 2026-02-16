namespace LancacheManager.Models;

public class AuthStatusResponse
{
    public bool IsAuthenticated { get; set; }
    public string? SessionType { get; set; }
    public string? SessionId { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool HasData { get; set; }
    public bool HasBeenInitialized { get; set; }
    public bool HasDataLoaded { get; set; }
    public bool GuestAccessEnabled { get; set; }
    public int GuestDurationHours { get; set; }
    public bool PrefillEnabled { get; set; }
    public DateTime? PrefillExpiresAt { get; set; }
    public string? Token { get; set; }
}

public class LoginRequest
{
    public string ApiKey { get; set; } = string.Empty;
}

public class LoginResponse
{
    public bool Success { get; set; }
    public string SessionType { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string? Token { get; set; }
}

public class SessionDto
{
    public string Id { get; set; } = string.Empty;
    public string SessionType { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
    public bool IsCurrentSession { get; set; }
    public bool IsExpired { get; set; }
    public DateTime? RevokedAt { get; set; }
    public bool PrefillEnabled { get; set; }
    public DateTime? PrefillExpiresAt { get; set; }
}

public class ApiKeyStatusResponse
{
    public bool HasApiKey { get; set; }
    public string KeyType { get; set; } = string.Empty;
    public bool HasPrimaryKey { get; set; }
}

public class GuestConfigResponse
{
    public int DurationHours { get; set; }
    public bool IsLocked { get; set; }
}

public class GuestPrefillConfigResponse
{
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; } = 2;
}
