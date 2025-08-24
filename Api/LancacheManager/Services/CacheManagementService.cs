using LancacheManager.Models;

namespace LancacheManager.Services;

public class CacheManagementService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CacheManagementService> _logger;
    private readonly string _cachePath;
    private readonly string _logPath;

    public CacheManagementService(IConfiguration configuration, ILogger<CacheManagementService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        // The actual nginx cache path
        _cachePath = configuration["LanCache:CachePath"] ?? "/mnt/cache/cache";
        _logPath = configuration["LanCache:LogPath"] ?? "/logs/access.log";
    }

    public CacheInfo GetCacheInfo()
    {
        var info = new CacheInfo();

        try
        {
            // Get the parent directory for drive info
            var driveRoot = Path.GetPathRoot(_cachePath) ?? "/";
            if (Directory.Exists(driveRoot))
            {
                var driveInfo = new DriveInfo(driveRoot);
                info.TotalCacheSize = driveInfo.TotalSize;
                info.FreeCacheSize = driveInfo.AvailableFreeSpace;
                info.UsedCacheSize = info.TotalCacheSize - info.FreeCacheSize;
                
                _logger.LogDebug($"Cache info: Total={info.TotalCacheSize}, Used={info.UsedCacheSize}, Free={info.FreeCacheSize}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache info");
        }

        return info;
    }

    public async Task ClearAllCache()
    {
        try
        {
            _logger.LogInformation($"Clearing all cache from: {_cachePath}");
            
            if (!Directory.Exists(_cachePath))
            {
                _logger.LogWarning($"Cache path does not exist: {_cachePath}");
                return;
            }

            // Delete all subdirectories (00-ff) in the nginx cache
            await Task.Run(() =>
            {
                var dirs = Directory.GetDirectories(_cachePath);
                var totalDirs = dirs.Length;
                var processed = 0;
                
                foreach (var dir in dirs)
                {
                    try
                    {
                        // Delete the directory and all its contents
                        Directory.Delete(dir, true);
                        
                        // Recreate the empty directory (nginx expects these to exist)
                        Directory.CreateDirectory(dir);
                        
                        processed++;
                        if (processed % 10 == 0)
                        {
                            _logger.LogInformation($"Cleared {processed}/{totalDirs} cache directories");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to clear directory: {dir}");
                    }
                }
                
                _logger.LogInformation($"Cache cleared: {processed} directories");
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing cache");
            throw;
        }
    }

    public async Task RemoveServiceFromLogs(string service)
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                _logger.LogWarning($"Log file not found: {_logPath}");
                return;
            }

            var tempFile = $"{_logPath}.tmp";
            var backupFile = $"{_logPath}.bak";
            
            _logger.LogInformation($"Removing {service} entries from log file");
            
            await Task.Run(async () =>
            {
                // Create backup
                File.Copy(_logPath, backupFile, true);
                
                using (var reader = new StreamReader(_logPath))
                using (var writer = new StreamWriter(tempFile))
                {
                    string? line;
                    int removedCount = 0;
                    int totalLines = 0;
                    
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        totalLines++;
                        
                        // Skip lines that start with [service]
                        if (!line.StartsWith($"[{service}]", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync(line);
                        }
                        else
                        {
                            removedCount++;
                            if (removedCount % 1000 == 0)
                            {
                                _logger.LogDebug($"Removed {removedCount} {service} entries");
                            }
                        }
                    }
                    
                    _logger.LogInformation($"Removed {removedCount} {service} entries from {totalLines} total lines");
                }
                
                // Replace original with filtered version
                File.Delete(_logPath);
                File.Move(tempFile, _logPath);
                
                _logger.LogInformation($"Log file updated. Backup saved as {backupFile}");
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error removing {service} from logs");
            throw;
        }
    }

    public async Task<Dictionary<string, long>> GetServiceLogCounts()
    {
        var counts = new Dictionary<string, long>();
        
        try
        {
            if (!File.Exists(_logPath))
            {
                return counts;
            }

            await Task.Run(async () =>
            {
                using (var reader = new StreamReader(_logPath))
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        // Extract service name from line
                        if (line.StartsWith("[") && line.IndexOf(']') > 0)
                        {
                            var endIndex = line.IndexOf(']');
                            var service = line.Substring(1, endIndex - 1).ToLower();
                            
                            if (!counts.ContainsKey(service))
                                counts[service] = 0;
                            
                            counts[service]++;
                        }
                    }
                }
            });
            
            _logger.LogInformation($"Log counts: {string.Join(", ", counts.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error counting service logs");
        }
        
        return counts;
    }

    // Compatibility method for old interface
    public async Task ClearCache(string? service)
    {
        if (string.IsNullOrEmpty(service))
        {
            await ClearAllCache();
        }
        else
        {
            // For specific service, remove from logs instead
            await RemoveServiceFromLogs(service);
        }
    }
}