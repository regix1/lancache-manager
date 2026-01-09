namespace LancacheManager.Core.Interfaces.Services;

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
    /// Gets the devices directory path
    /// </summary>
    string GetDevicesDirectory();

    /// <summary>
    /// Gets the themes directory path
    /// </summary>
    string GetThemesDirectory();

    /// <summary>
    /// Gets the cached images directory path
    /// </summary>
    string GetCachedImagesDirectory();

    /// <summary>
    /// Gets the operations directory path for temporary operation progress files
    /// </summary>
    string GetOperationsDirectory();

    /// <summary>
    /// Cleans up old operation progress files (completed operations older than the specified age)
    /// </summary>
    /// <param name="maxAgeHours">Maximum age in hours before files are deleted (default 24)</param>
    /// <returns>Number of files deleted</returns>
    int CleanupOldOperationFiles(int maxAgeHours = 24);

    /// <summary>
    /// Migrates operation files from the old data directory location to the new operations directory
    /// </summary>
    /// <returns>Number of files migrated</returns>
    int MigrateOperationFilesToNewLocation();

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
    /// Gets the path to the Rust cache size calculator executable
    /// </summary>
    string GetRustCacheSizePath();

    /// <summary>
    /// Gets the path to the Rust corruption manager executable
    /// </summary>
    string GetRustCorruptionManagerPath();

    /// <summary>
    /// Gets the path to the Rust game cache detector executable
    /// </summary>
    string GetRustGameDetectorPath();

    /// <summary>
    /// Gets the path to the Rust game cache remover executable
    /// </summary>
    string GetRustGameRemoverPath();

    /// <summary>
    /// Gets the path to the Rust service remover executable
    /// </summary>
    string GetRustServiceRemoverPath();

    /// <summary>
    /// Gets the path to the Rust data migrator executable
    /// </summary>
    string GetRustDataMigratorPath();

    /// <summary>
    /// Gets the path to the Rust speed tracker executable
    /// </summary>
    string GetRustSpeedTrackerPath();

    /// <summary>
    /// Gets the path to the database file
    /// </summary>
    string GetDatabasePath();

    /// <summary>
    /// Checks if a directory is writable
    /// </summary>
    /// <param name="directoryPath">The directory path to check</param>
    /// <returns>True if the directory is writable, false otherwise</returns>
    bool IsDirectoryWritable(string directoryPath);

    /// <summary>
    /// Checks if the cache directory is writable
    /// </summary>
    bool IsCacheDirectoryWritable();

    /// <summary>
    /// Checks if the logs directory is writable
    /// </summary>
    bool IsLogsDirectoryWritable();

    /// <summary>
    /// Checks if the Docker socket is available for container communication
    /// Required for nginx log rotation after log/cache manipulation operations
    /// </summary>
    bool IsDockerSocketAvailable();
}
