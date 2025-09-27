namespace LancacheManager.Services;

/// <summary>
/// Interface for platform-specific path resolution
/// </summary>
public interface IPathResolver
{
    /// <summary>
    /// Gets the base directory for the application
    /// </summary>
    string GetBasePath();

    /// <summary>
    /// Gets the data directory path
    /// </summary>
    string GetDataDirectory();

    /// <summary>
    /// Gets the logs directory path
    /// </summary>
    string GetLogsDirectory();

    /// <summary>
    /// Gets the cache directory path
    /// </summary>
    string GetCacheDirectory();


    /// <summary>
    /// Gets the themes directory path
    /// </summary>
    string GetThemesDirectory();
}