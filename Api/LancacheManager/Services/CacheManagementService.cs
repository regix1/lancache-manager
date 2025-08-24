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
                // Just get drive info - this is instant
                var driveInfo = new DriveInfo(Path.GetPathRoot(_cachePath) ?? "/");
                info.TotalCacheSize = driveInfo.TotalSize;
                info.FreeCacheSize = driveInfo.AvailableFreeSpace;
                info.UsedCacheSize = info.TotalCacheSize - info.FreeCacheSize;

                // Don't scan directories - just check which services exist
                var serviceDirs = new[] { "steam", "epic", "origin", "blizzard", "uplay", "riot", "wsus" };
                
                foreach (var service in serviceDirs)
                {
                    var servicePath = Path.Combine(_cachePath, service);
                    if (Directory.Exists(servicePath))
                    {
                        // Just mark that the service exists, don't calculate size
                        info.ServiceSizes[service] = -1; // -1 indicates "exists but size unknown"
                    }
                }
                
                // Set a placeholder for total files
                info.TotalFiles = -1; // -1 indicates "unknown"
                
                _logger.LogDebug($"Cache info: Total={info.TotalCacheSize}, Used={info.UsedCacheSize}, Free={info.FreeCacheSize}");
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