using System.Security.Cryptography;
using System.Text;
using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Platform;

/// <summary>
/// Outcome of a directory write-access probe. Distinguishes a deliberately read-only mount
/// from an ownership/mode denial so callers can log an accurate, low-noise reason.
/// </summary>
public enum DirectoryWriteAccess
{
    /// <summary>The directory accepts writes.</summary>
    Writable,

    /// <summary>The directory does not exist.</summary>
    DirectoryMissing,

    /// <summary>The path is mounted read-only, so writes are disabled by design.</summary>
    ReadOnlyMount,

    /// <summary>Writes are denied by ownership or file mode (commonly a PUID/PGID mismatch).</summary>
    OwnershipOrModeDenied,

    /// <summary>Write access could not be determined.</summary>
    Indeterminate
}

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

    public string GetStructuralCorruptionStateDirectory()
    {
        var path = Path.GetFullPath(Path.Combine(GetStateDirectory(), "corruption-structural"));
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetStructuralCorruptionStateScope(string datasourceName, string cachePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);

        var normalizedName = datasourceName.Trim().ToLowerInvariant();
        var normalizedRoot = Path.GetFullPath(cachePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            normalizedRoot = normalizedRoot.ToUpperInvariant();
        }

        var identity = $"structural-state-v1\n{normalizedName}\n{normalizedRoot}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    public string GetStructuralCorruptionStateDatabasePath(string datasourceName, string cachePath) =>
        Path.Combine(
            GetStructuralCorruptionStateDirectory(),
            $"{GetStructuralCorruptionStateScope(datasourceName, cachePath)}.sqlite3");

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

    public string GetImagesDirectory() => Path.GetFullPath(Path.Combine(GetDataDirectory(), "cache", "images"));

    public string GetOperationsDirectory()
    {
        var path = Path.GetFullPath(Path.Combine(GetDataDirectory(), "operations"));
        Directory.CreateDirectory(path);
        return path;
    }

    public int CleanupOperationFiles(int maxAgeHours = 24)
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

    public int MigrateOperationFiles()
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

    public string GetRustSteamRemoverPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"cache_steam_remove{RustExecutableExtension}");

    public string GetRustEpicRemoverPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"cache_epic_remove{RustExecutableExtension}");

    public string GetRustNamedGameRemoverPath(string service)
    {
        // Each name-keyed service has its own thin binary over the shared named-removal core.
        // The service string is the owning Downloads.Service identity ("blizzard"/"riot"/"xbox").
        var binary = (service ?? string.Empty).ToLowerInvariant() switch
        {
            "blizzard" => "cache_blizzard_remove",
            "riot" => "cache_riot_remove",
            "xbox" => "cache_xbox_remove",
            _ => throw new ArgumentException(
                $"No named-game removal binary registered for service '{service}'.", nameof(service))
        };
        return Path.Combine(AppContext.BaseDirectory, "rust-processor", $"{binary}{RustExecutableExtension}");
    }

    public string GetRustServiceRemoverPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"cache_service_remove{RustExecutableExtension}");

    public string GetRustEvictionScanPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"cache_eviction_scan{RustExecutableExtension}");

    public string GetRustLogPurgePath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", $"cache_purge_log_entries{RustExecutableExtension}");

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
    /// Checks if a directory is writable. Base implementation samples existing files then runs a create-test.
    /// Linux overrides <see cref="GetDirectoryWriteAccess"/> to add a /proc/mounts read-only check.
    /// </summary>
    public virtual bool IsDirectoryWritable(string directoryPath)
        => GetDirectoryWriteAccess(directoryPath) == DirectoryWriteAccess.Writable;

    /// <summary>
    /// Determines write access and the reason for any denial. Repeat, unchanged denials are logged at
    /// debug here; a single human-facing warning is emitted by the caller only when the state transitions.
    /// </summary>
    public virtual DirectoryWriteAccess GetDirectoryWriteAccess(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                _logger.LogDebug("Directory does not exist: {Path}", directoryPath);
                return DirectoryWriteAccess.DirectoryMissing;
            }

            return EvaluateWriteAccess(directoryPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing write access to directory: {Path}", directoryPath);
            return DirectoryWriteAccess.Indeterminate;
        }
    }

    public bool IsCacheWritable() => IsDirectoryWritable(GetCacheDirectory());

    public bool IsLogsWritable() => IsDirectoryWritable(GetLogsDirectory());

    public abstract bool IsDockerSocketAvailable();

    /// <summary>
    /// Finds a small sample of files near the directory root.
    /// Avoids recursive enumeration, which can block for minutes on large cache trees.
    /// </summary>
    protected List<string> FindSampleFiles(string directoryPath, int maxFiles = 5)
    {
        var results = new List<string>(maxFiles);

        try
        {
            foreach (var file in Directory.EnumerateFiles(directoryPath))
            {
                results.Add(file);
                if (results.Count >= maxFiles)
                {
                    return results;
                }
            }

            foreach (var subDirectory in Directory.EnumerateDirectories(directoryPath))
            {
                foreach (var file in Directory.EnumerateFiles(subDirectory))
                {
                    results.Add(file);
                    if (results.Count >= maxFiles)
                    {
                        return results;
                    }
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Shallow file sample failed for {Directory}; caller falls back to create/delete test", directoryPath);
        }

        return results;
    }

    /// <summary>
    /// Probes actual write access by opening existing files first, then falling back to a create-test,
    /// and reports the reason for any denial. All outcomes are logged at debug: this runs on a fast poll,
    /// so callers own the single, throttled, human-facing warning on a state transition.
    /// </summary>
    protected DirectoryWriteAccess EvaluateWriteAccess(string directoryPath)
    {
        // Strategy: Test ACTUAL write access by opening existing files for write.
        // This is more reliable than UID/GID comparison because it:
        // - Works on all architectures (ARM64, x86_64)
        // - Handles ACLs, group permissions, and root correctly
        // - Tests real write ability, not just ownership

        // Step 1: Try to find and test a shallow sample of existing files
        try
        {
            var existingFiles = FindSampleFiles(directoryPath);

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
                            return DirectoryWriteAccess.Writable;
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Write is denied by ownership or file mode (commonly a PUID/PGID mismatch).
                        // Debug-level because this repeats every poll while the state is unchanged;
                        // the caller reports it once on the writable -> read-only transition.
                        _logger.LogDebug("Write access denied by ownership or file mode on existing file: {Path}", existingFile);
                        return DirectoryWriteAccess.OwnershipOrModeDenied;
                    }
                    catch (IOException ex)
                    {
                        // File might be locked, try the next one
                        _logger.LogDebug(ex, "File locked or inaccessible, trying next: {Path}", existingFile);
                        continue;
                    }
                }

                // All sampled files were locked; fall back to the create-test. Debug-level because this can
                // recur every poll under heavy cache load and is not an actionable permission problem.
                _logger.LogDebug(
                    "All {Count} sampled files in {Path} were locked/inaccessible; falling back to create-test.",
                    existingFiles.Count, directoryPath);
            }
            else
            {
                _logger.LogDebug("No existing files found in {Path}, using create test", directoryPath);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Cannot even enumerate the directory: write is denied by ownership or file mode.
            _logger.LogDebug("Cannot enumerate files (write access denied by ownership or file mode): {Path}", directoryPath);
            return DirectoryWriteAccess.OwnershipOrModeDenied;
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
            return DirectoryWriteAccess.Writable;
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogDebug("Directory is not writable, cannot create files: {Path}", directoryPath);
            return DirectoryWriteAccess.OwnershipOrModeDenied;
        }
        catch (IOException)
        {
            _logger.LogDebug("Directory is not writable or inaccessible: {Path}", directoryPath);
            return DirectoryWriteAccess.OwnershipOrModeDenied;
        }
    }
}
