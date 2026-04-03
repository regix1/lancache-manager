using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Platform;

/// <summary>
/// Abstract base class for platform-specific path resolvers, containing shared logic
/// </summary>
public abstract class PathResolverBase : IPathResolver
{
    protected readonly ILogger _logger;

    protected PathResolverBase(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the base path for the application (platform-specific)
    /// </summary>
    protected abstract string BasePath { get; }

    /// <summary>
    /// Gets the file extension for Rust executables on this platform (e.g., "" for Linux, ".exe" for Windows)
    /// </summary>
    protected abstract string RustExecutableExtension { get; }

    public string GetBasePath() => BasePath;

    public virtual string GetDataDirectory()
    {
        var path = Path.GetFullPath(Path.Combine(BasePath, "data"));
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetConfigDirectory() => Path.GetFullPath(Path.Combine(GetDataDirectory(), "config"));

    public string GetStateDirectory() => Path.GetFullPath(Path.Combine(GetDataDirectory(), "state"));

    public string GetSecurityDirectory() => Path.GetFullPath(Path.Combine(GetDataDirectory(), "security"));

    public string GetLegacySqliteDirectory() => Path.GetFullPath(Path.Combine(GetDataDirectory(), "db"));

    public virtual string GetPicsDirectory()
    {
        var path = Path.GetFullPath(Path.Combine(GetDataDirectory(), "pics"));
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetPrefillDirectory() => Path.GetFullPath(Path.Combine(GetDataDirectory(), "prefill"));

    public virtual string GetLogsDirectory()
    {
        var path = Path.GetFullPath(Path.Combine(BasePath, "logs"));
        Directory.CreateDirectory(path);
        return path;
    }

    public virtual string GetCacheDirectory()
    {
        var path = Path.GetFullPath(Path.Combine(BasePath, "cache"));
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetDevicesDirectory() => Path.GetFullPath(Path.Combine(GetDataDirectory(), "devices"));

    public string GetThemesDirectory() => Path.GetFullPath(Path.Combine(GetDataDirectory(), "themes"));

    public string GetCachedImagesDirectory() => Path.GetFullPath(Path.Combine(GetDataDirectory(), "cache", "images"));

    public string GetOperationsDirectory()
    {
        var path = Path.GetFullPath(Path.Combine(GetDataDirectory(), "operations"));
        Directory.CreateDirectory(path);
        return path;
    }

    public int CleanupOldOperationFiles(int maxAgeHours = 24)
    {
        var operationsDir = GetOperationsDirectory();
        var deletedCount = 0;
        var cutoffTime = DateTime.UtcNow.AddHours(-maxAgeHours);

        try
        {
            foreach (var file in Directory.GetFiles(operationsDir, "*.json"))
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTimeUtc < cutoffTime)
                    {
                        File.Delete(file);
                        deletedCount++;
                        _logger.LogDebug("Deleted old operation file: {File}", file);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete operation file: {File}", file);
                }
            }

            if (deletedCount > 0)
            {
                _logger.LogInformation("Cleaned up {Count} old operation files from {Dir}", deletedCount, operationsDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cleaning up operations directory: {Dir}", operationsDir);
        }

        return deletedCount;
    }

    public int MigrateOperationFilesToNewLocation()
    {
        var dataDir = GetDataDirectory();
        var operationsDir = GetOperationsDirectory();
        var migratedCount = 0;

        // Patterns for operation files that should be moved
        var patterns = new[]
        {
            "cache_clear_progress_*.json",
            "corruption_removal_*.json",
            "log_remove_progress.json",
            "corruption_removal_progress.json"
        };

        try
        {
            foreach (var pattern in patterns)
            {
                foreach (var file in Directory.GetFiles(dataDir, pattern))
                {
                    try
                    {
                        var fileName = Path.GetFileName(file);
                        var destPath = Path.Combine(operationsDir, fileName);

                        // Move the file
                        File.Move(file, destPath, overwrite: true);
                        migratedCount++;
                        _logger.LogDebug("Migrated operation file: {File} -> {Dest}", file, destPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to migrate operation file: {File}", file);
                    }
                }
            }

            if (migratedCount > 0)
            {
                _logger.LogInformation("Migrated {Count} operation files to {Dir}", migratedCount, operationsDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error migrating operation files to {Dir}", operationsDir);
        }

        return migratedCount;
    }

    public string GetRustLogProcessorPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"log_processor{RustExecutableExtension}");

    public string GetRustDatabaseResetPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"db_reset{RustExecutableExtension}");

    public string GetRustLogManagerPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"log_service_manager{RustExecutableExtension}");

    public string GetRustCacheCleanerPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"cache_clear{RustExecutableExtension}");

    public string GetRustCacheSizePath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"cache_size{RustExecutableExtension}");

    public string GetRustCorruptionManagerPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"cache_corruption{RustExecutableExtension}");

    public string GetRustGameDetectorPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"cache_game_detect{RustExecutableExtension}");

