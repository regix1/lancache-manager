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
        // Read from configuration
        _cachePath = configuration["LanCache:CachePath"] ?? "/mnt/cache/cache";
        _logPath = configuration["LanCache:LogPath"] ?? "/logs/access.log";
        
        _logger.LogInformation($"CacheManagementService initialized - Cache: {_cachePath}, Logs: {_logPath}");
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

    public string GetCachePath()
    {
        return _cachePath;
    }

    public async Task ClearAllCache()
    {
        try
        {
            _logger.LogInformation($"Clearing all cache from: {_cachePath}");
            
            if (!Directory.Exists(_cachePath))
            {
                _logger.LogWarning($"Cache path does not exist: {_cachePath}");
                throw new DirectoryNotFoundException($"Cache path does not exist: {_cachePath}");
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
                throw new FileNotFoundException($"Log file not found: {_logPath}");
            }

            var logDir = Path.GetDirectoryName(_logPath) ?? "/logs";
            var backupFile = $"{_logPath}.bak";
            var tempFile = Path.Combine(logDir, $"access.log.tmp.{Guid.NewGuid()}");
            
            // Test write permissions by trying to create a temp file
            try
            {
                await File.WriteAllTextAsync(Path.Combine(logDir, ".write_test"), "test");
                File.Delete(Path.Combine(logDir, ".write_test"));
            }
            catch (Exception)
            {
                // Try /tmp as fallback
                tempFile = $"/tmp/access.log.tmp.{Guid.NewGuid()}";
                var tempBackup = $"/tmp/access.log.bak.{DateTime.Now:yyyyMMddHHmmss}";
                
                _logger.LogWarning($"Cannot write to {logDir}, using /tmp for processing");
                
                // Process in /tmp then try to copy back
                await Task.Run(async () =>
                {
                    // Create backup in /tmp
                    File.Copy(_logPath, tempBackup, true);
                    _logger.LogInformation($"Backup created at {tempBackup}");
                    
                    using (var reader = new StreamReader(_logPath))
                    using (var writer = new StreamWriter(tempFile))
                    {
                        string? line;
                        int removedCount = 0;
                        int totalLines = 0;
                        
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            totalLines++;
                            
                            if (!line.StartsWith($"[{service}]", StringComparison.OrdinalIgnoreCase))
                            {
                                await writer.WriteLineAsync(line);
                            }
                            else
                            {
                                removedCount++;
                            }
                        }
                        
                        _logger.LogInformation($"Removed {removedCount} {service} entries from {totalLines} total lines");
                    }
                });
                
                // Try to replace the original file
                try
                {
                    File.Delete(_logPath);
                    File.Move(tempFile, _logPath);
                    _logger.LogInformation("Successfully updated log file");
                }
                catch
                {
                    throw new UnauthorizedAccessException(
                        $"Cannot modify {_logPath}. The logs directory may be mounted read-only or have permission issues. " +
                        $"Processed file saved to {tempFile}. To fix: 1) Check docker logs, 2) Ensure logs mount has write permissions, " +
                        $"3) Try running: docker exec lancache-manager chmod 777 /logs"
                    );
                }
                
                return;
            }
            
            _logger.LogInformation($"Removing {service} entries from log file");
            
            await Task.Run(async () =>
            {
                // Create backup
                File.Copy(_logPath, backupFile, true);
                _logger.LogInformation($"Backup created at {backupFile}");
                
                using (var reader = new StreamReader(_logPath))
                using (var writer = new StreamWriter(tempFile))
                {
                    string? line;
                    int removedCount = 0;
                    int totalLines = 0;
                    
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        totalLines++;
                        
                        if (!line.StartsWith($"[{service}]", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync(line);
                        }
                        else
                        {
                            removedCount++;
                            if (removedCount % 10000 == 0)
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
                
                _logger.LogInformation($"Log file updated successfully");
            });
        }
        catch (UnauthorizedAccessException)
        {
            // Re-throw with clear message
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error removing {service} from logs");
            throw new Exception($"Failed to remove {service} from logs: {ex.Message}", ex);
        }
    }

    public async Task<Dictionary<string, long>> GetServiceLogCounts()
    {
        var counts = new Dictionary<string, long>();
        
        try
        {
            if (!File.Exists(_logPath))
            {
                _logger.LogWarning($"Log file not found: {_logPath}");
                return counts;
            }

            await Task.Run(async () =>
            {
                using (var reader = new StreamReader(_logPath))
                {
                    string? line;
                    var serviceSet = new HashSet<string>();
                    
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        // Extract service name from line
                        if (line.StartsWith("[") && line.IndexOf(']') > 0)
                        {
                            var endIndex = line.IndexOf(']');
                            var service = line.Substring(1, endIndex - 1).ToLower();
                            
                            // Skip localhost entries
                            if (service == "127.0.0.1")
                                continue;
                            
                            if (!counts.ContainsKey(service))
                            {
                                counts[service] = 0;
                                serviceSet.Add(service);
                            }
                            
                            counts[service]++;
                        }
                    }
                    
                    _logger.LogInformation($"Found {serviceSet.Count} services in logs: {string.Join(", ", serviceSet)}");
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

    // Get list of unique services from logs
    public async Task<List<string>> GetServicesFromLogs()
    {
        var services = new HashSet<string>();
        
        try
        {
            if (!File.Exists(_logPath))
            {
                _logger.LogWarning($"Log file not found: {_logPath}");
                // Return common services as fallback
                return new List<string> { "steam", "epic", "origin", "blizzard", "wsus", "riot" };
            }

            await Task.Run(async () =>
            {
                using (var reader = new StreamReader(_logPath))
                {
                    string? line;
                    int linesChecked = 0;
                    
                    // Check first 10000 lines to get service list quickly
                    while ((line = await reader.ReadLineAsync()) != null && linesChecked < 10000)
                    {
                        linesChecked++;
                        
                        if (line.StartsWith("[") && line.IndexOf(']') > 0)
                        {
                            var endIndex = line.IndexOf(']');
                            var service = line.Substring(1, endIndex - 1).ToLower();
                            
                            // Skip IP addresses and localhost
                            if (!service.Contains(".") && service != "127")
                            {
                                services.Add(service);
                            }
                        }
                    }
                    
                    _logger.LogInformation($"Found services: {string.Join(", ", services)}");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning for services");
            // Return common services as fallback
            return new List<string> { "steam", "epic", "origin", "blizzard", "wsus", "riot" };
        }
        
        return services.OrderBy(s => s).ToList();
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