namespace LancacheManager.Models;

public class AuthStatusResponse
{
    public bool IsAuthenticated { get; set; }
    /// <summary>False when Security:EnableAuthentication is disabled; lets the frontend bypass the login prompt + setup wizard.</summary>
    public bool AuthenticationEnabled { get; set; }

    /// <summary>The current session's kind. Null when <see cref="IsAuthenticated"/> is false.</summary>
    public SessionType? SessionType { get; set; }

    /// <summary>The current session's id. Null when <see cref="IsAuthenticated"/> is false.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>When the current session expires. Null when <see cref="IsAuthenticated"/> is false.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// The account the current session signed in as. Null for a guest, an API-key caller and the
    /// disabled-authentication session, none of which have an account row (UserSession.cs:29).
    /// </summary>
    public Guid? AccountId { get; set; }

    /// <summary>
    /// True when the caller is the main admin, the one account that cannot be deleted, disabled or
    /// demoted and the only one that may create further admins.
    /// </summary>
    public bool IsMainAdmin { get; set; }
    public bool HasData { get; set; }
    public bool HasBeenInitialized { get; set; }
    public bool HasDataLoaded { get; set; }
    public bool GuestAccessEnabled { get; set; }
    public int GuestDurationHours { get; set; }
    /// <summary>Backward-compatible: true if any prefill service (Steam, Epic, or Battle.net) is active.</summary>
    public bool PrefillEnabled { get; set; }
    public bool SteamPrefillEnabled { get; set; }

    /// <summary>When Steam prefill access expires for this session. Null when <see cref="SteamPrefillEnabled"/> is false.</summary>
    public DateTime? SteamPrefillExpiresAt { get; set; }
    public bool EpicPrefillEnabled { get; set; }

    /// <summary>When Epic prefill access expires for this session. Null when <see cref="EpicPrefillEnabled"/> is false.</summary>
    public DateTime? EpicPrefillExpiresAt { get; set; }
    public bool BattlenetPrefillEnabled { get; set; }

    /// <summary>When Battle.net prefill access expires for this session. Null when <see cref="BattlenetPrefillEnabled"/> is false.</summary>
    public DateTime? BattlenetPrefillExpiresAt { get; set; }
    public bool RiotPrefillEnabled { get; set; }

    /// <summary>When Riot prefill access expires for this session. Null when <see cref="RiotPrefillEnabled"/> is false.</summary>
    public DateTime? RiotPrefillExpiresAt { get; set; }
    public bool XboxPrefillEnabled { get; set; }

    /// <summary>When Xbox prefill access expires for this session. Null when <see cref="XboxPrefillEnabled"/> is false.</summary>
    public DateTime? XboxPrefillExpiresAt { get; set; }
}

public class LoginRequest
{
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The account signing in. Matched against the stored username case-insensitively, because the
    /// column is citext (AppDbContext.cs:209-211) and two names differing only in case are one
    /// account. [38]
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The account's password. Required alongside the key and the username: all three are checked, so
    /// holding the installation's key is no longer a sign-in on its own. [38][39]
    /// </summary>
    public string Password { get; set; } = string.Empty;
}

public class LoginResponse
{
    public bool Success { get; set; }
    public SessionType SessionType { get; set; }
    public DateTime ExpiresAt { get; set; }
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
    /// <summary>Backward-compatible: true if Steam, Epic, or Battle.net prefill is active, or session is admin.</summary>
    public bool PrefillEnabled { get; set; }
    public bool SteamPrefillEnabled { get; set; }
    public DateTime? SteamPrefillExpiresAt { get; set; }
    public bool EpicPrefillEnabled { get; set; }
    public DateTime? EpicPrefillExpiresAt { get; set; }
    public bool BattlenetPrefillEnabled { get; set; }
    public DateTime? BattlenetPrefillExpiresAt { get; set; }
    public bool RiotPrefillEnabled { get; set; }
    public DateTime? RiotPrefillExpiresAt { get; set; }
    public bool XboxPrefillEnabled { get; set; }
    public DateTime? XboxPrefillExpiresAt { get; set; }

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

/// <summary>Response for POST api/api-keys/regenerate: confirms the key was rotated.</summary>
public class ApiKeyRegenerateResponse
{
    public bool Success { get; set; }

