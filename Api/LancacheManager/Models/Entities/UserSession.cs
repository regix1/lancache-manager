using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

public class UserSession
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// SHA-256 hash of the raw session token (stored Base64URL-encoded in cookie)
    /// </summary>
    public string SessionTokenHash { get; set; } = string.Empty;

    /// <summary>
    /// Admin or Guest session type.
    /// </summary>
    public SessionType SessionType { get; set; }

    /// <summary>
    /// The account this session signed in as. Null for the three kinds of session that are created
    /// without one: a guest session, the shared session an <c>X-Api-Key</c> caller runs as, and the
    /// shared session created while authentication is disabled.
    ///
    /// Deliberately a plain id rather than a foreign key, for the same reason
    /// <see cref="IdentityAuditEntry"/> holds plain ids: deleting an account must leave its session
    /// rows behind so they can be rejected, not take them with it.
    /// </summary>
    public Guid? AccountId { get; set; }

    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;

    // Client-reported + GeoIP-enriched metadata. All optional - populated by
    // the /api/sessions/me/client-info endpoint. The public IP is resolved
    // server-side (the request's remote address, falling back to
    // PublicIpLookupService) rather than reported by the browser; the POST
    // body only carries the browser-reported locale/screen fields.
    public string? PublicIpAddress { get; set; }
    public string? CountryCode { get; set; }
    public string? CountryName { get; set; }
    public string? RegionName { get; set; }
    public string? City { get; set; }
    public string? Timezone { get; set; }
    public string? IspName { get; set; }
    public string? ScreenResolution { get; set; }
    public string? BrowserLanguage { get; set; }

    // Timestamps - all in UTC
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }

    // Revocation
    public bool IsRevoked { get; set; }
    public DateTime? RevokedAtUtc { get; set; }

    // Prefill access - when set and in the future, guest has prefill tab access for the given service
    public DateTime? SteamPrefillExpiresAtUtc { get; set; }
    public DateTime? EpicPrefillExpiresAtUtc { get; set; }
    public DateTime? BattleNetPrefillExpiresAtUtc { get; set; }
    public DateTime? RiotPrefillExpiresAtUtc { get; set; }
    public DateTime? XboxPrefillExpiresAtUtc { get; set; }

    // Token rotation - previous token remains valid during grace period
    public string? PreviousSessionTokenHash { get; set; }
    public DateTime? PreviousTokenValidUntilUtc { get; set; }

    // Navigation property
    public UserPreferences? Preferences { get; set; }
}
