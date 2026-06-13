using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Tracks banned prefill users. A ban keys on either the game <see cref="Username"/>
/// (Steam/Epic, captured at credential time) or, for anonymous services with no login
/// such as Battle.net, the shared lancache-manager auth-session <see cref="BannedUserId"/> GUID.
/// Exactly one of the two identity columns is populated per ban.
/// </summary>
public class BannedSteamUser
{
    [Key]
    public long Id { get; set; }

    /// <summary>
    /// The game username that was banned (stored lowercase for case-insensitive matching).
    /// Null for UserId-based bans (anonymous services like Battle.net have no username).
    /// </summary>
    [MaxLength(100)]
    public string? Username { get; set; }

    /// <summary>
    /// The lancache-manager auth-session id (UserSession.Id / DaemonSession.UserId GUID) that was banned.
    /// Used for anonymous services (e.g. Battle.net) that issue no game username. Null for username-based bans.
    /// </summary>
    public Guid? BannedUserId { get; set; }

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
