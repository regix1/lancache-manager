using LancacheManager.Models;

namespace LancacheManager.Services;

public class CacheManagementService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CacheManagementService> _logger;

    public CacheManagementService(IConfiguration configuration, ILogger<CacheManagementService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public CacheInfo GetCacheInfo()
    {
        var cachePath = _configuration["LanCache:CachePath"] ?? "/cache";
        var info = new CacheInfo();

        try
        {
            if (Directory.Exists(cachePath))
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(cachePath) ?? "/");
                info.TotalCacheSize = driveInfo.TotalSize;
                info.FreeCacheSize = driveInfo.AvailableFreeSpace;
                info.UsedCacheSize = info.TotalCacheSize - info.FreeCacheSize;

                // Get service sizes
                foreach (var dir in Directory.GetDirectories(cachePath))
                {
                    try
                    {
                        var dirName = Path.GetFileName(dir);
                        // Skip hidden directories and numeric directories
                        if (dirName.StartsWith(".") || dirName.All(char.IsDigit))
                            continue;
                            
                        var size = GetDirectorySize(dir);
                        if (size > 0)
                        {
                            info.ServiceSizes[dirName] = size;
                            info.TotalFiles += CountFiles(dir);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Could not get size for {dir}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache info");
        }

        return info;
    }

    private long GetDirectorySize(string path)
    {
        try
        {
            long size = 0;
            var dir = new DirectoryInfo(path);
            
            foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    size += file.Length;
                }
                catch { }
            }
            
            return size;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Could not calculate size for {path}");
            return 0;
        }
    }

    private int CountFiles(string path)
    {
        try
        {
            return Directory.GetFiles(path, "*", SearchOption.AllDirectories).Length;
        }
        catch
        {
            return 0;
        }
    }

    public async Task ClearCache(string? service)
    {
        var cachePath = _configuration["LanCache:CachePath"] ?? "/cache";
        
        try
        {
            if (!string.IsNullOrEmpty(service))
            {
                var servicePath = Path.Combine(cachePath, service);
                if (Directory.Exists(servicePath))
                {
                    await Task.Run(() => DeleteDirectoryContents(servicePath));
                    _logger.LogInformation($"Cleared cache for service: {service}");
                }
            }
            else
            {
                // Clear all services
                var tasks = Directory.GetDirectories(cachePath)
                    .Select(dir => Task.Run(() => DeleteDirectoryContents(dir)))
                    .ToArray();
                    
                await Task.WhenAll(tasks);
                _logger.LogInformation("Cleared all cache");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing cache");
            throw;
        }
    }

    private void DeleteDirectoryContents(string path)
    {
        try
        {
            // Use system command for better performance and handling
            if (OperatingSystem.IsLinux())
            {
                // Use rm -rf for Linux (more reliable)
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/bin/rm",
                    Arguments = $"-rf \"{path}/*\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                process?.WaitForExit();
                
                // Recreate the directory
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
            }
            else
            {
                // Windows or fallback
                var dir = new DirectoryInfo(path);
                foreach (var file in dir.GetFiles())
                {
                    try { file.Delete(); } catch { }
                }
                foreach (var subDir in dir.GetDirectories())
                {
                    try { subDir.Delete(true); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error deleting contents of {path}");
        }
    }
}