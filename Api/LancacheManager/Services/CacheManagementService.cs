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

                // Get service sizes - look for directories that look like services
                var serviceDirs = new[] { "steam", "epic", "origin", "blizzard", "uplay", "riot", "wsus" };
                
                foreach (var service in serviceDirs)
                {
                    var servicePath = Path.Combine(cachePath, service);
                    if (Directory.Exists(servicePath))
                    {
                        try
                        {
                            var size = GetDirectorySize(servicePath);
                            if (size > 0)
                            {
                                info.ServiceSizes[service] = size;
                                info.TotalFiles += CountFiles(servicePath);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"Could not get size for {service}");
                        }
                    }
                }

                // Also check for any other directories that might be cache
                foreach (var dir in Directory.GetDirectories(cachePath))
                {
                    var dirName = Path.GetFileName(dir);
                    // Skip if already processed or if it's a system directory
                    if (!serviceDirs.Contains(dirName) && !dirName.StartsWith(".") && !dirName.All(char.IsDigit))
                    {
                        try
                        {
                            var size = GetDirectorySize(dir);
                            if (size > 0)
                            {
                                info.ServiceSizes[dirName] = size;
                                info.TotalFiles += CountFiles(dir);
                            }
                        }
                        catch { }
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
            
            // Use parallel processing for large directories
            var files = dir.GetFiles("*", SearchOption.AllDirectories);
            size = files.Sum(f => f.Length);
            
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
                    await Task.Run(() => {
                        Directory.Delete(servicePath, true);
                        Directory.CreateDirectory(servicePath);
                    });
                    _logger.LogInformation($"Cleared cache for service: {service}");
                }
            }
            else
            {
                // Clear all known service directories
                var services = new[] { "steam", "epic", "origin", "blizzard", "uplay", "riot", "wsus" };
                foreach (var svc in services)
                {
                    var servicePath = Path.Combine(cachePath, svc);
                    if (Directory.Exists(servicePath))
                    {
                        await Task.Run(() => {
                            Directory.Delete(servicePath, true);
                            Directory.CreateDirectory(servicePath);
                        });
                    }
                }
                _logger.LogInformation("Cleared all cache");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing cache");
            throw;
        }
    }
}