using LancacheManager.Models;
using LancacheManager.Constants;

namespace LancacheManager.Services;

public class CacheManagementService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CacheManagementService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly string _cachePath;
    private readonly string _logPath;

    public CacheManagementService(IConfiguration configuration, ILogger<CacheManagementService> logger, IPathResolver pathResolver)
    {
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;

        // Use PathResolver to get properly resolved paths
        var configCachePath = configuration["LanCache:CachePath"];
        _cachePath = !string.IsNullOrEmpty(configCachePath)
            ? _pathResolver.ResolvePath(configCachePath)
            : _pathResolver.GetCacheDirectory();

        var configLogPath = configuration["LanCache:LogPath"];
        _logPath = !string.IsNullOrEmpty(configLogPath)
            ? _pathResolver.ResolvePath(configLogPath)
            : Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");

        // Check if cache directory exists, create if it doesn't
        if (!Directory.Exists(_cachePath))
        {
            try
            {
                Directory.CreateDirectory(_cachePath);
                _logger.LogInformation($"Created cache directory: {_cachePath}");
            }
            catch (Exception ex)
            {
                var errorMsg = $"Failed to create cache directory: {_cachePath}. Error: {ex.Message}";
                _logger.LogError(ex, errorMsg);
                // Don't throw - allow the service to start but log the error
                // The cache operations will fail appropriately when attempted
            }
        }

        _logger.LogInformation($"CacheManagementService initialized - Cache: {_cachePath}, Logs: {_logPath}");
    }

    public CacheInfo GetCacheInfo()
    {
        var info = new CacheInfo();

        try
        {
            // For Windows development, skip drive info
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                _logger.LogDebug("Running on Windows, skipping drive info");
                return info;
            }

            // Find the actual mount point for the cache directory
            var mountPoint = GetMountPoint(_cachePath);
            _logger.LogInformation($"Cache path: {_cachePath}, Mount point: {mountPoint}");
            
            if (Directory.Exists(mountPoint))
            {
                var driveInfo = new DriveInfo(mountPoint);
                info.TotalCacheSize = driveInfo.TotalSize;
                info.FreeCacheSize = driveInfo.AvailableFreeSpace;
                info.UsedCacheSize = info.TotalCacheSize - info.FreeCacheSize;
                
                _logger.LogDebug($"Drive info for {mountPoint}: Total={info.TotalCacheSize}, Used={info.UsedCacheSize}, Free={info.FreeCacheSize}");
            }
            else
            {
                _logger.LogWarning($"Mount point does not exist: {mountPoint}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache info");
        }

        return info;
    }

    private string GetMountPoint(string path)
    {
        try
        {
            // Read /proc/mounts to find the correct mount point
            if (File.Exists("/proc/mounts"))
            {
                var mounts = File.ReadAllLines("/proc/mounts");
                var bestMatch = "/";
                var bestMatchLength = 0;

                foreach (var mount in mounts)
                {
                    var parts = mount.Split(' ');
                    if (parts.Length >= 2)
                    {
                        var mountPoint = parts[1];
                        // Check if this mount point is a parent of our path
                        if (path.StartsWith(mountPoint) && mountPoint.Length > bestMatchLength)
                        {
                            bestMatch = mountPoint;
                            bestMatchLength = mountPoint.Length;
                        }
                    }
                }

                _logger.LogDebug($"Best mount point for {path}: {bestMatch}");
                return bestMatch;
            }
            
            // Fallback: check if the cache path is a mount point
            var cachePath = _pathResolver.GetCacheDirectory();
            if (path.StartsWith(cachePath))
            {
                if (Directory.Exists(cachePath) && new DriveInfo(cachePath).TotalSize > 0)
                    return cachePath;
            }
            
            // Last resort: use root
            return "/";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Error detecting mount point for {path}, using root");
        }

        return "/";
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

            // Get all cache directories (00-ff)
            var dirs = Directory.GetDirectories(_cachePath)
                .Where(d => {
                    var name = Path.GetFileName(d);
                    return name.Length == 2 && IsHex(name);
                })
                .ToList();
            
            if (dirs.Count == 0)
            {
                _logger.LogWarning($"No cache directories found in {_cachePath}");
                throw new InvalidOperationException($"No cache directories (00-ff) found in {_cachePath}. Is this the correct cache path?");
            }
            
            _logger.LogInformation($"Found {dirs.Count} cache directories to clear");

            // Delete all subdirectories (00-ff) in the nginx cache
            await Task.Run(() =>
            {
                var totalDirs = dirs.Count;
                var processed = 0;
                var filesDeleted = 0;
                
                foreach (var dir in dirs)
                {
                    try
                    {
                        // Count files before deletion
                        var fileCount = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
                        filesDeleted += fileCount;
                        
                        // Delete all files in the directory but keep the directory structure
                        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            try
                            {
                                File.Delete(file);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, $"Failed to delete file: {file}");
                            }
                        }
                        
                        // Delete subdirectories but keep the main directory (00-ff)
                        var subdirs = Directory.GetDirectories(dir);
                        foreach (var subdir in subdirs)
                        {
                            try
                            {
                                Directory.Delete(subdir, true);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, $"Failed to delete subdirectory: {subdir}");
                            }
                        }
                        
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
                
                _logger.LogInformation($"Cache cleared: {processed} directories processed, {filesDeleted} files deleted");
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing cache");
            throw;
        }
    }
    
    private bool IsHex(string value)
    {
        return value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }

    public async Task RemoveServiceFromLogs(string service)
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                throw new FileNotFoundException($"Log file not found: {_logPath}");
            }

            var logDir = Path.GetDirectoryName(_logPath) ?? _pathResolver.GetLogsDirectory();
            var backupFile = $"{_logPath}.bak";
            var tempFile = Path.Combine(logDir, $"access.log.tmp.{Guid.NewGuid()}");
            
            // Test write permissions by trying to create a temp file
            try
            {
                await File.WriteAllTextAsync(Path.Combine(logDir, ".write_test"), "test");
                File.Delete(Path.Combine(logDir, ".write_test"));
            }
            catch (Exception ex)
            {
                var errorMsg = $"Cannot write to logs directory: {logDir}. " +
                              "This may indicate: 1) Directory is mounted read-only, 2) Permission issues, 3) Directory doesn't exist. " +
                              "Please ensure the logs directory exists and has write permissions.";
                _logger.LogError(ex, errorMsg);
                throw new UnauthorizedAccessException(errorMsg, ex);
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
                            
                            // Skip IP addresses (anything with dots) and localhost
                            if (service.Contains(".") || service == "127")
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
            // First, get all services that have counts
            var serviceCounts = await GetServiceLogCounts();
            foreach (var service in serviceCounts.Keys)
            {
                if (!string.IsNullOrEmpty(service) && !service.Contains("."))
                {
                    services.Add(service);
                }
            }
            
            // If we found services from counts, return them
            if (services.Count > 0)
            {
                _logger.LogInformation($"Found services from counts: {string.Join(", ", services)}");
                return services.OrderBy(s => s).ToList();
            }
            
            // Fallback: scan log file if no counts found
            if (!File.Exists(_logPath))
            {
                _logger.LogWarning($"Log file not found: {_logPath}");
                return new List<string> { "steam", "epic", "origin", "blizzard", "wsus", "riot" };
            }

            await Task.Run(async () =>
            {
                using (var reader = new StreamReader(_logPath))
                {
                    string? line;
                    int linesChecked = 0;
                    int maxLinesToCheck = 50000; // Increased from 10000 to scan more lines
                    
                    while ((line = await reader.ReadLineAsync()) != null && linesChecked < maxLinesToCheck)
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
                    
                    _logger.LogInformation($"Found services by scanning {linesChecked} lines: {string.Join(", ", services)}");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning for services");
            return new List<string> { "steam", "epic", "origin", "blizzard", "wsus", "riot" };
        }
        
        // If no services found, return defaults
        if (services.Count == 0)
        {
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