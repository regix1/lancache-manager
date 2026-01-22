using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using LancacheManager.Core.Interfaces.Services;

namespace LancacheManager.Infrastructure.Platform;

/// <summary>
/// Linux-specific path resolver that finds the correct project directories
/// </summary>
public class LinuxPathResolver : IPathResolver
{
    private readonly ILogger<LinuxPathResolver> _logger;
    private readonly string _basePath;



    public LinuxPathResolver(ILogger<LinuxPathResolver> logger)
    {
        _logger = logger;

        // In Docker/production, use root directory
        // In development, find the project root
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
        {
            _basePath = "/";
        }
        else
        {
            _basePath = FindProjectRoot();
        }

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
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "log_processor");

    public string GetRustDatabaseResetPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "db_reset");

    public string GetRustLogManagerPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "log_service_manager");

    public string GetRustCacheCleanerPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "cache_clear");

    public string GetRustCacheSizePath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "cache_size");

    public string GetRustCorruptionManagerPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "cache_corruption");

    public string GetRustGameDetectorPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "cache_game_detect");

    public string GetRustGameRemoverPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "cache_game_remove");

    public string GetRustServiceRemoverPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "cache_service_remove");

    public string GetRustDataMigratorPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "db_migrate");

    public string GetRustSpeedTrackerPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "speed_tracker");

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

        // For relative paths, combine with base path (root for Linux)
        var fullPath = relativePath.StartsWith("/") ? relativePath : Path.Combine(_basePath, relativePath);
        return NormalizePath(fullPath);
    }

    /// <summary>
    /// Normalizes path separators for the current platform (Linux)
    /// </summary>
    public string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        // Replace all separators with Linux forward slash
        var normalized = path.Replace('\\', '/');

        // Remove duplicate separators
        while (normalized.Contains("//"))
        {
            normalized = normalized.Replace("//", "/");
        }

        return normalized;
    }

    /// <summary>
    /// Finds the project root directory by looking for the Api and Web folders
    /// </summary>
    private string FindProjectRoot()
    {
        var currentDir = Directory.GetCurrentDirectory();


        // Quick check: if we're in Api/LancacheManager, go up two levels
        if (currentDir.EndsWith("/Api/LancacheManager", StringComparison.OrdinalIgnoreCase))
        {
            var projectRoot = Directory.GetParent(currentDir)?.Parent?.FullName;
            if (projectRoot != null && IsValidProjectRoot(projectRoot))
            {
                return projectRoot;
            }
        }

        // Quick check: if we're in the Api directory, go up one level
        if (currentDir.EndsWith("/Api", StringComparison.OrdinalIgnoreCase))
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
                dir.FullName.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
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
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a directory is writable
    /// On Linux, checks /proc/mounts for read-only flag first, then falls back to test file
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

            // Check /proc/mounts for read-only mount flag (Linux-specific optimization)
            // IMPORTANT: Only short-circuit on READ-ONLY mounts. For read-write mounts,
            // we MUST still run TestWriteAccess to verify actual permissions (PUID/PGID).
            var isReadOnlyMount = CheckLinuxReadOnlyMount(directoryPath);
            if (isReadOnlyMount.HasValue && isReadOnlyMount.Value)
            {
                // Mount is definitely read-only - no need to test further
                _logger.LogDebug("Directory is mounted read-only (from /proc/mounts): {Path}", directoryPath);
                return false;
            }

            // Mount is read-write (or unknown) - test actual write permissions
            // This is critical for detecting PUID/PGID mismatches
            return TestWriteAccess(directoryPath);
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
    /// Required for nginx log rotation after log/cache manipulation operations.
    /// </summary>
    public bool IsDockerSocketAvailable()
    {
        try
        {
            // Check if docker socket exists at the standard location
            if (File.Exists("/var/run/docker.sock"))
            {
                _logger.LogDebug("Docker socket found at /var/run/docker.sock");
                return true;
            }

            _logger.LogDebug("Docker socket not found at /var/run/docker.sock");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking Docker socket availability");
            return false;
        }
    }

    /// <summary>
    /// Checks if directory is mounted read-only by reading /proc/mounts
    /// </summary>
    private bool? CheckLinuxReadOnlyMount(string directoryPath)
    {
        try
        {
            if (!File.Exists("/proc/mounts"))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(directoryPath);
            var mounts = File.ReadAllLines("/proc/mounts");

            // Find the longest matching mount point (most specific)
            string? matchingMount = null;
            string? matchingOptions = null;

            foreach (var line in mounts)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;

                var mountPoint = parts[1];
                var mountOptions = parts[3];

                // Check if directory is under this mount point
                if (fullPath.StartsWith(mountPoint) && (matchingMount == null || mountPoint.Length > matchingMount.Length))
                {
                    matchingMount = mountPoint;
                    matchingOptions = mountOptions;
                }
            }

            if (matchingOptions != null)
            {
                // Check if 'ro' is in the mount options
                var options = matchingOptions.Split(',');
                var isReadOnly = options.Contains("ro");
                _logger.LogDebug("Mount options for {Path}: {Options} (read-only: {IsReadOnly})",
                    directoryPath, matchingOptions, isReadOnly);
                return isReadOnly;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check Linux mount options for {Path}", directoryPath);
            return null;
        }
    }

    [DllImport("libc")]
    private static extern uint getuid();

    private uint? GetProcessUid()
    {
        var envUid = Environment.GetEnvironmentVariable("LANCACHE_PUID") ??
                     Environment.GetEnvironmentVariable("PUID") ??
                     Environment.GetEnvironmentVariable("UID");

        if (uint.TryParse(envUid, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedUid))
        {
            return parsedUid;
        }

        try
        {
            return getuid();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to determine process UID");
            return null;
        }
    }

    private bool TryGetPathOwnerUid(string path, out uint ownerUid)
    {
        ownerUid = 0;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "stat",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("%u");
            startInfo.ArgumentList.Add(path);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(2000))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Ignore failures when killing a hung process
                }

                _logger.LogDebug("Timed out running stat for path: {Path}", path);
                return false;
            }

            if (process.ExitCode != 0)
            {
                _logger.LogDebug("stat failed for {Path} (exit {Code}): {Error}", path, process.ExitCode, error);
                return false;
            }

            var trimmed = output.Trim();
            if (uint.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out ownerUid))
            {
                return true;
            }

            _logger.LogDebug("Unable to parse stat output '{Output}' for {Path}", trimmed, path);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to determine owner UID for {Path}", path);
            return false;
        }
    }

    private void LogPuidMismatch(uint ownerUid, uint processUid)
    {
        _logger.LogWarning(
            "Directory is owned by UID {OwnerUid} but process runs as UID {ProcessUid}. " +
            "Set PUID={Puid} in docker-compose.yml to match the lancache container.",
            ownerUid, processUid, ownerUid);
    }

    /// <summary>
    /// Tests write access by opening existing files first, then falling back to create/delete
    /// </summary>
    private bool TestWriteAccess(string directoryPath)
    {
        // Strategy: Test ACTUAL write access by opening existing files for write.
        // This is more reliable than UID/GID comparison because it:
        // - Works on all architectures (ARM64, x86_64)
        // - Handles ACLs, group permissions, and root correctly
        // - Tests real write ability, not just ownership

        // Step 1: Try to find and test existing files in the directory tree
        // Cache directories have hierarchical structure (e.g., /cache/steam/...) so search recursively
        var processUid = GetProcessUid();

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

                // All files were locked/inaccessible, fall back to create test
                _logger.LogDebug("All existing files locked in {Path}, falling back to create test", directoryPath);
            }
            else
            {
                _logger.LogDebug("No existing files found in {Path}, checking ownership before create test", directoryPath);

                if (TryGetPathOwnerUid(directoryPath, out var ownerUid))
                {
                    if (processUid.HasValue)
                    {
                        if (ownerUid != processUid.Value)
                        {
                            LogPuidMismatch(ownerUid, processUid.Value);
                            return false;
                        }

                        if (ownerUid == processUid.Value)
                        {
                            _logger.LogDebug("Directory owner UID matches process UID {ProcessUid} for {Path}", processUid.Value, directoryPath);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("Process UID not available; skipping ownership check for {Path}", directoryPath);
                    }
                }
                else
                {
                    _logger.LogDebug("Could not determine directory owner UID for {Path}; skipping ownership check", directoryPath);
                }
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
