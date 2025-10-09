namespace LancacheManager.Services;

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
            _logger.LogDebug("Running in container, using root as base path");
        }
        else
        {
            _basePath = FindProjectRoot();
        }

        _logger.LogDebug("Linux base path resolved to: {BasePath}", _basePath);
    }

    public string GetBasePath() => _basePath;

    public string GetDataDirectory() => Path.GetFullPath(Path.Combine(_basePath, "data"));

    public string GetLogsDirectory() => Path.GetFullPath(Path.Combine(_basePath, "logs"));

    public string GetCacheDirectory() => Path.GetFullPath(Path.Combine(_basePath, "cache"));

    public string GetThemesDirectory() => Path.GetFullPath(Path.Combine(GetDataDirectory(), "themes"));

    public string GetRustLogProcessorPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "lancache_processor");

    public string GetRustDatabaseResetPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "database_reset");

    public string GetRustLogManagerPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "log_manager");

    public string GetRustCacheCleanerPath() =>
        Path.Combine(AppContext.BaseDirectory, "rust-processor", "cache_cleaner");

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

        _logger.LogDebug("Starting project root search from: {CurrentDir}", currentDir);

        // Quick check: if we're in Api/LancacheManager, go up two levels
        if (currentDir.EndsWith("/Api/LancacheManager", StringComparison.OrdinalIgnoreCase))
        {
            var projectRoot = Directory.GetParent(currentDir)?.Parent?.FullName;
            if (projectRoot != null && IsValidProjectRoot(projectRoot))
            {
                _logger.LogDebug("Found project root via Api/LancacheManager pattern: {Root}", projectRoot);
                return projectRoot;
            }
        }

        // Quick check: if we're in the Api directory, go up one level
        if (currentDir.EndsWith("/Api", StringComparison.OrdinalIgnoreCase))
        {
            var projectRoot = Directory.GetParent(currentDir)?.FullName;
            if (projectRoot != null && IsValidProjectRoot(projectRoot))
            {
                _logger.LogDebug("Found project root via Api pattern: {Root}", projectRoot);
                return projectRoot;
            }
        }

        // Search up the directory tree
        var dir = new DirectoryInfo(currentDir);
        while (dir != null)
        {
            if (IsValidProjectRoot(dir.FullName))
            {
                _logger.LogDebug("Found project root via tree search: {Root}", dir.FullName);
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
                        _logger.LogDebug("Found project root via bin search: {Root}", parent.FullName);
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error validating project root: {Path}", path);
            return false;
        }
    }
}