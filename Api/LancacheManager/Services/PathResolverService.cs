using LancacheManager.Services;

namespace LancacheManager.Services;

/// <summary>
/// Cross-platform path resolver service that handles Windows and Linux path differences
/// </summary>
public class PathResolverService : IPathResolver
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

        // For relative paths, combine with application base directory
        var basePath = AppContext.BaseDirectory;
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

        // Fall back to default cache directory in data folder
        return Path.Combine(GetDataDirectory(), "cache");
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

        // Fall back to default log path in data folder
        return Path.Combine(GetDataDirectory(), "logs", "access.log");
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

        // Fall back to default database path
        return Path.Combine(GetDataDirectory(), "lancache.db");
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

        // Fall back to default api key path
        return Path.Combine(GetDataDirectory(), "api_key.txt");
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

        // Fall back to default devices path
        return Path.Combine(GetDataDirectory(), "devices");
    }

    /// <summary>
    /// Gets the base directory path for the application
    /// </summary>
    public string GetBasePath()
    {
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Gets the data directory path
    /// </summary>
    public string GetDataDirectory()
    {
        var configPath = _configuration["LanCache:DataPath"];
        if (!string.IsNullOrEmpty(configPath))
        {
            return ResolvePath(configPath);
        }

        // Fall back to default data directory relative to app base
        var configuredPath = _configuration["DataDirectory"];
        if (!string.IsNullOrEmpty(configuredPath))
        {
            return Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(AppContext.BaseDirectory, configuredPath);
        }

        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    /// <summary>
    /// Gets the logs directory path
    /// </summary>
    public string GetLogsDirectory()
    {
        var configPath = _configuration["LanCache:LogsPath"];
        if (!string.IsNullOrEmpty(configPath))
        {
            return ResolvePath(configPath);
        }

        // Fall back to default logs directory in data folder
        return Path.Combine(GetDataDirectory(), "logs");
    }



    /// <summary>
    /// Gets the cache directory path
    /// </summary>
    public string GetCacheDirectory()
    {
        return GetCachePath();
    }

    /// <summary>
    /// Gets the themes directory path
    /// </summary>
    public string GetThemesDirectory()
    {
        return Path.Combine(GetDataDirectory(), "themes");
    }
}