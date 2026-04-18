namespace LancacheManager.Models;

public class AuthStatusResponse
{
    public bool IsAuthenticated { get; set; }
    public SessionType? SessionType { get; set; }
    public Guid? SessionId { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool HasData { get; set; }
    public bool HasBeenInitialized { get; set; }
    public bool HasDataLoaded { get; set; }
    public bool GuestAccessEnabled { get; set; }
    public int GuestDurationHours { get; set; }
    /// <summary>Backward-compatible: true if either Steam or Epic prefill is active.</summary>
    public bool PrefillEnabled { get; set; }
    public bool SteamPrefillEnabled { get; set; }
    public DateTime? SteamPrefillExpiresAt { get; set; }
    public bool EpicPrefillEnabled { get; set; }
    public DateTime? EpicPrefillExpiresAt { get; set; }
    public string? Token { get; set; }
}

public class LoginRequest
{
    public string ApiKey { get; set; } = string.Empty;
}

public class LoginResponse
{
    public bool Success { get; set; }
    public SessionType SessionType { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string? Token { get; set; }
}

public class SessionDto
{
    public Guid Id { get; set; }
    public SessionType SessionType { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
    public bool IsCurrentSession { get; set; }
    public bool IsExpired { get; set; }
    public DateTime? RevokedAt { get; set; }
    /// <summary>Backward-compatible: true if either Steam or Epic prefill is active, or session is admin.</summary>
    public bool PrefillEnabled { get; set; }
    public bool SteamPrefillEnabled { get; set; }
    public DateTime? SteamPrefillExpiresAt { get; set; }
    public bool EpicPrefillEnabled { get; set; }
    public DateTime? EpicPrefillExpiresAt { get; set; }

    // Client-reported + GeoIP-enriched metadata. All optional.
    public string? PublicIpAddress { get; set; }
    public string? CountryCode { get; set; }
    public string? CountryName { get; set; }
    public string? RegionName { get; set; }
    public string? City { get; set; }
    public string? Timezone { get; set; }
    public string? IspName { get; set; }
    public string? ScreenResolution { get; set; }
    public string? BrowserLanguage { get; set; }
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
