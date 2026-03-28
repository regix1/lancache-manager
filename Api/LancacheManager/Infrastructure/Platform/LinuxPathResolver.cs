namespace LancacheManager.Infrastructure.Platform;

/// <summary>
/// Linux-specific path resolver that finds the correct project directories
/// </summary>
public class LinuxPathResolver : PathResolverBase
{
    private readonly string _basePath;

    public LinuxPathResolver(ILogger<LinuxPathResolver> logger) : base(logger)
    {
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

    protected override string BasePath => _basePath;

    protected override string RustExecutableExtension => "";

    /// <summary>
    /// Resolves a relative path to an absolute path based on the operating system
    /// </summary>
    public override string ResolvePath(string relativePath)
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
    public override string NormalizePath(string path)
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
    /// Checks if a directory is writable.
    /// On Linux, checks /proc/mounts for read-only flag first, then falls back to base TestWriteAccess.
    /// </summary>
    public override bool IsDirectoryWritable(string directoryPath)
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
    /// Checks if the Docker socket is available for container communication.
    /// Required for nginx log rotation after log/cache manipulation operations.
    /// </summary>
    public override bool IsDockerSocketAvailable()
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
}
