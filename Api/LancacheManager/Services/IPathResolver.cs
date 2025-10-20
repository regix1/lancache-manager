namespace LancacheManager.Services;

/// <summary>
/// Interface for platform-specific path resolution
/// </summary>
public interface IPathResolver
{
    /// <summary>
    /// Resolves a relative path to an absolute path based on the operating system
    /// </summary>
    string ResolvePath(string relativePath);

    /// <summary>
    /// Normalizes path separators for the current platform
    /// </summary>
    string NormalizePath(string path);

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

    /// <summary>
    /// Gets the path to the Rust log processor executable
    /// </summary>
    string GetRustLogProcessorPath();

    /// <summary>
    /// Gets the path to the Rust database reset executable
    /// </summary>
    string GetRustDatabaseResetPath();

    /// <summary>
    /// Gets the path to the Rust log manager executable
    /// </summary>
    string GetRustLogManagerPath();

    /// <summary>
    /// Gets the path to the Rust cache cleaner executable
    /// </summary>
    string GetRustCacheCleanerPath();

    /// <summary>
    /// Gets the path to the Rust corruption manager executable
    /// </summary>
    string GetRustCorruptionManagerPath();
}