using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Controller for browsing the server filesystem to locate database files
/// Used primarily for DeveLanCacheUI import feature
/// Security: Paths can be restricted via Security:AllowedBrowsePaths configuration
/// </summary>
[ApiController]
[Route("api/filebrowser")]
public class FileBrowserController : ControllerBase
{
    private readonly ILogger<FileBrowserController> _logger;
    private readonly IConfiguration _configuration;
    private readonly List<string> _allowedPaths;

    public FileBrowserController(ILogger<FileBrowserController> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;

        // Parse allowed paths from configuration
        // Format: comma-separated list of paths, e.g., "/data,/mnt,C:\data"
        // Default: /data and /mnt for common Docker mount points
        var allowedPathsConfig = _configuration["Security:AllowedBrowsePaths"];
        if (string.IsNullOrWhiteSpace(allowedPathsConfig))
        {
            // Default to /data and /mnt - common Docker mount points for databases
            _allowedPaths = new List<string> { "/data", "/mnt" };
            _logger.LogInformation("FileBrowser: Using default allowed paths /data and /mnt. Configure Security:AllowedBrowsePaths to customize.");
        }
        else
        {
            _allowedPaths = allowedPathsConfig
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => Path.GetFullPath(p))
                .ToList();
        }
    }

    /// <summary>
    /// Check if a path is within allowed directories
    /// </summary>
    private bool IsPathAllowed(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return _allowedPaths.Any(allowed =>
            fullPath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// GET /api/filebrowser/list - List contents of a directory
    /// Query params: path (optional, defaults to common locations)
    /// </summary>
    [HttpGet("list")]
    public IActionResult ListDirectory([FromQuery] string? path)
    {
        try
        {
            // If no path provided, return common locations (filtered by allowed paths)
            if (string.IsNullOrWhiteSpace(path))
            {
                return Ok(new DirectoryListResponse
                {
                    CurrentPath = "/",
                    ParentPath = null,
                    Items = GetCommonLocations()
                });
            }

            // Security: Validate path is within allowed directories
            if (!IsPathAllowed(path))
            {
                _logger.LogWarning("Attempted access to restricted path: {Path}", path);
                return StatusCode(403, new ErrorResponse { Error = "Access to this path is not allowed" });
            }

            // Validate path exists
            if (!Directory.Exists(path))
            {
                return BadRequest(new ErrorResponse { Error = "Directory does not exist" });
            }

            var directoryInfo = new DirectoryInfo(path);
            var items = new List<FileSystemItemDto>();

            // Get directories
            try
            {
                var directories = directoryInfo.GetDirectories()
                    .OrderBy(d => d.Name);

                foreach (var dir in directories)
                {
                    try
                    {
                        items.Add(new FileSystemItemDto
                        {
                            Name = dir.Name,
                            Path = dir.FullName,
                            IsDirectory = true,
                            LastModified = dir.LastWriteTime,
                            IsAccessible = true
                        });
                    }
                    catch (UnauthorizedAccessException)
                    {
                        items.Add(new FileSystemItemDto
                        {
                            Name = dir.Name,
                            Path = dir.FullName,
                            IsDirectory = true,
                            LastModified = DateTime.MinValue,
                            IsAccessible = false
                        });
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("Access denied to directory: {Path}", path);
            }

            // Get .db files only
            try
            {
                var files = directoryInfo.GetFiles("*.db")
                    .OrderBy(f => f.Name);

                foreach (var file in files)
                {
                    try
                    {
                        items.Add(new FileSystemItemDto
                        {
                            Name = file.Name,
                            Path = file.FullName,
                            IsDirectory = false,
                            Size = file.Length,
                            LastModified = file.LastWriteTime,
                            IsAccessible = true
                        });
                    }
                    catch (UnauthorizedAccessException)
                    {
                        items.Add(new FileSystemItemDto
                        {
                            Name = file.Name,
                            Path = file.FullName,
                            IsDirectory = false,
                            Size = 0,
                            LastModified = DateTime.MinValue,
                            IsAccessible = false
                        });
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("Access denied to files in directory: {Path}", path);
            }

            // Get parent directory - only if it's within allowed paths
            // If parent is outside allowed paths, set to null so UI navigates to home
            string? parentPath = null;
            if (directoryInfo.Parent != null)
            {
                var parentFullPath = directoryInfo.Parent.FullName;
                if (IsPathAllowed(parentFullPath))
                {
                    parentPath = parentFullPath;
                }
                // else: parentPath stays null, meaning "back" should go to home/root
            }

            return Ok(new DirectoryListResponse
            {
                CurrentPath = path,
                ParentPath = parentPath,
                Items = items
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied to path: {Path}", path);
            return StatusCode(403, new ErrorResponse { Error = "Access denied to this directory" });
        }
    }

    /// <summary>
    /// Get allowed locations where databases might be located
    /// Only returns paths that are explicitly configured in AllowedBrowsePaths
    /// </summary>
    private List<FileSystemItemDto> GetCommonLocations()
    {
        var locations = new List<FileSystemItemDto>();

        // Only show configured allowed paths - no other locations
        foreach (var allowedPath in _allowedPaths)
        {
            if (Directory.Exists(allowedPath))
            {
                try
                {
                    var dirInfo = new DirectoryInfo(allowedPath);
                    locations.Add(new FileSystemItemDto
                    {
                        Name = allowedPath,
                        Path = allowedPath,
                        IsDirectory = true,
                        LastModified = dirInfo.LastWriteTime,
                        IsAccessible = true
                    });
                }
                catch
                {
                    locations.Add(new FileSystemItemDto
                    {
                        Name = allowedPath,
                        Path = allowedPath,
                        IsDirectory = true,
                        LastModified = DateTime.MinValue,
                        IsAccessible = false
                    });
                }
            }
            else
            {
                // Show the allowed path even if it doesn't exist (marked as inaccessible)
                locations.Add(new FileSystemItemDto
                {
                    Name = allowedPath,
                    Path = allowedPath,
                    IsDirectory = true,
                    LastModified = DateTime.MinValue,
                    IsAccessible = false
                });
            }
        }

        return locations;
    }

    /// <summary>
    /// GET /api/filebrowser/search - Search for .db files
    /// Query params: searchPath (directory to search in), pattern (filename pattern)
    /// Special case: searchPath="/" searches within all allowed paths
    /// </summary>
    [HttpGet("search")]
    public IActionResult SearchDatabases([FromQuery] string searchPath, [FromQuery] string? pattern = "*.db")
    {
        try
        {
            var results = new List<FileSystemItemDto>();
            var searchPattern = string.IsNullOrWhiteSpace(pattern) ? "*.db" : pattern;

            // Special case: root path "/" - search within all allowed paths
            if (string.IsNullOrWhiteSpace(searchPath) || searchPath == "/")
            {
                foreach (var allowedPath in _allowedPaths)
                {
                    if (Directory.Exists(allowedPath))
                    {
                        SearchDirectory(allowedPath, searchPattern, results, 0, 3);
                    }
                }

                return Ok(new FileSearchResponse
                {
                    SearchPath = "/",
                    Pattern = searchPattern,
                    Results = results.OrderBy(r => r.Path).ToList()
                });
            }

            // Security: Validate path is within allowed directories
            if (!IsPathAllowed(searchPath))
            {
                _logger.LogWarning("Attempted search in restricted path: {Path}", searchPath);
                return StatusCode(403, new ErrorResponse { Error = "Access to this path is not allowed" });
            }

            if (!Directory.Exists(searchPath))
            {
                return BadRequest(new ErrorResponse { Error = "Search path does not exist" });
            }

            // Search up to 3 levels deep to avoid long searches
            SearchDirectory(searchPath, searchPattern, results, 0, 3);

            return Ok(new FileSearchResponse
            {
                SearchPath = searchPath,
                Pattern = searchPattern,
                Results = results.OrderBy(r => r.Path).ToList()
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied during search: {Path}", searchPath);
            return StatusCode(403, new ErrorResponse { Error = "Access denied" });
        }
    }

    private void SearchDirectory(string path, string pattern, List<FileSystemItemDto> results, int currentDepth, int maxDepth)
    {
        if (currentDepth >= maxDepth)
            return;

        try
        {
            var dirInfo = new DirectoryInfo(path);

            // Add matching files
            var files = dirInfo.GetFiles(pattern);
            foreach (var file in files)
            {
                results.Add(new FileSystemItemDto
                {
                    Name = file.Name,
                    Path = file.FullName,
                    IsDirectory = false,
                    Size = file.Length,
                    LastModified = file.LastWriteTime,
                    IsAccessible = true
                });
            }

            // Recursively search subdirectories
            var directories = dirInfo.GetDirectories();
            foreach (var dir in directories)
            {
                try
                {
                    SearchDirectory(dir.FullName, pattern, results, currentDepth + 1, maxDepth);
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip directories we can't access
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip if we can't access this directory
        }
    }
}
