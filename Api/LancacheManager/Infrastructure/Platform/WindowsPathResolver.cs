using LancacheManager.Core.Interfaces.Services;

namespace LancacheManager.Infrastructure.Platform;

/// <summary>
/// Windows-specific path resolver that finds the correct project directories
/// </summary>
public class WindowsPathResolver : IPathResolver
{
    private readonly ILogger<WindowsPathResolver> _logger;
    private readonly string _basePath;

    public WindowsPathResolver(ILogger<WindowsPathResolver> logger)
    {
        _logger = logger;
        _basePath = FindProjectRoot();
    }

    public string GetBasePath() => _basePath;

    public string GetDataDirectory() => Path.GetFullPath(Path.Combine(_basePath, "data"));

    public string GetLogsDirectory() => Path.GetFullPath(Path.Combine(_basePath, "logs"));

    public string GetCacheDirectory() => Path.GetFullPath(Path.Combine(_basePath, "cache"));

    public string GetDevicesDirectory() => Path.GetFullPath(Path.Combine(GetDataDirectory(), "devices"));

    public string GetThemesDirectory() => Path.GetFullPath(Path.Combine(GetDataDirectory(), "themes"));

    public string GetCachedImagesDirectory() => Path.GetFullPath(Path.Combine(GetDataDirectory(), "cached-img"));

    public string GetOperationsDirectory()
    {
        var path = Path.GetFullPath(Path.Combine(GetDataDirectory(), "operations"));
        Directory.CreateDirectory(path); // Ensure directory exists
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
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "log_processor.exe");

