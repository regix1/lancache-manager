using LancacheManager.Constants;

namespace LancacheManager.Services;

/// <summary>
/// Cross-platform path resolver service that handles Windows and Linux path differences
/// </summary>
public class PathResolverService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PathResolverService> _logger;

    public PathResolverService(IConfiguration configuration, ILogger<PathResolverService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a relative path to an absolute path based on the operating system
    /// </summary>
    public string ResolvePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return string.Empty;

        // If already absolute, normalize separators and return
        if (Path.IsPathRooted(relativePath))
        {
            return NormalizePath(relativePath);
        }

        // For relative paths, combine with appropriate base directory
        var basePath = LancacheConstants.IsLinuxEnvironment
            ? "/"
            : LancacheConstants.WindowsBasePath;

        var fullPath = Path.Combine(basePath, relativePath);
        return NormalizePath(fullPath);
    }

    /// <summary>
    /// Normalizes path separators for the current platform
    /// </summary>
    public string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        // Replace all separators with the platform-appropriate one
        var normalized = path.Replace('/', Path.DirectorySeparatorChar)
                            .Replace('\\', Path.DirectorySeparatorChar);

        // Remove duplicate separators
        while (normalized.Contains($"{Path.DirectorySeparatorChar}{Path.DirectorySeparatorChar}"))
        {
            normalized = normalized.Replace(
                $"{Path.DirectorySeparatorChar}{Path.DirectorySeparatorChar}",
                Path.DirectorySeparatorChar.ToString());
        }

        return normalized;
    }

    /// <summary>
    /// Gets the cache path from configuration, properly resolved for the platform
    /// </summary>
    public string GetCachePath()
    {
        var configPath = _configuration["LanCache:CachePath"];
        if (!string.IsNullOrEmpty(configPath))
        {
            return ResolvePath(configPath);
        }

        // Fall back to constants
        return LancacheConstants.CACHE_DIRECTORY;
    }

    /// <summary>
    /// Gets the log path from configuration, properly resolved for the platform
    /// </summary>
    public string GetLogPath()
    {
        var configPath = _configuration["LanCache:LogPath"];
        if (!string.IsNullOrEmpty(configPath))
        {
            return ResolvePath(configPath);
        }

        // Fall back to constants
        return LancacheConstants.LOG_PATH;
    }

    /// <summary>
    /// Gets the database path from configuration, properly resolved for the platform
    /// </summary>
    public string GetDatabasePath()
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrEmpty(connectionString) && connectionString.StartsWith("Data Source="))
        {
            var dbPath = connectionString.Substring("Data Source=".Length);
            return ResolvePath(dbPath);
        }

        // Fall back to constants
        return LancacheConstants.DATABASE_PATH;
    }

    /// <summary>
    /// Gets the API key path from configuration, properly resolved for the platform
    /// </summary>
    public string GetApiKeyPath()
    {
        var configPath = _configuration["Security:ApiKeyPath"];
        if (!string.IsNullOrEmpty(configPath))
        {
            return ResolvePath(configPath);
        }

        // Fall back to constants
        return Path.Combine(LancacheConstants.DATA_DIRECTORY, "api_key.txt");
    }

    /// <summary>
    /// Gets the devices path from configuration, properly resolved for the platform
    /// </summary>
    public string GetDevicesPath()
    {
        var configPath = _configuration["Security:DevicesPath"];
        if (!string.IsNullOrEmpty(configPath))
        {
            return ResolvePath(configPath);
        }

        // Fall back to constants
        return Path.Combine(LancacheConstants.DATA_DIRECTORY, "devices");
    }
}