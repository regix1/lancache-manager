using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Tracks banned Steam users by their hashed username.
/// The username is SHA-256 hashed for privacy - we can verify bans without storing plaintext usernames.
/// </summary>
public class BannedSteamUser
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// SHA-256 hash of the Steam username (lowercase, trimmed).
    /// This is deterministic - same username always produces the same hash.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string UsernameHash { get; set; } = string.Empty;

    /// <summary>
    /// Reason for the ban (optional, for admin reference)
    /// </summary>
    [MaxLength(500)]
    public string? BanReason { get; set; }

    /// <summary>
    /// DeviceId of the user who was banned (for reference)
    /// </summary>
    [MaxLength(100)]
    public string? BannedDeviceId { get; set; }

    /// <summary>
    /// When the ban was created
    /// </summary>
    public DateTime BannedAtUtc { get; set; }

    /// <summary>
    /// Who issued the ban (admin DeviceId or "system")
    /// </summary>
    [MaxLength(100)]
    public string? BannedBy { get; set; }

    /// <summary>
    /// Optional expiry time. Null means permanent ban.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>
    /// Whether the ban has been lifted
    /// </summary>
    public bool IsLifted { get; set; }

    /// <summary>
    /// When the ban was lifted (if applicable)
    /// </summary>
    public DateTime? LiftedAtUtc { get; set; }

    /// <summary>
    /// Who lifted the ban (if applicable)
    /// </summary>
    [MaxLength(100)]
    public string? LiftedBy { get; set; }
}
