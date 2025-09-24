using System.Runtime.InteropServices;

namespace LancacheManager.Constants;

/// <summary>
/// Central location for all system constants and paths used throughout the application
/// Automatically detects OS and provides appropriate paths (Linux first, then Windows)
/// </summary>
public static class LancacheConstants
{
    private static bool? _isLinuxEnvironment;

    /// <summary>
    /// Detects if running in Linux environment using reliable platform detection
    /// </summary>
    public static bool IsLinuxEnvironment
    {
        get
        {
            if (_isLinuxEnvironment.HasValue)
                return _isLinuxEnvironment.Value;

            // Use reliable platform detection (not directory checking which can be misleading)
            _isLinuxEnvironment = Environment.OSVersion.Platform == PlatformID.Unix ||
                                Environment.OSVersion.Platform == PlatformID.MacOSX ||
                                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                                RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

            return _isLinuxEnvironment.Value;
        }
    }

    /// <summary>
    /// Gets the base directory for Windows development (Api folder)
    /// </summary>
    public static string WindowsBasePath
    {
        get
        {
            // Try to find the Api directory by looking for the project structure
            var currentDir = Directory.GetCurrentDirectory();

            // If we're already in the right place or a subdirectory
            var dir = new DirectoryInfo(currentDir);
            while (dir != null)
            {
                // Look for the Api directory
                if (Directory.Exists(Path.Combine(dir.FullName, "Api")))
                {
                    return dir.FullName;
                }

                // If we're in the Api directory itself, return its parent
                if (dir.Name == "Api" && dir.Parent != null)
                {
                    return dir.Parent.FullName;
                }

                // If we're in the LancacheManager directory (inside Api), go up two levels
                if (dir.Name == "LancacheManager" && dir.Parent?.Name == "Api" && dir.Parent.Parent != null)
                {
                    return dir.Parent.Parent.FullName;
                }

                dir = dir.Parent;
            }

            // Fallback: use the application base directory and try to navigate
            var appBase = AppDomain.CurrentDomain.BaseDirectory;
            var testDir = new DirectoryInfo(appBase);

            // Navigate up from bin folder structure (e.g., bin/Debug/net8.0)
            while (testDir != null && testDir.Name != "Api")
            {
                // Look for Api directory at this level
                if (Directory.Exists(Path.Combine(testDir.FullName, "Api")))
                {
                    return testDir.FullName;
                }
                testDir = testDir.Parent;
            }

            // Last resort: use the directory that should contain the project
            var fallbackPath = @"H:\_git\lancache-manager";
            if (Directory.Exists(fallbackPath))
            {
                return fallbackPath;
            }

            // Final fallback: current directory
            return currentDir;
        }
    }

    // Directory paths - dynamic based on OS
    public static string DATA_DIRECTORY => IsLinuxEnvironment ? "/data" : Path.Combine(WindowsBasePath, "data");
    public static string LOGS_DIRECTORY => IsLinuxEnvironment ? "/logs" : Path.Combine(WindowsBasePath, "logs");
    public static string THEMES_DIRECTORY => Path.Combine(DATA_DIRECTORY, "themes");

    // Cache directory paths (in order of preference)
    public static string CACHE_DIRECTORY => IsLinuxEnvironment ? "/cache" : Path.Combine(WindowsBasePath, "cache");
    public static string CACHE_DIRECTORY_ALT => IsLinuxEnvironment ? "/lancache" : Path.Combine(WindowsBasePath, "lancache");
    public static string CACHE_DIRECTORY_MOUNTED => IsLinuxEnvironment ? "/mnt/cache/cache" : Path.Combine(WindowsBasePath, "mnt", "cache", "cache");

    // Array of all possible cache paths for iteration
    public static string[] CACHE_PATHS => new[] { CACHE_DIRECTORY, CACHE_DIRECTORY_ALT, CACHE_DIRECTORY_MOUNTED };

    // File paths - dynamic based on OS
    public static string DATABASE_PATH => Path.Combine(DATA_DIRECTORY, "lancache.db");
    public static string LOG_PATH => Path.Combine(LOGS_DIRECTORY, "access.log");
    public static string POSITION_FILE => Path.Combine(DATA_DIRECTORY, "logposition.txt");
    public static string PROCESSING_MARKER => Path.Combine(DATA_DIRECTORY, "bulk_processing.marker");
    public static string PERFORMANCE_DATA_FILE => Path.Combine(DATA_DIRECTORY, "performance.json");

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