namespace LancacheManager.Constants;

/// <summary>
/// Central location for all system constants and paths used throughout the application
/// </summary>
public static class LancacheConstants
{
    // Directory paths
    public const string DATA_DIRECTORY = "/data";
    public const string LOGS_DIRECTORY = "/logs";
    public const string THEMES_DIRECTORY = "/data/themes";

    // Cache directory paths (in order of preference)
    public const string CACHE_DIRECTORY = "/cache";  // Primary cache path
    public const string CACHE_DIRECTORY_ALT = "/lancache";  // Alternative cache path
    public const string CACHE_DIRECTORY_MOUNTED = "/mnt/cache/cache";  // Docker mounted cache path

    // Array of all possible cache paths for iteration
    public static readonly string[] CACHE_PATHS = { CACHE_DIRECTORY, CACHE_DIRECTORY_ALT, CACHE_DIRECTORY_MOUNTED };

    // File paths
    public const string DATABASE_PATH = "/data/lancache.db";
    public const string LOG_PATH = "/logs/access.log";
    public const string POSITION_FILE = "/data/logposition.txt";
    public const string PROCESSING_MARKER = "/data/bulk_processing.marker";
    public const string PERFORMANCE_DATA_FILE = "/data/performance.json";

    // Configuration keys
    public const string CONFIG_KEY_REQUIRE_AUTH_METRICS = "Security:RequireAuthForMetrics";
    public const string CONFIG_KEY_REQUIRE_AUTH_PERFORMANCE = "Security:RequireAuthForPerformance";
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
}