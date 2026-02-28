using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LancacheManager.Models;

/// <summary>
/// Records individual game prefill operations within a session.
/// Provides a history log of what was prefilled, when, and the result.
/// </summary>
public class PrefillHistoryEntry
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The session ID this entry belongs to
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// App ID of the game (numeric for Steam, string for Epic)
    /// </summary>
    [MaxLength(100)]
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// Name of the game
    /// </summary>
    [MaxLength(200)]
    public string? AppName { get; set; }

    /// <summary>
    /// When the prefill for this game started
    /// </summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>
    /// When the prefill for this game completed (null if still in progress)
    /// </summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Total bytes downloaded for this game
    /// </summary>
    public long BytesDownloaded { get; set; }

    /// <summary>
    /// Total bytes expected for this game
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Result status: InProgress, Completed, Failed, Cancelled
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "InProgress";

    /// <summary>
    /// Error message if the prefill failed
    /// </summary>
    [MaxLength(500)]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Navigation property to the parent session
    /// </summary>
    [ForeignKey("SessionId")]
    public PrefillSession? Session { get; set; }
}
