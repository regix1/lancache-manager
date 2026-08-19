using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LancacheManager.Models;

public class UserPreferences
{
    [Key]
    public long Id { get; set; }

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
    /// <summary>
    /// Reads every time in the app on the UTC clock, whatever <see cref="UseLocalTimezone"/> says. It is a
    /// third answer to "which clock", not a variation on the other two, which is why it is its own column
    /// rather than a combination of them. UTC has no 12-hour face worth offering, so choosing it also puts
    /// <see cref="Use24HourFormat"/> on.
    /// </summary>
    public bool UseUtcTimezone { get; set; }
    public bool Use24HourFormat { get; set; }
    public bool ShowDatasourceLabels { get; set; } = true;

    // Allowed time formats for this user (JSON array, null = all formats allowed)
    // Valid values: server-24h, server-12h, local-24h, local-12h, utc
    public string? AllowedTimeFormats { get; set; }

    // Refresh rate for guest users (null = use default guest refresh rate)
    // Persisted as UPPER-CASE string ("LIVE", "ULTRA", "REALTIME", "STANDARD", "RELAXED", "SLOW")
    // via an EF ValueConverter configured in AppDbContext.
    public RefreshRate? RefreshRate { get; set; }

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
