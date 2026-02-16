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
    /// "admin" or "guest"
    /// </summary>
    public string SessionType { get; set; } = string.Empty;

    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;

    // Timestamps - all in UTC
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }

    // Revocation
    public bool IsRevoked { get; set; }
    public DateTime? RevokedAtUtc { get; set; }

    // Prefill access - when set and in the future, guest has prefill tab access
    public DateTime? PrefillExpiresAtUtc { get; set; }

    // Token rotation - previous token remains valid during grace period
    public string? PreviousSessionTokenHash { get; set; }
    public DateTime? PreviousTokenValidUntilUtc { get; set; }

    // Navigation property
    public UserPreferences? Preferences { get; set; }
}
