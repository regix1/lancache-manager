using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LancacheManager.Models;

public class UserPreferences
{
    [Key]
    public int Id { get; set; }

    // Foreign key to UserSession (using DeviceId, the persistent identifier)
    public string DeviceId { get; set; } = string.Empty;

    // Theme preferences
    public string? SelectedTheme { get; set; }

    // UI preferences
    public bool SharpCorners { get; set; }
    public bool DisableFocusOutlines { get; set; }
    public bool DisableTooltips { get; set; }
    public bool PicsAlwaysVisible { get; set; }
    public bool DisableStickyNotifications { get; set; }
    public bool UseLocalTimezone { get; set; }
    public bool Use24HourFormat { get; set; }
    public bool ShowDatasourceLabels { get; set; } = true;

    // Date display preferences
    public bool ShowYearInDates { get; set; }

    // Refresh rate for guest users (null = use default guest refresh rate)
    // Valid values: LIVE, ULTRA, REALTIME, STANDARD, RELAXED, SLOW
    public string? RefreshRate { get; set; }

    // Timestamp
    public DateTime UpdatedAtUtc { get; set; }

    // Navigation property
    [ForeignKey(nameof(DeviceId))]
    public UserSession? Session { get; set; }
}
