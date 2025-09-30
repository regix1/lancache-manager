namespace LancacheManager.Constants;

/// <summary>
/// Central location for system constants used throughout the application
/// </summary>
public static class LancacheConstants
{
    // Configuration keys
    public const string CONFIG_KEY_REQUIRE_AUTH_METRICS = "Security:RequireAuthForMetrics";
    public const string CONFIG_KEY_API_KEY = "Security:ApiKey";

    // Default values
    public const int DEFAULT_DOWNLOAD_LIMIT = 100;
    public const int DEFAULT_STATS_PERIOD_HOURS = 24;
    public const int DEFAULT_CACHE_CLEAR_BATCH_SIZE = 100;
    public const string DEFAULT_THEME_NAME = "dark-default";

    // Service names (for consistency)
    public const string SERVICE_STEAM = "steam";
    public const string SERVICE_EPIC = "epic";
    public const string SERVICE_ORIGIN = "origin";
    public const string SERVICE_BLIZZARD = "blizzard";
    public const string SERVICE_RIOT = "riot";
    public const string SERVICE_WSUS = "wsus";

    // System theme IDs
    public static readonly string[] SYSTEM_THEMES = { "dark-default", "light-default" };

    // Time constants
    public const int CACHE_CLEAR_TIMEOUT_MINUTES = 30;
    public const int LOG_PROCESSING_TIMEOUT_MINUTES = 60;
    public const int OPERATION_POLL_INTERVAL_MS = 1000;

    // Size constants
    public const long MIN_CACHE_SIZE_FOR_WARNING = 1073741824; // 1GB in bytes
    public const int MAX_LOG_BATCH_SIZE = 10000;

    /// <summary>
    /// Known lancache services for log filtering and identification
    /// </summary>
    public static readonly HashSet<string> KNOWN_SERVICES = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "epic", "epicgames", "origin", "blizzard", "battle.net", "battlenet",
        "wsus", "riot", "riotgames", "uplay", "ubisoft", "gog", "nintendo", "sony",
        "microsoft", "xbox", "apple", "frontier", "nexusmods", "wargaming", "arenanet"
    };

    /// <summary>
    /// Checks if a string contains only hexadecimal characters (0-9, a-f, A-F)
    /// </summary>
    public static bool IsHex(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        return value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }
}