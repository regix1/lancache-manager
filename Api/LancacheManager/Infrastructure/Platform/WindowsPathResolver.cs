namespace LancacheManager.Infrastructure.Platform;

/// <summary>
/// Windows-specific path resolver that finds the correct project directories
/// </summary>
public class WindowsPathResolver : PathResolverBase
{
    private readonly string _basePath;

    public WindowsPathResolver(ILogger<WindowsPathResolver> logger) : base(logger)
    {
        _basePath = FindProjectRoot();
    }

    protected override string BasePath => _basePath;

    protected override string RustExecutableExtension => ".exe";

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

        // For relative paths, combine with base path
        var fullPath = Path.Combine(_basePath, relativePath);
        return NormalizePath(fullPath);
    }

    /// <summary>
    /// Normalizes path separators for the current platform (Windows)
    /// </summary>
    public override string NormalizePath(string path)
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
    /// Checks if the Docker socket is available for container communication.
    /// On Windows (development only), always returns true since nginx log rotation
    /// is only relevant in Docker/Linux production environments.
    /// </summary>
    public override bool IsDockerSocketAvailable()
    {
        // Windows is development-only, Docker socket is a Linux/Docker concept
        // Return true to allow testing of features that would require docker socket in production
        Logger.LogDebug("Docker socket check skipped on Windows (development environment)");
        return true;
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
}
