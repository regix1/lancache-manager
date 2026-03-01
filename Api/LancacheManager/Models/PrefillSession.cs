using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Persisted record of prefill daemon sessions.
/// Allows tracking sessions across app restarts and provides admin visibility.
/// </summary>
public class PrefillSession
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The daemon session ID (GUID format)
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// SessionId of the auth session that created this prefill session
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string CreatedBySessionId { get; set; } = string.Empty;

    /// <summary>
    /// Docker container ID (if still running)
    /// </summary>
    [MaxLength(100)]
    public string? ContainerId { get; set; }

    /// <summary>
    /// Docker container name
    /// </summary>
    [MaxLength(100)]
    public string? ContainerName { get; set; }

    /// <summary>
    /// The Steam username used for login (stored lowercase for ban identification)
    /// </summary>
    [MaxLength(100)]
    public string? SteamUsername { get; set; }

    /// <summary>
    /// Platform identifier (e.g., "Steam", "Epic"). Defaults to "Steam" for backward compatibility.
    /// </summary>
    [MaxLength(20)]
    public string Platform { get; set; } = "Steam";

    /// <summary>
    /// Session status: Active, Terminated, Expired, Orphaned
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "Active";

    /// <summary>
    /// Whether the user is currently authenticated with Steam
    /// </summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// Whether a prefill operation is currently running
    /// </summary>
    public bool IsPrefilling { get; set; }

    /// <summary>
    /// When the session was created
    /// </summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When the session ended (terminated, expired, or orphaned)
    /// </summary>
    public DateTime? EndedAtUtc { get; set; }

    /// <summary>
    /// When the session was scheduled to expire
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// Reason for session termination (if applicable)
    /// </summary>
    [MaxLength(200)]
    public string? TerminationReason { get; set; }

    /// <summary>
    /// Who terminated the session (if manually terminated)
    /// </summary>
    [MaxLength(100)]
    public string? TerminatedBy { get; set; }

    /// <summary>
    /// History of games prefilled during this session
    /// </summary>
    public ICollection<PrefillHistoryEntry> PrefillHistory { get; set; } = new List<PrefillHistoryEntry>();
}
