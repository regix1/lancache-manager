using LancacheManager.Models;
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

    // Cache for service log counts - prevents repeated Rust binary executions
    private Dictionary<string, long>? _cachedServiceCounts;
    private DateTime? _cacheExpiry;
    private readonly TimeSpan _cacheValidDuration = TimeSpan.FromMinutes(5); // Cache valid for 5 minutes
    private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
    private DateTime? _lastLogWarningTime; // Track last time we logged a warning
    private readonly TimeSpan _logWarningThrottle = TimeSpan.FromMinutes(5); // Only log warnings every 5 minutes

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

    /// <summary>
    /// Invalidate the service counts cache - call this after modifying logs
    /// </summary>
    public async Task InvalidateServiceCountsCache()
    {
        await _cacheLock.WaitAsync();
        try
        {
            _cachedServiceCounts = null;
            _cacheExpiry = null;
            _logger.LogDebug("Service count cache invalidated");
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task RemoveServiceFromLogs(string service)
    {
        try
        {
            // Invalidate cache since we're modifying logs
            await InvalidateServiceCountsCache();

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
                Arguments = $"remove \"{logDir}\" \"{service}\" \"{progressFile}\"",
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
        // Check if cache is valid
        await _cacheLock.WaitAsync();
        try
        {
            if (_cachedServiceCounts != null && _cacheExpiry.HasValue && DateTime.UtcNow < _cacheExpiry.Value)
            {
                _logger.LogDebug("Returning cached service counts (expires in {TimeRemaining})", _cacheExpiry.Value - DateTime.UtcNow);
                return new Dictionary<string, long>(_cachedServiceCounts);
            }
        }
        finally
        {
            _cacheLock.Release();
        }

        var counts = new Dictionary<string, long>();

        try
        {
            // Extract log directory - Rust will discover all log files (access.log, .1, .2.gz, etc.)
            var logDir = Path.GetDirectoryName(_logPath) ?? _pathResolver.GetLogsDirectory();

            if (!Directory.Exists(logDir))
            {
                LogThrottledWarning($"Log directory not found: {logDir}");
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
                Arguments = $"count \"{logDir}\" \"{progressFile}\"",
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

                    if (string.IsNullOrWhiteSpace(json))
                    {
                        _logger.LogWarning("Rust progress file contained no data while counting logs. Path: {ProgressFile}", progressFile);
                        return counts;
                    }

                    try
                    {
                        var options = new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        };
                        var progressData = JsonSerializer.Deserialize<LogCountProgressData>(json, options);

                        if (progressData?.ServiceCounts != null)
                        {
                            counts = progressData.ServiceCounts.ToDictionary(kvp => kvp.Key, kvp => (long)kvp.Value);
                            _logger.LogInformation($"Rust log counting completed: Found {counts.Count} services");

                            // Cache the results
                            await _cacheLock.WaitAsync();
                            try
                            {
                                _cachedServiceCounts = new Dictionary<string, long>(counts);
                                _cacheExpiry = DateTime.UtcNow.Add(_cacheValidDuration);
                                _logger.LogDebug("Service counts cached until {CacheExpiry}", _cacheExpiry);
                            }
                            finally
                            {
                                _cacheLock.Release();
                            }
                        }
                        else
                        {
                            LogThrottledWarning($"Rust progress file did not include service counts. Path: {progressFile}");
                        }
                    }
                    catch (JsonException jsonEx)
                    {
                        LogThrottledWarning($"Rust progress file contained invalid JSON. Path: {progressFile}. Error: {jsonEx.Message}");
                        return counts;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // If the log file doesn't exist, return empty counts instead of throwing
            if (ex.Message.Contains("No such file or directory") || ex.Message.Contains("os error 2"))
            {
                LogThrottledWarning($"Log file not accessible: {_logPath}. Returning empty service counts.");
                return counts;
            }

            // Log the full exception details for debugging
            _logger.LogError(ex, "Error counting service logs with Rust binary. Exception type: {ExceptionType}, Message: {Message}",
                ex.GetType().Name, ex.Message);
            throw;
        }

        return counts;
    }

    /// <summary>
    /// Logs a warning message, but throttles to only log once every 5 minutes
    /// to prevent log spam when repeatedly called
    /// </summary>
    private void LogThrottledWarning(string message)
    {
        var now = DateTime.UtcNow;
        if (!_lastLogWarningTime.HasValue || (now - _lastLogWarningTime.Value) > _logWarningThrottle)
        {
            _logger.LogWarning(message);
            _lastLogWarningTime = now;
        }
        else
        {
            // Log at debug level so we can still track calls, but don't spam warnings
            _logger.LogDebug("(Throttled warning) {Message}", message);
        }
    }

    // Helper class for deserializing Rust progress data
    private class LogCountProgressData
    {
        [System.Text.Json.Serialization.JsonPropertyName("is_processing")]
        public bool IsProcessing { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("percent_complete")]
        public double PercentComplete { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("lines_processed")]
        public ulong LinesProcessed { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("service_counts")]
        public Dictionary<string, ulong>? ServiceCounts { get; set; }
    }

    // Helper class for corruption summary
    private class CorruptionSummaryData
    {
        [System.Text.Json.Serialization.JsonPropertyName("service_counts")]
        public Dictionary<string, ulong>? ServiceCounts { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("total_corrupted")]
        public ulong TotalCorrupted { get; set; }
    }

    // Get list of unique services from logs with improved filtering
    public async Task<List<string>> GetServicesFromLogs()
    {
        try
        {
            // Get all services that have counts (uses Rust binary for fast counting)
            var serviceCounts = await GetServiceLogCounts();

            // Filter out invalid services:
            // - localhost
            // - "ip-address" marker
            // - Raw IP addresses (regex match)
            // Keep valid CDN services (domains with dots like "officecdn.microsoft.com")
            var filteredOut = new List<string>();
            var services = serviceCounts.Keys
                .Where(service =>
                {
                    if (string.IsNullOrEmpty(service)) { filteredOut.Add($"{service} (empty)"); return false; }
                    if (service.Equals("localhost", StringComparison.OrdinalIgnoreCase)) { filteredOut.Add($"{service} (localhost)"); return false; }
                    if (service.Equals("ip-address", StringComparison.OrdinalIgnoreCase)) { filteredOut.Add($"{service} (ip-address marker)"); return false; }

                    // Check if it's a raw IP address (e.g., 192.168.1.1)
                    if (System.Text.RegularExpressions.Regex.IsMatch(service, @"^\d+\.\d+\.\d+\.\d+$"))
                    {
                        filteredOut.Add($"{service} (raw IP)");
                        return false;
                    }

                    return true;
                })
                .OrderBy(s => s)
                .ToList();

            if (services.Count > 0)
            {
                _logger.LogInformation($"Found {services.Count} valid services: {string.Join(", ", services)}");
                if (filteredOut.Count > 0)
                {
                    _logger.LogDebug($"Filtered out {filteredOut.Count} invalid services: {string.Join(", ", filteredOut)}");
                }
                return services;
            }

            // Log which services were filtered out to help debugging
            if (filteredOut.Count > 0)
            {
                _logger.LogInformation($"No valid services found. All {filteredOut.Count} services were filtered out: {string.Join(", ", filteredOut)}");
            }
            else
            {
                LogThrottledWarning("No valid services found in log file - logs may be empty or all entries were removed");
            }
            return new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting services from logs");
            return new List<string>();
        }
    }

    /// <summary>
    /// Get corruption summary with caching based on log file modification time
    /// </summary>
    public async Task<Dictionary<string, long>> GetCorruptionSummary()
    {
        var cachePath = Path.Combine(_pathResolver.GetDataDirectory(), "corruption_summary_cache.json");
        var logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");

        _logger.LogInformation("[CorruptionDetection] GetCorruptionSummary called - cache: {Cache}, log: {Log}", cachePath, logPath);

        // Check if cache exists and is valid
        if (File.Exists(cachePath) && File.Exists(logPath))
        {
            var cacheInfo = new FileInfo(cachePath);
            var logInfo = new FileInfo(logPath);

            _logger.LogInformation("[CorruptionDetection] Cache modified: {CacheTime}, Log modified: {LogTime}",
                cacheInfo.LastWriteTimeUtc, logInfo.LastWriteTimeUtc);

            // Cache is valid if it was created AFTER the log file's last modification
            if (cacheInfo.LastWriteTimeUtc > logInfo.LastWriteTimeUtc)
            {
                _logger.LogInformation("[CorruptionDetection] Using cached corruption summary (cache is newer)");
                try
                {
                    var cachedJson = await File.ReadAllTextAsync(cachePath);

                    var cachedData = JsonSerializer.Deserialize<CorruptionSummaryData>(cachedJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (cachedData?.ServiceCounts != null)
                    {
                        var result = cachedData.ServiceCounts.ToDictionary(kvp => kvp.Key, kvp => (long)kvp.Value);
                        _logger.LogInformation("[CorruptionDetection] Returning cached summary: {Services}",
                            string.Join(", ", result.Select(kvp => $"{kvp.Key}={kvp.Value}")));
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[CorruptionDetection] Failed to read cache, will regenerate");
                }
            }
            else
            {
                _logger.LogInformation("[CorruptionDetection] Cache is stale, will regenerate");
            }
        }
        else
        {
            _logger.LogInformation("[CorruptionDetection] Cache or log missing - cacheExists: {CE}, logExists: {LE}",
                File.Exists(cachePath), File.Exists(logPath));
        }

        // Run Rust binary to detect corruption
        var logDir = Path.GetDirectoryName(logPath) ?? _pathResolver.GetLogsDirectory();
        var cacheDir = _cachePath;
        var timezone = Environment.GetEnvironmentVariable("TZ") ?? "UTC";

        var rustBinaryPath = _pathResolver.GetRustCorruptionManagerPath();

        if (!File.Exists(rustBinaryPath))
        {
            var errorMsg = $"Corruption manager binary not found at {rustBinaryPath}. Please ensure the Rust binaries are built.";
            _logger.LogError(errorMsg);
            throw new FileNotFoundException(errorMsg);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = rustBinaryPath,
            Arguments = $"summary \"{logDir}\" \"{cacheDir}\" \"{timezone}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var process = Process.Start(startInfo))
        {
            if (process == null)
            {
                throw new Exception("Failed to start corruption_manager process");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            var output = await outputTask;
            var error = await errorTask;

            _logger.LogInformation("[CorruptionDetection] Rust process exit code: {Code}", process.ExitCode);

            if (process.ExitCode != 0)
            {
                _logger.LogError("[CorruptionDetection] Failed with exit code {Code}: {Error}", process.ExitCode, error);
                throw new Exception($"corruption_manager failed with exit code {process.ExitCode}: {error}");
            }

            // Parse JSON output from Rust binary
            var summaryData = JsonSerializer.Deserialize<CorruptionSummaryData>(output,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (summaryData?.ServiceCounts == null)
            {
                _logger.LogInformation("[CorruptionDetection] No service counts in result");
                return new Dictionary<string, long>();
            }

            var finalResult = summaryData.ServiceCounts.ToDictionary(kvp => kvp.Key, kvp => (long)kvp.Value);
            _logger.LogInformation("[CorruptionDetection] Summary generated: {Services}",
                string.Join(", ", finalResult.Select(kvp => $"{kvp.Key}={kvp.Value}")));

            // Cache the results
            await File.WriteAllTextAsync(cachePath, output);
            _logger.LogInformation("[CorruptionDetection] Results cached to {Path}", cachePath);

            return finalResult;
        }
    }

    /// <summary>
    /// Remove corrupted chunks for a specific service (invalidates cache)
    /// </summary>
    public async Task RemoveCorruptedChunks(string service)
    {
        _logger.LogInformation("[CorruptionDetection] RemoveCorruptedChunks for service: {Service}", service);

        var logDir = Path.GetDirectoryName(_logPath) ?? _pathResolver.GetLogsDirectory();
        var cacheDir = _cachePath;
        var dataDir = _pathResolver.GetDataDirectory();
        var progressPath = Path.Combine(dataDir, "corruption_removal_progress.json");

        _logger.LogInformation("[CorruptionDetection] Removal params - logDir: {LogDir}, cacheDir: {CacheDir}, progress: {Progress}",
            logDir, cacheDir, progressPath);

        var rustBinaryPath = _pathResolver.GetRustCorruptionManagerPath();

        if (!File.Exists(rustBinaryPath))
        {
            var errorMsg = $"Corruption manager binary not found at {rustBinaryPath}. Please ensure the Rust binaries are built.";
            _logger.LogError(errorMsg);
            throw new FileNotFoundException(errorMsg);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = rustBinaryPath,
            Arguments = $"remove \"{logDir}\" \"{cacheDir}\" \"{service}\" \"{progressPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var process = Process.Start(startInfo))
        {
            if (process == null)
            {
                throw new Exception("Failed to start corruption_manager process");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            var output = await outputTask;
            var error = await errorTask;

            _logger.LogInformation("[CorruptionDetection] Removal process exit code: {Code}", process.ExitCode);
            _logger.LogInformation("[CorruptionDetection] Removal stdout: {Output}", output);
            if (!string.IsNullOrEmpty(error))
            {
                _logger.LogWarning("[CorruptionDetection] Removal stderr: {Error}", error);
            }

            if (process.ExitCode != 0)
            {
                _logger.LogError("[CorruptionDetection] Removal failed with exit code {Code}: {Error}", process.ExitCode, error);
                throw new Exception($"corruption_manager failed with exit code {process.ExitCode}: {error}");
            }

            _logger.LogInformation("[CorruptionDetection] Successfully removed corrupted chunks for {Service}", service);
        }

        // Invalidate corruption summary cache
        var cachePath = Path.Combine(dataDir, "corruption_summary_cache.json");
        if (File.Exists(cachePath))
        {
            _logger.LogInformation("[CorruptionDetection] Deleting cache file: {Path}", cachePath);
            File.Delete(cachePath);
            _logger.LogInformation("[CorruptionDetection] Corruption summary cache invalidated");
        }
        else
        {
            _logger.LogInformation("[CorruptionDetection] No cache file to delete at {Path}", cachePath);
        }

        // Also invalidate service count cache since corruption affects counts
        await _cacheLock.WaitAsync();
        try
        {
            _cachedServiceCounts = null;
            _cacheExpiry = null;
            _logger.LogInformation("[CorruptionDetection] Service count cache invalidated");
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
