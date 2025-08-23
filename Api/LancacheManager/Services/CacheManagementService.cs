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
                    var dirName = Path.GetFileName(dir);
                    var size = GetDirectorySize(dir);
                    info.ServiceSizes[dirName] = size;
                    info.TotalFiles += Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
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
            var dir = new DirectoryInfo(path);
            return dir.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    public Task ClearCache(string? service)
    {
        var cachePath = _configuration["LanCache:CachePath"] ?? "/cache";
        
        try
        {
            if (!string.IsNullOrEmpty(service))
            {
                var servicePath = Path.Combine(cachePath, service);
                if (Directory.Exists(servicePath))
                {
                    Directory.Delete(servicePath, true);
                    Directory.CreateDirectory(servicePath);
                }
            }
            else
            {
                foreach (var dir in Directory.GetDirectories(cachePath))
                {
                    Directory.Delete(dir, true);
                    Directory.CreateDirectory(dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing cache");
        }
        
        return Task.CompletedTask;
    }
}