using LancacheManager.Models;
using LancacheManager.Constants;
using System.Diagnostics;
using System.Text.Json;

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
                    return name.Length == 2 && LancacheConstants.IsHex(name);
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

    public async Task RemoveServiceFromLogs(string service)
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                throw new FileNotFoundException($"Log file not found: {_logPath}");
            }

            var logDir = Path.GetDirectoryName(_logPath) ?? _pathResolver.GetLogsDirectory();

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

            // Use Rust binary for fast log filtering
            var dataDir = _pathResolver.GetDataDirectory();

            // Ensure data directory exists
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
                _logger.LogInformation($"Created data directory: {dataDir}");
            }

            var progressFile = Path.Combine(dataDir, "log_remove_progress.json");
            var rustBinaryPath = _pathResolver.GetRustLogManagerPath();

            // Check if Rust binary exists
            if (!File.Exists(rustBinaryPath))
            {
                var errorMsg = $"Rust log_manager binary not found at {rustBinaryPath}. Please ensure the Rust binaries are built.";
                _logger.LogError(errorMsg);
                throw new FileNotFoundException(errorMsg);
            }

            _logger.LogInformation($"Using Rust binary for log filtering: {rustBinaryPath}");

            var startInfo = new ProcessStartInfo
            {
                FileName = rustBinaryPath,
                Arguments = $"remove \"{_logPath}\" \"{service}\" \"{progressFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new Exception("Failed to start Rust log_manager process");
                }

                // Read stdout and stderr asynchronously to prevent buffer deadlock
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

                var output = await outputTask;
                var error = await errorTask;

                if (process.ExitCode != 0)
                {
                    throw new Exception($"Rust log_manager failed with exit code {process.ExitCode}: {error}");
                }

                _logger.LogInformation($"Rust log filtering completed: {output}");
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogDebug($"Rust stderr: {error}");
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Re-throw with clear message
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error removing {service} from logs with Rust binary");
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
                _logger.LogWarning($"Log file not found: {_logPath}");
                return counts;
            }

            // Use Rust binary for fast log counting
            var dataDir = _pathResolver.GetDataDirectory();

            // Ensure data directory exists
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
                _logger.LogInformation($"Created data directory: {dataDir}");
            }

            var progressFile = Path.Combine(dataDir, "log_count_progress.json");
            var rustBinaryPath = _pathResolver.GetRustLogManagerPath();

            // Check if Rust binary exists
            if (!File.Exists(rustBinaryPath))
            {
                var errorMsg = $"Rust log_manager binary not found at {rustBinaryPath}. Please ensure the Rust binaries are built.";
                _logger.LogError(errorMsg);
                throw new FileNotFoundException(errorMsg);
            }

            _logger.LogInformation($"Using Rust binary for log counting: {rustBinaryPath}");

            var startInfo = new ProcessStartInfo
            {
                FileName = rustBinaryPath,
                Arguments = $"count \"{_logPath}\" \"{progressFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new Exception("Failed to start Rust log_manager process");
                }

                // Read stdout and stderr asynchronously to prevent buffer deadlock
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

                var output = await outputTask;
                var error = await errorTask;

                if (process.ExitCode != 0)
                {
                    throw new Exception($"Rust log_manager failed with exit code {process.ExitCode}: {error}");
                }

                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogDebug($"Rust stderr: {error}");
                }

                // Read results from progress file
                if (File.Exists(progressFile))
                {
                    // Use FileStream with FileShare.ReadWrite to allow other processes to access the file
                    string json;
                    using (var fileStream = new FileStream(progressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fileStream))
                    {
                        json = await reader.ReadToEndAsync();
                    }

                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };
                    var progressData = JsonSerializer.Deserialize<LogCountProgressData>(json, options);

                    if (progressData?.ServiceCounts != null)
                    {
                        counts = progressData.ServiceCounts.ToDictionary(kvp => kvp.Key, kvp => (long)kvp.Value);
                        _logger.LogInformation($"Rust log counting completed: Found {counts.Count} services");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // If the log file doesn't exist, return empty counts instead of throwing
            if (ex.Message.Contains("No such file or directory") || ex.Message.Contains("os error 2"))
            {
                _logger.LogWarning($"Log file not accessible: {_logPath}. Returning empty service counts.");
                return counts;
            }

            _logger.LogError(ex, "Error counting service logs with Rust binary");
            throw;
        }

        return counts;
    }

    // Helper class for deserializing Rust progress data
    private class LogCountProgressData
    {
        public bool IsProcessing { get; set; }
        public double PercentComplete { get; set; }
        public string Status { get; set; } = "";
        public string Message { get; set; } = "";
        public ulong LinesProcessed { get; set; }
        public Dictionary<string, ulong>? ServiceCounts { get; set; }
    }

    // Get list of unique services from logs
    public async Task<List<string>> GetServicesFromLogs()
    {
        try
        {
            // Get all services that have counts (uses Rust binary for fast counting)
            var serviceCounts = await GetServiceLogCounts();
            var services = serviceCounts.Keys
                .Where(service => !string.IsNullOrEmpty(service) && !service.Contains("."))
                .OrderBy(s => s)
                .ToList();

            if (services.Count > 0)
            {
                _logger.LogInformation($"Found services from counts: {string.Join(", ", services)}");
                return services;
            }

            // If no services found, return empty list
            _logger.LogWarning("No services found in log file");
            return new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting services from logs");
            return new List<string>();
        }
    }
}