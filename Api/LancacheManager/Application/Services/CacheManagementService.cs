using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Application.Services;

public class CacheManagementService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CacheManagementService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly string _cachePath;
    private readonly string _logPath;

    // Lock for thread safety during Rust binary execution
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
        // Delete the Rust cache file to force rescan
        var dataDir = _pathResolver.GetDataDirectory();
        var progressFile = Path.Combine(dataDir, "log_count_progress.json");

        if (File.Exists(progressFile))
        {
            await Task.Run(() => File.Delete(progressFile));
        }
    }

    public async Task RemoveServiceFromLogs(string service)
    {
        // Use semaphore to ensure only one Rust process runs at a time
        await _cacheLock.WaitAsync();
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
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<Dictionary<string, long>> GetServiceLogCounts(bool forceRefresh = false)
    {
        // Use semaphore to ensure only one Rust process runs at a time
        await _cacheLock.WaitAsync();
        try
        {
            // Use Rust binary which has its own file-based caching with modification time validation
            // No need for C# in-memory cache - Rust handles it efficiently
            var counts = new Dictionary<string, long>();

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

            // If forceRefresh is true, delete the cache file to force Rust to rescan
            if (forceRefresh && File.Exists(progressFile))
            {
                _logger.LogInformation("Force refresh - deleting cached progress file: {ProgressFile}", progressFile);
                File.Delete(progressFile);
            }

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
                            // Rust binary handles caching via file modification time - no need for C# cache
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

            return counts;
        }
        catch (Exception ex)
        {
            // If the log file doesn't exist, return empty counts instead of throwing
            if (ex.Message.Contains("No such file or directory") || ex.Message.Contains("os error 2"))
            {
                LogThrottledWarning($"Log file not accessible: {_logPath}. Returning empty service counts.");
                return new Dictionary<string, long>();
            }

            // Log the full exception details for debugging
            _logger.LogError(ex, "Error counting service logs with Rust binary. Exception type: {ExceptionType}, Message: {Message}",
                ex.GetType().Name, ex.Message);
            throw;
        }
        finally
        {
            _cacheLock.Release();
        }
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

    // Helper classes for detailed corruption report
    private class CorruptionReport
    {
        [System.Text.Json.Serialization.JsonPropertyName("corrupted_chunks")]
        public List<CorruptedChunkDetail>? CorruptedChunks { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("summary")]
        public CorruptionSummaryData? Summary { get; set; }
    }

    public class CorruptedChunkDetail
    {
        [System.Text.Json.Serialization.JsonPropertyName("service")]
        public string Service { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("miss_count")]
        public ulong MissCount { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("cache_file_path")]
        public string CacheFilePath { get; set; } = string.Empty;
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
    public async Task<Dictionary<string, long>> GetCorruptionSummary(bool forceRefresh = false)
    {
        // Use semaphore to ensure only one Rust process runs at a time
        await _cacheLock.WaitAsync();
        try
        {
            var logDir = _pathResolver.GetLogsDirectory();
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

                // No caching needed - Rust binary is fast enough to run on every request
                return finalResult;
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Remove corrupted chunks for a specific service (invalidates cache)
    /// </summary>
    public async Task RemoveCorruptedChunks(string service)
    {
        // Use semaphore to ensure only one Rust process runs at a time
        await _cacheLock.WaitAsync();
        try
        {
            _logger.LogInformation("[CorruptionDetection] RemoveCorruptedChunks for service: {Service}", service);

            var dbPath = _pathResolver.GetDatabasePath();
            var logDir = Path.GetDirectoryName(_logPath) ?? _pathResolver.GetLogsDirectory();
            var cacheDir = _cachePath;
            var dataDir = _pathResolver.GetDataDirectory();
            var progressPath = Path.Combine(dataDir, "corruption_removal_progress.json");

            _logger.LogInformation("[CorruptionDetection] Removal params - db: {DbPath}, logDir: {LogDir}, cacheDir: {CacheDir}, progress: {Progress}",
                dbPath, logDir, cacheDir, progressPath);

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
                Arguments = $"remove \"{dbPath}\" \"{logDir}\" \"{cacheDir}\" \"{service}\" \"{progressPath}\"",
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

            // No corruption summary cache to invalidate (Rust runs fresh each time)
            // Invalidate service count cache since corruption removal affects counts
            await InvalidateServiceCountsCache();
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Get detailed corruption information for a specific service
    /// </summary>
    public async Task<List<CorruptedChunkDetail>> GetCorruptionDetails(string service, bool forceRefresh = false)
    {
        // Use semaphore to ensure only one Rust process runs at a time
        await _cacheLock.WaitAsync();
        try
        {
            _logger.LogInformation("[CorruptionDetection] GetCorruptionDetails for service: {Service}, forceRefresh: {ForceRefresh}",
                service, forceRefresh);

            var logDir = _pathResolver.GetLogsDirectory();
            var cacheDir = _cachePath;
            var dataDir = _pathResolver.GetDataDirectory();
            var timezone = Environment.GetEnvironmentVariable("TZ") ?? "UTC";
            var outputJson = Path.Combine(dataDir, $"corruption_details_{service}_{DateTime.UtcNow:yyyyMMddHHmmss}.json");

            var rustBinaryPath = _pathResolver.GetRustCorruptionManagerPath();

            if (!File.Exists(rustBinaryPath))
            {
                var errorMsg = $"Corruption manager binary not found at {rustBinaryPath}. Please ensure the Rust binaries are built.";
                _logger.LogError(errorMsg);
                throw new FileNotFoundException(errorMsg);
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = rustBinaryPath,
                    Arguments = $"detect \"{logDir}\" \"{cacheDir}\" \"{outputJson}\" \"{timezone}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _logger.LogInformation("[CorruptionDetection] Running detect command: {Command} {Args}",
                    rustBinaryPath, startInfo.Arguments);

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

                    _logger.LogInformation("[CorruptionDetection] Detect process exit code: {Code}", process.ExitCode);

                    if (process.ExitCode != 0)
                    {
                        _logger.LogError("[CorruptionDetection] Detect failed with exit code {Code}: {Error}",
                            process.ExitCode, error);
                        throw new Exception($"corruption_manager detect failed with exit code {process.ExitCode}: {error}");
                    }

                    // Read the generated JSON file
                    if (!File.Exists(outputJson))
                    {
                        _logger.LogError("[CorruptionDetection] Output JSON file not found: {Path}", outputJson);
                        throw new FileNotFoundException($"Corruption details output file not found: {outputJson}");
                    }

                    var jsonContent = await File.ReadAllTextAsync(outputJson);
                    _logger.LogInformation("[CorruptionDetection] Read JSON output, length: {Length}", jsonContent.Length);

                    // Parse the report
                    var report = JsonSerializer.Deserialize<CorruptionReport>(jsonContent,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (report?.CorruptedChunks == null)
                    {
                        _logger.LogInformation("[CorruptionDetection] No corrupted chunks in report");
                        return new List<CorruptedChunkDetail>();
                    }

                    // Filter by service
                    var serviceDetails = report.CorruptedChunks
                        .Where(chunk => chunk.Service.Equals(service, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    _logger.LogInformation("[CorruptionDetection] Found {Count} corrupted chunks for service {Service}",
                        serviceDetails.Count, service);

                    // Clean up temporary JSON file
                    try
                    {
                        File.Delete(outputJson);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[CorruptionDetection] Failed to delete temporary JSON file: {Path}", outputJson);
                    }

                    return serviceDetails;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CorruptionDetection] Error getting corruption details for service {Service}", service);
                throw;
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    // Helper classes for game cache removal
    public class GameCacheRemovalReport
    {
        [System.Text.Json.Serialization.JsonPropertyName("game_app_id")]
        public uint GameAppId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("game_name")]
        public string GameName { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("cache_files_deleted")]
        public int CacheFilesDeleted { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("total_bytes_freed")]
        public ulong TotalBytesFreed { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("empty_dirs_removed")]
        public int EmptyDirsRemoved { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("log_entries_removed")]
        public ulong LogEntriesRemoved { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("depot_ids")]
        public List<uint> DepotIds { get; set; } = new List<uint>();
    }

    /// <summary>
    /// Remove all cache files for a specific game
    /// </summary>
    public async Task<GameCacheRemovalReport> RemoveGameFromCache(uint gameAppId)
    {
        await _cacheLock.WaitAsync();
        try
        {
            _logger.LogInformation("[GameRemoval] Starting game cache removal for AppID {AppId}", gameAppId);

            var dataDir = _pathResolver.GetDataDirectory();
            var dbPath = _pathResolver.GetDatabasePath();
            var logsDir = _pathResolver.GetLogsDirectory();
            var outputJson = Path.Combine(dataDir, $"game_removal_{gameAppId}_{DateTime.UtcNow:yyyyMMddHHmmss}.json");

            var rustBinaryPath = _pathResolver.GetRustGameRemoverPath();

            if (!File.Exists(rustBinaryPath))
            {
                var errorMsg = $"Game cache remover binary not found at {rustBinaryPath}. Please ensure the Rust binaries are built.";
                _logger.LogError(errorMsg);
                throw new FileNotFoundException(errorMsg);
            }

            if (!File.Exists(dbPath))
            {
                var errorMsg = $"Database not found at {dbPath}";
                _logger.LogError(errorMsg);
                throw new FileNotFoundException(errorMsg);
            }

            if (!Directory.Exists(logsDir))
            {
                var errorMsg = $"Logs directory not found at {logsDir}";
                _logger.LogError(errorMsg);
                throw new DirectoryNotFoundException(errorMsg);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = rustBinaryPath,
                Arguments = $"\"{dbPath}\" \"{logsDir}\" \"{_cachePath}\" {gameAppId} \"{outputJson}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _logger.LogInformation("[GameRemoval] Running removal: {Binary} {Args}", rustBinaryPath, startInfo.Arguments);

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new Exception("Failed to start game_cache_remover process");
                }

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

                var output = await outputTask;
                var error = await errorTask;

                _logger.LogInformation("[GameRemoval] Process exit code: {Code}", process.ExitCode);

                // Log stdout (completion messages and summary)
                if (!string.IsNullOrEmpty(output))
                {
                    _logger.LogInformation("[GameRemoval] Process output:\n{Output}", output);
                }

                // Log stderr (diagnostic messages)
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogInformation("[GameRemoval] Process stderr: {Error}", error);
                }

                if (process.ExitCode != 0)
                {
                    _logger.LogError("[GameRemoval] Failed with exit code {Code}: {Error}", process.ExitCode, error);
                    throw new Exception($"game_cache_remover failed with exit code {process.ExitCode}: {error}");
                }

                // Read the generated JSON file
                if (!File.Exists(outputJson))
                {
                    _logger.LogError("[GameRemoval] Output JSON file not found: {Path}", outputJson);
                    throw new FileNotFoundException($"Game removal output file not found: {outputJson}");
                }

                var jsonContent = await File.ReadAllTextAsync(outputJson);
                _logger.LogInformation("[GameRemoval] Read JSON output, length: {Length}", jsonContent.Length);

                // Parse the report
                var report = JsonSerializer.Deserialize<GameCacheRemovalReport>(jsonContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (report == null)
                {
                    _logger.LogError("[GameRemoval] Failed to parse removal report");
                    throw new Exception("Failed to parse game removal report");
                }

                _logger.LogInformation("[GameRemoval] Removed {Files} files ({Bytes} bytes) for game {AppId}",
                    report.CacheFilesDeleted, report.TotalBytesFreed, gameAppId);

                // Clean up temporary JSON file
                try
                {
                    File.Delete(outputJson);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[GameRemoval] Failed to delete temporary JSON file: {Path}", outputJson);
                }

                return report;
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
