using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Tracks banned Steam users by their username.
/// </summary>
public class BannedSteamUser
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The Steam username that was banned (stored lowercase for case-insensitive matching).
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Reason for the ban (optional, for admin reference)
    /// </summary>
    [MaxLength(500)]
    public string? BanReason { get; set; }

    /// <summary>
    /// SessionId of the auth session that was banned (for reference)
    /// </summary>
    [MaxLength(100)]
    public string? BannedBySessionId { get; set; }

    /// <summary>
    /// When the ban was created
    /// </summary>
    public DateTime BannedAtUtc { get; set; }

    /// <summary>
    /// Who issued the ban (admin SessionId or "system")
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