    public string GetRustDatabaseResetPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "db_reset.exe");

    public string GetRustLogManagerPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "log_service_manager.exe");

    public string GetRustCacheCleanerPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "cache_clear.exe");

    public string GetRustCacheSizePath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "cache_size.exe");

    public string GetRustCorruptionManagerPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "cache_corruption.exe");

    public string GetRustGameDetectorPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "cache_game_detect.exe");

    public string GetRustGameRemoverPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "cache_game_remove.exe");

    public string GetRustServiceRemoverPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "cache_service_remove.exe");

    public string GetRustDataMigratorPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "db_migrate.exe");

    public string GetRustSpeedTrackerPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "speed_tracker.exe");

    public string GetDatabasePath() =>
        Path.Combine(GetDataDirectory(), "LancacheManager.db");

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

        // For relative paths, combine with base path
        var fullPath = Path.Combine(_basePath, relativePath);
        return NormalizePath(fullPath);
    }

    /// <summary>
    /// Normalizes path separators for the current platform (Windows)
    /// </summary>
    public string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        // Replace all separators with Windows backslash
        var normalized = path.Replace('/', '\\');

        // Remove duplicate separators
        while (normalized.Contains("\\\\"))
        {
            normalized = normalized.Replace("\\\\", "\\");
        }

        return normalized;
    }

    /// <summary>
    /// Finds the project root directory by looking for the Api and Web folders
    /// </summary>
    private string FindProjectRoot()
    {
        var currentDir = Directory.GetCurrentDirectory();

        // Normalize path separators for Windows
        currentDir = currentDir.Replace('/', '\\');

        // Quick check: if we're in Api\LancacheManager, go up two levels
        if (currentDir.EndsWith("\\Api\\LancacheManager", StringComparison.OrdinalIgnoreCase))
        {
            var projectRoot = Directory.GetParent(currentDir)?.Parent?.FullName;
            if (projectRoot != null && IsValidProjectRoot(projectRoot))
            {
                return projectRoot;
            }
        }

        // Quick check: if we're in the Api directory, go up one level
        if (currentDir.EndsWith("\\Api", StringComparison.OrdinalIgnoreCase))
        {
            var projectRoot = Directory.GetParent(currentDir)?.FullName;
            if (projectRoot != null && IsValidProjectRoot(projectRoot))
            {
                return projectRoot;
            }
        }

        // Search up the directory tree
        var dir = new DirectoryInfo(currentDir);
        while (dir != null)
        {
            if (IsValidProjectRoot(dir.FullName))
            {
                return dir.FullName;
            }

            // Handle bin directory cases (for development builds)
            if (dir.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                dir.FullName.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            {
                var parent = dir.Parent;
                while (parent != null)
                {
                    if (IsValidProjectRoot(parent.FullName))
                    {
                        return parent.FullName;
                    }
                    parent = parent.Parent;
                }
            }

            dir = dir.Parent;
        }

        // If we can't find the project root, throw an exception
        throw new DirectoryNotFoundException($"Could not find project root directory from: {currentDir}");
    }

    /// <summary>
    /// Validates that a directory is the project root by checking for expected subdirectories
    /// </summary>
    private bool IsValidProjectRoot(string path)
    {
        try
        {
            return Directory.Exists(Path.Combine(path, "Api")) &&
                   Directory.Exists(Path.Combine(path, "Web"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a directory is writable
    /// Windows uses test file creation method
    /// </summary>
    public bool IsDirectoryWritable(string directoryPath)
    {
        try
        {
            // Check if directory exists
            if (!Directory.Exists(directoryPath))
            {
                _logger.LogWarning("Directory does not exist: {Path}", directoryPath);
                return false;
            }

            // CRITICAL: We need to test TWO things:
            // 1. Can we create new files in the directory? (directory write access)
            // 2. Can we modify EXISTING files? (file ownership/permission check)
            //
            // The second check catches permission mismatches where the container can create
            // new files but cannot modify files owned by another user.

            // First, try to find and test against an existing file in the directory
            try
            {
                var existingFiles = Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly)
                    .Take(5) // Check up to 5 files to find one we can test
                    .ToList();

                foreach (var existingFile in existingFiles)
                {
                    try
                    {
                        // Try to open the file for write access WITHOUT modifying it
                        using (var fs = new FileStream(existingFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                        {
                            // Successfully opened for write - we have permission
                            _logger.LogDebug("Existing file write test passed for: {Path}", existingFile);
                            return true;
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Cannot modify this existing file - permission issue
                        _logger.LogDebug("Cannot modify existing file (permission denied): {Path}", existingFile);
                        return false;
                    }
                    catch (IOException)
                    {
                        // File might be locked, try the next one
                        continue;
                    }
                }

                // No existing files found or all were locked, fall back to create test
                _logger.LogDebug("No existing files found in {Path}, falling back to create test", directoryPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error checking existing files in {Path}, falling back to create test", directoryPath);
            }

            // Fallback: Test if we can at least create new files
            var testFilePath = Path.Combine(directoryPath, $".write_test_{Guid.NewGuid()}.tmp");

            try
            {
                File.WriteAllText(testFilePath, "write test");
                File.Delete(testFilePath);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogDebug("Directory is read-only: {Path}", directoryPath);
                return false;
            }
            catch (IOException)
            {
                _logger.LogDebug("Directory is read-only or inaccessible: {Path}", directoryPath);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing write access to directory: {Path}", directoryPath);
            return false;
        }
    }

    /// <summary>
    /// Checks if the cache directory is writable
    /// </summary>
    public bool IsCacheDirectoryWritable() => IsDirectoryWritable(GetCacheDirectory());

    /// <summary>
    /// Checks if the logs directory is writable
    /// </summary>
    public bool IsLogsDirectoryWritable() => IsDirectoryWritable(GetLogsDirectory());

    /// <summary>
    /// Checks if the Docker socket is available for container communication.
    /// On Windows (development only), always returns true since nginx log rotation
    /// is only relevant in Docker/Linux production environments.
    /// </summary>
    public bool IsDockerSocketAvailable()
    {
        // Windows is development-only, Docker socket is a Linux/Docker concept
        // Return true to allow testing of features that would require docker socket in production
        _logger.LogDebug("Docker socket check skipped on Windows (development environment)");
        return true;
    }
}
