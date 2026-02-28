using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LancacheManager.Models;

public class UserPreferences
{
    [Key]
    public int Id { get; set; }

    // Foreign key to UserSession
    public Guid SessionId { get; set; }

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

    // Allowed time formats for this user (JSON array, null = all formats allowed)
    // Valid values: server-24h, server-12h, local-24h, local-12h
    public string? AllowedTimeFormats { get; set; }

    // Refresh rate for guest users (null = use default guest refresh rate)
    // Valid values: LIVE, ULTRA, REALTIME, STANDARD, RELAXED, SLOW
    public string? RefreshRate { get; set; }

    // Per-session refresh rate lock (null = use global default, true = locked, false = unlocked)
    public bool? RefreshRateLocked { get; set; }

    // Per-session max thread count limit per service (null = use system default)
    public int? SteamMaxThreadCount { get; set; }
    public int? EpicMaxThreadCount { get; set; }

    // Timestamp
    public DateTime UpdatedAtUtc { get; set; }

    // Navigation property
    [ForeignKey(nameof(SessionId))]
    public UserSession? Session { get; set; }
}