    public string GetRustGameRemoverPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"cache_game_remove{RustExecutableExtension}");

    public string GetRustEpicRemoverPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"cache_epic_remove{RustExecutableExtension}");

    public string GetRustServiceRemoverPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"cache_service_remove{RustExecutableExtension}");

    public string GetRustEvictionScanPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"cache_eviction_scan{RustExecutableExtension}");

    public string GetRustDataMigratorPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"db_migrate{RustExecutableExtension}");

    public string GetRustSpeedTrackerPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"speed_tracker{RustExecutableExtension}");

    public string GetLegacySqlitePath() =>
        Path.Combine(GetLegacySqliteDirectory(), "LancacheManager.db");

    public string GetDataProtectionKeysPath() =>
        Path.Combine(GetSecurityDirectory(), "DataProtection-Keys");

    public string GetPostgresCredentialsPath() =>
        Path.Combine(GetConfigDirectory(), "postgres-credentials.json");

    public string GetSettingsPath(string settingsFileName) =>
        Path.Combine(GetConfigDirectory(), settingsFileName);

    /// <summary>
    /// Resolves a relative path to an absolute path based on the operating system
    /// </summary>
    public abstract string ResolvePath(string relativePath);

    /// <summary>
    /// Normalizes path separators for the current platform
    /// </summary>
    public abstract string NormalizePath(string path);

    /// <summary>
    /// Checks if a directory is writable. Base implementation uses test-file creation.
    /// Linux overrides to add /proc/mounts check and existing-file sampling.
    /// </summary>
    public virtual bool IsDirectoryWritable(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                _logger.LogWarning("Directory does not exist: {Path}", directoryPath);
                return false;
            }

            return TestWriteAccess(directoryPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing write access to directory: {Path}", directoryPath);
            return false;
        }
    }

    public bool IsCacheDirectoryWritable() => IsDirectoryWritable(GetCacheDirectory());

    public bool IsLogsDirectoryWritable() => IsDirectoryWritable(GetLogsDirectory());

    public abstract bool IsDockerSocketAvailable();

    /// <summary>
    /// Tests write access by opening existing files first, then falling back to create-test.
    /// </summary>
    protected bool TestWriteAccess(string directoryPath)
    {
        // Strategy: Test ACTUAL write access by opening existing files for write.
        // This is more reliable than UID/GID comparison because it:
        // - Works on all architectures (ARM64, x86_64)
        // - Handles ACLs, group permissions, and root correctly
        // - Tests real write ability, not just ownership

        // Step 1: Try to find and test existing files in the directory tree
        try
        {
            var existingFiles = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
                .Take(20) // Check up to 20 files for a representative sample
                .ToList();

            _logger.LogDebug("Found {Count} files to test in {Path}", existingFiles.Count, directoryPath);

            if (existingFiles.Count > 0)
            {
                foreach (var existingFile in existingFiles)
                {
                    try
                    {
                        _logger.LogDebug("Testing write access on existing file: {Path}", existingFile);
                        // Try to open the file for write access WITHOUT modifying it
                        // FileShare.None is the definitive access check for write permission
                        using (var fs = new FileStream(existingFile, FileMode.Open, FileAccess.Write, FileShare.None))
                        {
                            // Successfully opened for write - we have permission
                            _logger.LogDebug("Write access confirmed for file: {Path}", existingFile);
                            return true;
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Cannot modify this file - permission issue (likely PUID/PGID mismatch)
                        _logger.LogWarning(
                            "Permission denied on existing file: {Path}. " +
                            "This typically indicates PUID/PGID mismatch - update docker-compose.yml to match lancache container ownership.",
                            existingFile);
                        return false;
                    }
                    catch (IOException ex)
                    {
                        // File might be locked, try the next one
                        _logger.LogDebug(ex, "File locked or inaccessible, trying next: {Path}", existingFile);
                        continue;
                    }
                }

                // BUG 8 FIX: All sampled files were locked - log a warning before falling back
                _logger.LogWarning(
                    "All {Count} sampled files in {Path} were locked/inaccessible. " +
                    "Falling back to create-test, which may not detect PUID/PGID mismatches on existing cache files. " +
                    "This can happen when the cache is under heavy load.",
                    existingFiles.Count, directoryPath);
            }
            else
            {
                _logger.LogDebug("No existing files found in {Path}, using create test", directoryPath);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            // Can't even enumerate files - definitely no access
            _logger.LogWarning(ex, "Cannot enumerate files in directory (permission denied): {Path}", directoryPath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking existing files in {Path}, falling back to create test", directoryPath);
        }

        // Step 2: Fallback - test if we can create/delete files in the directory
        // This is needed for empty directories and validates directory-level write access
        var testFilePath = Path.Combine(directoryPath, $".lancache_permcheck_{Guid.NewGuid():N}");

        try
        {
            File.WriteAllText(testFilePath, "permission check");
            File.Delete(testFilePath);
            _logger.LogDebug("Create/delete test passed in {Path}", directoryPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogDebug("Directory is read-only (cannot create files): {Path}", directoryPath);
            return false;
        }
        catch (IOException)
        {
            _logger.LogDebug("Directory is read-only or inaccessible: {Path}", directoryPath);
            return false;
        }
    }
}
