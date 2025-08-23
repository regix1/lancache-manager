using LancacheManager.Models;

namespace LancacheManager.Services;

public class CacheManagementService
{
    private readonly IConfiguration _configuration;

    public CacheManagementService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public CacheInfo GetCacheInfo()
    {
        var cachePath = _configuration["LanCache:CachePath"] ?? "C:/temp/lancache/cache";
        var info = new CacheInfo();

        // Create directory if it doesn't exist (for testing)
        if (!Directory.Exists(cachePath))
        {
            Directory.CreateDirectory(cachePath);
            
            // Create sample service directories for testing
            var services = new[] { "steam", "epic", "origin", "blizzard", "wsus" };
            foreach (var service in services)
            {
                Directory.CreateDirectory(Path.Combine(cachePath, service));
            }
        }

        if (Directory.Exists(cachePath))
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(cachePath)!);
            info.TotalCacheSize = driveInfo.TotalSize;
            info.FreeCacheSize = driveInfo.AvailableFreeSpace;
            info.UsedCacheSize = info.TotalCacheSize - info.FreeCacheSize;

            // Get service-specific sizes
            var serviceDirs = Directory.GetDirectories(cachePath);
            foreach (var dir in serviceDirs)
            {
                var dirInfo = new DirectoryInfo(dir);
                var size = GetDirectorySize(dirInfo);
                info.ServiceSizes[Path.GetFileName(dir)] = size;
                info.TotalFiles += Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
            }
        }

        return info;
    }

    private long GetDirectorySize(DirectoryInfo dir)
    {
        try
        {
            return dir.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<bool> ClearCache(string? service = null)
    {
        var cachePath = _configuration["LanCache:CachePath"] ?? "C:/temp/lancache/cache";
        
        try
        {
            if (service != null)
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
                // Clear all cache
                var dirs = Directory.GetDirectories(cachePath);
                foreach (var dir in dirs)
                {
                    Directory.Delete(dir, true);
                    Directory.CreateDirectory(dir);
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}