using LancacheManager.Models;

namespace LancacheManager.Services;

public class CacheManagementService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CacheManagementService> _logger;
    private readonly string _cachePath;

    public CacheManagementService(IConfiguration configuration, ILogger<CacheManagementService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _cachePath = configuration["LanCache:CachePath"] ?? "/cache";
    }

    public CacheInfo GetCacheInfo()
    {
        var info = new CacheInfo();

        try
        {
            if (Directory.Exists(_cachePath))
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(_cachePath) ?? "/");
                info.TotalCacheSize = driveInfo.TotalSize;
                info.FreeCacheSize = driveInfo.AvailableFreeSpace;
                info.UsedCacheSize = info.TotalCacheSize - info.FreeCacheSize;

                // Get service sizes - look for known service directories
                var serviceDirs = new[] { "steam", "epic", "origin", "blizzard", "uplay", "riot", "wsus" };
                
                foreach (var service in serviceDirs)
                {
                    var servicePath = Path.Combine(_cachePath, service);
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

                // Check for any other cache directories
                foreach (var dir in Directory.GetDirectories(_cachePath))
                {
                    var dirName = Path.GetFileName(dir);
                    // Skip if already processed or if it's a system directory
                    if (!serviceDirs.Contains(dirName) && !dirName.StartsWith("."))
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
            else
            {
                _logger.LogWarning($"Cache path does not exist: {_cachePath}");
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
            
            // Get all files recursively
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Could not count files in {path}");
            return 0;
        }
    }

    public async Task ClearCache(string? service)
    {
        try
        {
            if (!string.IsNullOrEmpty(service))
            {
                var servicePath = Path.Combine(_cachePath, service);
                if (Directory.Exists(servicePath))
                {
                    await Task.Run(() => 
                    {
                        Directory.Delete(servicePath, true);
                        Directory.CreateDirectory(servicePath);
                    });
                    _logger.LogInformation($"Cleared cache for service: {service}");
                }
                else
                {
                    _logger.LogWarning($"Service cache directory not found: {servicePath}");
                }
            }
            else
            {
                // Clear all known service directories
                var services = new[] { "steam", "epic", "origin", "blizzard", "uplay", "riot", "wsus" };
                foreach (var svc in services)
                {
                    var servicePath = Path.Combine(_cachePath, svc);
                    if (Directory.Exists(servicePath))
                    {
                        await Task.Run(() => 
                        {
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
            _logger.LogError(ex, $"Error clearing cache for {service ?? "all services"}");
            throw;
        }
    }
}