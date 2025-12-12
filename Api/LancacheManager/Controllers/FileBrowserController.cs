using LancacheManager.Application.DTOs;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Controller for browsing the server filesystem to locate database files
/// Used primarily for DeveLanCacheUI import feature
/// </summary>
[ApiController]
[Route("api/filebrowser")]
public class FileBrowserController : ControllerBase
{
    private readonly ILogger<FileBrowserController> _logger;

    public FileBrowserController(ILogger<FileBrowserController> logger)
    {
        _logger = logger;
    }


    /// <summary>
    /// GET /api/filebrowser/list - List contents of a directory
    /// Query params: path (optional, defaults to common locations)
    /// </summary>
    [HttpGet("list")]
    [RequireAuth]
    public IActionResult ListDirectory([FromQuery] string? path)
    {
        try
        {
            // If no path provided, return common locations
            if (string.IsNullOrWhiteSpace(path))
            {
                return Ok(new DirectoryListResponse
                {
                    CurrentPath = "/",
                    ParentPath = null,
                    Items = GetCommonLocations()
                });
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

            // Get parent directory
            string? parentPath = null;
            if (directoryInfo.Parent != null)
            {
                parentPath = directoryInfo.Parent.FullName;
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
    /// Get common locations where DeveLanCacheUI databases might be located
    /// </summary>
    private List<FileSystemItemDto> GetCommonLocations()
    {
        var locations = new List<FileSystemItemDto>();
        var commonPaths = new List<string>();

        // Detect OS and add appropriate paths
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            commonPaths.AddRange(new[]
            {
                "/data",
                "/mnt",
                "/var/lib",
                "/opt",
                "/home",
                "/app",
                "/config"
            });
        }
        else if (OperatingSystem.IsWindows())
        {
            commonPaths.AddRange(new[]
            {
                "C:\\data",
                "C:\\ProgramData",
                "C:\\Users",
                "D:\\",
                "H:\\"
            });
        }
        else if (OperatingSystem.IsMacOS())
        {
            commonPaths.AddRange(new[]
            {
                "/Users",
                "/Applications",
                "/Volumes"
            });
        }

        // Add current directory
        try
        {
            var currentDir = Directory.GetCurrentDirectory();
            locations.Add(new FileSystemItemDto
            {
                Name = "Current Directory",
                Path = currentDir,
                IsDirectory = true,
                LastModified = Directory.GetLastWriteTime(currentDir),
                IsAccessible = true
            });
        }
        catch { /* Ignore */ }

        // Add root
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() || OperatingSystem.IsMacOS())
        {
            locations.Add(new FileSystemItemDto
            {
                Name = "Root (/)",
                Path = "/",
                IsDirectory = true,
                LastModified = DateTime.MinValue,
                IsAccessible = true
            });
        }
        else if (OperatingSystem.IsWindows())
        {
            // Add drive letters
            try
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .OrderBy(d => d.Name);

                foreach (var drive in drives)
                {
                    locations.Add(new FileSystemItemDto
                    {
                        Name = $"{drive.Name} ({drive.VolumeLabel})",
                        Path = drive.RootDirectory.FullName,
                        IsDirectory = true,
                        LastModified = DateTime.MinValue,
                        IsAccessible = true
                    });
                }
            }
            catch { /* Ignore */ }
        }

        // Add common locations that exist
        foreach (var path in commonPaths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    var dirInfo = new DirectoryInfo(path);
                    locations.Add(new FileSystemItemDto
                    {
                        Name = $"Common: {path}",
                        Path = path,
                        IsDirectory = true,
                        LastModified = dirInfo.LastWriteTime,
                        IsAccessible = true
                    });
                }
                catch
                {
                    locations.Add(new FileSystemItemDto
                    {
                        Name = $"Common: {path}",
                        Path = path,
                        IsDirectory = true,
                        LastModified = DateTime.MinValue,
                        IsAccessible = false
                    });
                }
            }
        }

        return locations;
    }

    /// <summary>
    /// GET /api/filebrowser/search - Search for .db files
    /// Query params: searchPath (directory to search in), pattern (filename pattern)
    /// </summary>
    [HttpGet("search")]
    [RequireAuth]
    public IActionResult SearchDatabases([FromQuery] string searchPath, [FromQuery] string? pattern = "*.db")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(searchPath))
            {
                searchPath = "/";
            }

            if (!Directory.Exists(searchPath))
            {
                return BadRequest(new ErrorResponse { Error = "Search path does not exist" });
            }

            var results = new List<FileSystemItemDto>();
            var searchPattern = string.IsNullOrWhiteSpace(pattern) ? "*.db" : pattern;

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