    /// <summary>
    /// The key that works from now on. Rotation ends every session including the caller's own, so this
    /// answer is the last one that reaches them and has to carry the key they need to sign back in. [49]
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
    public string Warning { get; set; } = string.Empty;
}

public class GuestConfigResponse
{
    public int DurationHours { get; set; }
    public bool IsLocked { get; set; }
}

public class GuestDurationResponse
{
    public int DurationHours { get; set; }
    public string Source { get; set; } = "config";
    public bool CanEdit { get; set; } = true;
    public int EnvVarValue { get; set; }
}

/// <summary>Response for POST api/auth/logout.</summary>
public class LogoutResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>Response for GET api/auth/guest/status.</summary>
public class GuestStatusResponse
{
    public bool IsLocked { get; set; }
    public int DurationHours { get; set; }
}

/// <summary>Response for POST api/auth/guest/config/lock.</summary>
public class GuestLockResponse
{
    public bool Success { get; set; }
    public bool IsLocked { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for GET api/auth/guest/prefill/config: the Steam guest-prefill defaults, plus the
/// Epic and Battle.net enabled flags that were bundled onto this endpoint before those services
/// got their own guest-prefill config endpoints.
/// </summary>
public class GuestPrefillConfigResponse
{
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; }

    /// <summary>Per-guest-session thread cap. Null when no cap is configured.</summary>
    public int? MaxThreadCount { get; set; }
    public bool EpicEnabledByDefault { get; set; }
    public int EpicDurationHours { get; set; }

    /// <summary>Per-guest-session thread cap for Epic prefill. Null when no cap is configured.</summary>
    public int? EpicMaxThreadCount { get; set; }
    public bool BattlenetEnabledByDefault { get; set; }
    public int BattlenetDurationHours { get; set; }
}

/// <summary>
/// Response for POST api/auth/guest/prefill/config: the Steam guest-prefill defaults after the
/// save. Shared with the Epic and Xbox save endpoints, whose responses have the same shape.
/// </summary>
public class SetGuestPrefillConfigResponse
{
    public bool Success { get; set; }
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; }

    /// <summary>Per-guest-session thread cap. Null when no cap is configured.</summary>
    public int? MaxThreadCount { get; set; }
}

/// <summary>
/// Response for GET api/auth/guest/epic-prefill/config. Shared with the Xbox get endpoint,
/// whose response has the same shape.
/// </summary>
public class EpicGuestPrefillConfigResponse
{
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; }

    /// <summary>Per-guest-session thread cap. Null when no cap is configured.</summary>
    public int? MaxThreadCount { get; set; }
}

/// <summary>
/// Response for GET api/auth/guest/battlenet-prefill/config. Battle.net guest prefill is
/// anonymous (no account login), so it carries no thread-count cap. Shared with the Riot get
/// endpoint, whose response has the same shape.
/// </summary>
public class BattleNetGuestPrefillConfigResponse
{
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; }
}

/// <summary>
/// Response for POST api/auth/guest/battlenet-prefill/config: the Battle.net guest-prefill
/// defaults after the save. Shared with the Riot save endpoint, whose response has the same shape.
/// </summary>
public class SetBattleNetGuestPrefillConfigResponse
{
    public bool Success { get; set; }
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; }
}

/// <summary>Response for POST api/auth/guest/prefill/toggle/{sessionId}: the per-session prefill grant after the change.</summary>
public class GuestPrefillToggleResponse
{
    public bool Success { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public bool Enabled { get; set; }

    /// <summary>When the session's prefill access for this service expires. Null when the grant was just revoked, or <see cref="Enabled"/> is false.</summary>
    public DateTime? PrefillExpiresAt { get; set; }
}
