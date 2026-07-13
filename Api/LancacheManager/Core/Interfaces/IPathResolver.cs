namespace LancacheManager.Core.Interfaces;

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
    /// Gets the config directory path
    /// </summary>
    string GetConfigDirectory();

    /// <summary>
    /// Gets the state directory path
    /// </summary>
    string GetStateDirectory();

    /// <summary>
    /// Gets the durable directory for structural corruption baselines.
    /// This directory is not subject to temporary operation-file cleanup.
    /// </summary>
    string GetStructuralCorruptionStateDirectory();

    /// <summary>Gets the stable, filesystem-safe state scope for a datasource/cache root.</summary>
    string GetStructuralCorruptionStateScope(string datasourceName, string cachePath);

    /// <summary>Gets the durable SQLite baseline path for a datasource/cache root.</summary>
    string GetStructuralCorruptionStateDatabasePath(string datasourceName, string cachePath);

    /// <summary>
    /// Gets the security directory path
    /// </summary>
    string GetSecurityDirectory();

    /// <summary>
    /// Gets the legacy SQLite database directory path
    /// </summary>
    string GetLegacySqliteDirectory();

    /// <summary>
    /// Gets the PICS data directory path
    /// </summary>
    string GetPicsDirectory();

    /// <summary>
    /// Gets the prefill data directory path
    /// </summary>
    string GetPrefillDirectory();

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
    string GetImagesDirectory();

    /// <summary>
    /// Gets the operations directory path for temporary operation progress files
    /// </summary>
    string GetOperationsDirectory();

    /// <summary>
    /// Cleans up old operation progress files (completed operations older than the specified age)
    /// </summary>
    /// <param name="maxAgeHours">Maximum age in hours before files are deleted (default 24)</param>
    /// <returns>Number of files deleted</returns>
    int CleanupOperationFiles(int maxAgeHours = 24);

    /// <summary>
    /// Migrates operation files from the old data directory location to the new operations directory
    /// </summary>
    /// <returns>Number of files migrated</returns>
    int MigrateOperationFiles();

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
    /// Gets the path to the Rust Steam game cache remover executable
    /// </summary>
    string GetRustSteamRemoverPath();

    /// <summary>
    /// Gets the path to the Rust Epic game remover executable
    /// </summary>
    string GetRustEpicRemoverPath();

    /// <summary>
    /// Gets the path to the per-service Rust named-game (Blizzard/Riot/Xbox) remover
    /// executable for the given owning service (e.g. "blizzard", "riot", "xbox").
    /// Each service has its own thin binary (cache_{service}_remove) over a shared core.
    /// </summary>
    string GetRustNamedGameRemoverPath(string service);

    /// <summary>
    /// Gets the path to the Rust service remover executable
    /// </summary>
    string GetRustServiceRemoverPath();

    /// <summary>
    /// Gets the path to the Rust eviction scanner executable
    /// </summary>
    string GetRustEvictionScanPath();

    /// <summary>
    /// Gets the path to the Rust bulk log-purge executable (cache_purge_log_entries).
    /// Used by RemoveEvictedRecordsAsync to rewrite access.log files and drop entries
    /// for all evicted games in a single pass.
    /// </summary>
    string GetRustLogPurgePath();

    /// <summary>
    /// Gets the path to the Rust speed tracker executable
    /// </summary>
    string GetRustSpeedTrackerPath();

    /// <summary>
    /// Gets the path to the legacy SQLite database file
    /// </summary>
    string GetLegacySqlitePath();

    /// <summary>
    /// Gets the path to the Data Protection keys directory
    /// </summary>
    string GetDataProtectionKeysPath();

    /// <summary>
    /// Gets the path to a settings file in the config directory
    /// </summary>
    /// <param name="settingsFileName">The name of the settings file</param>
    string GetSettingsPath(string settingsFileName);

    /// <summary>
    /// Gets the path to the PostgreSQL credentials file
    /// </summary>
    string GetPostgresCredentialsPath();

    /// <summary>
    /// Checks if a directory is writable
    /// </summary>
    /// <param name="directoryPath">The directory path to check</param>
    /// <returns>True if the directory is writable, false otherwise</returns>
    bool IsDirectoryWritable(string directoryPath);

    /// <summary>
    /// Checks if the cache directory is writable
    /// </summary>
    bool IsCacheWritable();

    /// <summary>
    /// Checks if the logs directory is writable
    /// </summary>
    bool IsLogsWritable();

    /// <summary>
    /// Checks if the Docker socket is available for container communication
    /// Required for nginx log rotation after log/cache manipulation operations
    /// </summary>
    bool IsDockerSocketAvailable();
}
