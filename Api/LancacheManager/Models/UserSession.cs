using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

public class UserSession
{
    [Key]
    public string SessionId { get; set; } = string.Empty;

    // For guests, SessionId and DeviceId are now the same (no more guest_{deviceId}_{timestamp})
    // For authenticated users, they were already the same
    public string DeviceId => SessionId; // Computed property for backward compatibility

    public string DeviceName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string Browser { get; set; } = string.Empty;

    // Session type
    public bool IsGuest { get; set; }

    // Timestamps - all in UTC
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; } // Null for authenticated users, set for guests
    public DateTime LastSeenAtUtc { get; set; }

    // Revocation
    public bool IsRevoked { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }

    // API Key (encrypted) - only for authenticated users
    public string? ApiKey { get; set; }

    // Navigation property
    public UserPreferences? Preferences { get; set; }
}
