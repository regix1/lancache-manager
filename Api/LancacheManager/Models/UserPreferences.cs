using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LancacheManager.Models;

public class UserPreferences
{
    [Key]
    public int Id { get; set; }

    // Foreign key to UserSession
    public string SessionId { get; set; } = string.Empty;

    // Theme preferences
    public string? SelectedTheme { get; set; }

    // UI preferences
    public bool SharpCorners { get; set; }
    public bool DisableFocusOutlines { get; set; }
    public bool DisableTooltips { get; set; }
    public bool PicsAlwaysVisible { get; set; }
    public bool HideAboutSections { get; set; }
    public bool DisableStickyNotifications { get; set; }

    // Timestamp
    public DateTime UpdatedAtUtc { get; set; }

    // Navigation property
    [ForeignKey(nameof(SessionId))]
    public UserSession? Session { get; set; }
}
