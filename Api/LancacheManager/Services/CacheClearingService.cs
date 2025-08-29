using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using LancacheManager.Hubs;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Services;

public class CacheClearingService : IHostedService
{
    private readonly ILogger<CacheClearingService> _logger;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, CacheClearOperation> _operations = new();
    private readonly string _cachePath;
    private readonly string _statusFilePath = "/tmp/cache_clear_status.json";
    private Timer? _cleanupTimer;

    public CacheClearingService(
        ILogger<CacheClearingService> logger,
        IHubContext<DownloadHub> hubContext,
        IConfiguration configuration)
    {
        _logger = logger;
        _hubContext = hubContext;
        _configuration = configuration;
        
        // Determine cache path - check most likely locations first
        var possiblePaths = new[]
        {
            "/mnt/cache/cache",  // Docker mounted cache
            "/cache",            // Direct container mount
            configuration["LanCache:CachePath"] ?? "/cache"  // Config override
        };
        
        foreach (var path in possiblePaths)
        {
            if (Directory.Exists(path))
            {
                // Check if it has hex directories (00-ff) to confirm it's a cache
                var dirs = Directory.GetDirectories(path);
                if (dirs.Any(d => {
                    var name = Path.GetFileName(d);
                    return name.Length == 2 && IsHex(name);
                }))
                {
                    _cachePath = path;
                    _logger.LogInformation($"Detected cache path: {_cachePath}");
                    break;
                }
            }
        }
        
        // If no valid cache found, use config or default
        if (string.IsNullOrEmpty(_cachePath))
        {
            _cachePath = configuration["LanCache:CachePath"] ?? "/mnt/cache/cache";
            _logger.LogWarning($"No cache detected, using configured path: {_cachePath}");
        }
        
        _logger.LogInformation($"CacheClearingService initialized with cache path: {_cachePath}");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadPersistedOperations();
        _cleanupTimer = new Timer(CleanupOldOperations, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Dispose();
        SavePersistedOperations();
        
        foreach (var operation in _operations.Values)
        {
            operation.CancellationTokenSource?.Cancel();
        }
        
        return Task.CompletedTask;
    }

    public async Task<string> StartCacheClearAsync(string? service = null)
    {
        var operationId = Guid.NewGuid().ToString();
        var operation = new CacheClearOperation
        {
            Id = operationId,
            Service = service,
            StartTime = DateTime.UtcNow,
            Status = ClearStatus.Preparing,
            StatusMessage = "Initializing cache clear...",
            CancellationTokenSource = new CancellationTokenSource()
        };
        
        _operations[operationId] = operation;
        SavePersistedOperations();
        
        _logger.LogInformation($"Starting cache clear operation {operationId}");
        
        // Start the clear operation on a background thread
        _ = Task.Run(async () => await ExecuteCacheClear(operation), operation.CancellationTokenSource.Token);
        
        return operationId;
    }

    private async Task ExecuteCacheClear(CacheClearOperation operation)
    {
        try
        {
            _logger.LogInformation($"Executing cache clear operation {operation.Id}");
            
            operation.Status = ClearStatus.Preparing;
            operation.StatusMessage = "Analyzing cache structure...";
            await NotifyProgress(operation);
            
            if (!Directory.Exists(_cachePath))
            {
                operation.Status = ClearStatus.Failed;
                operation.Error = $"Cache path does not exist: {_cachePath}";
                operation.EndTime = DateTime.UtcNow;
                await NotifyProgress(operation);
                SavePersistedOperations();
                return;
            }

            // Get all hex directories (00-ff)
            var hexDirs = Directory.GetDirectories(_cachePath)
                .Where(d => {
                    var name = Path.GetFileName(d);
                    return name.Length == 2 && IsHex(name);
                })
                .OrderBy(d => d)
                .ToList();

            if (hexDirs.Count == 0)
            {
                operation.Status = ClearStatus.Failed;
                operation.Error = "No cache directories found";
                operation.EndTime = DateTime.UtcNow;
                await NotifyProgress(operation);
                SavePersistedOperations();
                return;
            }

            operation.TotalDirectories = hexDirs.Count;
            _logger.LogInformation($"Found {hexDirs.Count} cache directories to clear");

            // Try to get cache size estimation
            operation.StatusMessage = "Estimating cache size (this may take a moment for large caches)...";
            await NotifyProgress(operation);
            
            long estimatedSize = await EstimateCacheSizeQuick();
            operation.TotalBytesToDelete = estimatedSize;
            
            _logger.LogInformation($"Cache size: {FormatBytes(estimatedSize)}");
            
            // Start clearing
            operation.Status = ClearStatus.Running;
            operation.StatusMessage = "Starting cache deletion...";
            await NotifyProgress(operation);
            SavePersistedOperations();
            
            // Track progress
            long totalBytesDeleted = 0;
            int dirsProcessed = 0;
            var startTime = DateTime.UtcNow;
            
            // Process directories
            foreach (var dir in hexDirs)
            {
                if (operation.CancellationTokenSource?.Token.IsCancellationRequested == true)
                {
                    operation.Status = ClearStatus.Cancelled;
                    operation.StatusMessage = $"Cancelled - Cleared {FormatBytes(totalBytesDeleted)}";
                    operation.EndTime = DateTime.UtcNow;
                    await NotifyProgress(operation);
                    SavePersistedOperations();
                    return;
                }
                
                var dirName = Path.GetFileName(dir);
                _logger.LogDebug($"Processing directory {dirName} ({dirsProcessed + 1}/{hexDirs.Count})");
                
                // Get size of this directory before deleting
                long dirSize = 0;
                try
                {
                    dirSize = await GetDirectorySizeQuick(dir);
                    _logger.LogDebug($"Directory {dirName} size: {FormatBytes(dirSize)}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to get size of directory {dirName}");
                }
                
                // Delete the directory contents
                bool success = await DeleteDirectoryContents(dir);
                
                if (success)
                {
                    totalBytesDeleted += dirSize;
                    operation.BytesDeleted = totalBytesDeleted;
                    _logger.LogDebug($"Successfully cleared directory {dirName}, total cleared: {FormatBytes(totalBytesDeleted)}");
                }
                else
                {
                    _logger.LogWarning($"Failed to fully clear directory {dirName}");
                }
                
                dirsProcessed++;
                operation.DirectoriesProcessed = dirsProcessed;
                
                // Calculate percentage
                if (operation.TotalBytesToDelete > 0 && totalBytesDeleted > 0)
                {
                    // Use bytes for percentage if we have actual data
                    operation.PercentComplete = Math.Min(100, (totalBytesDeleted * 100.0) / operation.TotalBytesToDelete);
                }
                else
                {
                    // Fall back to directory count
                    operation.PercentComplete = (dirsProcessed * 100.0) / hexDirs.Count;
                }
                
                // Update status message with progress
                var elapsed = DateTime.UtcNow - startTime;
                var rate = totalBytesDeleted / Math.Max(1, elapsed.TotalSeconds);
                
                operation.StatusMessage = $"Clearing directory {dirName} ({dirsProcessed}/{hexDirs.Count}) - {FormatBytes(totalBytesDeleted)} cleared";
                
                if (rate > 0 && operation.TotalBytesToDelete > totalBytesDeleted)
                {
                    var remainingBytes = operation.TotalBytesToDelete - totalBytesDeleted;
                    var remainingSeconds = remainingBytes / rate;
                    var remainingTime = TimeSpan.FromSeconds(remainingSeconds);
                    
                    if (remainingTime.TotalMinutes < 60)
                    {
                        operation.StatusMessage += $" - ~{remainingTime.Minutes}m remaining";
                    }
                    else
                    {
                        operation.StatusMessage += $" - ~{remainingTime.Hours}h {remainingTime.Minutes}m remaining";
                    }
                }
                
                // Send update every directory or every 2% progress
                await NotifyProgress(operation);
                
                // Save state every 10 directories
                if (dirsProcessed % 10 == 0)
                {
                    SavePersistedOperations();
                }
            }
            
            // Final update
            operation.Status = ClearStatus.Completed;
            operation.StatusMessage = $"Successfully cleared {FormatBytes(totalBytesDeleted)} from {dirsProcessed} directories";
            operation.EndTime = DateTime.UtcNow;
            operation.BytesDeleted = totalBytesDeleted;
            operation.PercentComplete = 100;
            
            var duration = operation.EndTime.Value - operation.StartTime;
            _logger.LogInformation($"Cache clear completed in {duration.TotalSeconds:F1} seconds - Cleared {FormatBytes(totalBytesDeleted)}");
            
            await NotifyProgress(operation);
            SavePersistedOperations();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in cache clear operation {operation.Id}");
            operation.Status = ClearStatus.Failed;
            operation.Error = ex.Message;
            operation.StatusMessage = $"Failed: {ex.Message}";
            operation.EndTime = DateTime.UtcNow;
            await NotifyProgress(operation);
            SavePersistedOperations();
        }
    }

    private async Task<long> EstimateCacheSizeQuick()
    {
        try
        {
            // First, try to get the used space from df for the mount point
            // This is instant and accurate
            _logger.LogInformation($"Getting cache size for {_cachePath}");
            
            using var dfProcess = new Process();
            dfProcess.StartInfo = new ProcessStartInfo
            {
                FileName = "df",
                Arguments = "-B1 " + _cachePath, // Size in bytes
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            dfProcess.Start();
            string dfOutput = await dfProcess.StandardOutput.ReadToEndAsync();
            await dfProcess.WaitForExitAsync();
            
            if (dfProcess.ExitCode == 0 && !string.IsNullOrEmpty(dfOutput))
            {
                var lines = dfOutput.Split('\n');
                if (lines.Length > 1)
                {
                    // Parse the second line (first is header)
                    var parts = lines[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 && long.TryParse(parts[2], out var usedBytes))
                    {
                        _logger.LogInformation($"Cache mount point shows {FormatBytes(usedBytes)} used");
                        
                        // If this is a shared mount, we need to calculate just the cache portion
                        // Try a quick du with larger timeout for more accuracy
                        return await GetActualCacheSize(usedBytes);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get size from df");
        }
        
        // Fallback: Try du with longer timeout
        try
        {
            _logger.LogInformation("Trying du command to estimate size...");
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "timeout",
                Arguments = $"30 du -sb {_cachePath}", // 30 second timeout for large caches
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            
            // Start reading output asynchronously
            var outputTask = process.StandardOutput.ReadToEndAsync();
            
            // Wait for completion or timeout
            if (await Task.Run(() => process.WaitForExit(30000)))
            {
                string output = await outputTask;
                if (!string.IsNullOrEmpty(output))
                {
                    var parts = output.Split('\t');
                    if (parts.Length > 0 && long.TryParse(parts[0], out var size))
                    {
                        _logger.LogInformation($"du reported cache size: {FormatBytes(size)}");
                        return size;
                    }
                }
            }
            else
            {
                _logger.LogWarning("du command timed out after 30 seconds");
                process.Kill();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get cache size with du");
        }
        
        // Last resort: sample-based estimation
        try
        {
            _logger.LogInformation("Using sampling to estimate cache size...");
            var hexDirs = Directory.GetDirectories(_cachePath)
                .Where(d => Path.GetFileName(d).Length == 2)
                .ToList();
            
            if (hexDirs.Any())
            {
                long totalSampleSize = 0;
                int totalSampleFiles = 0;
                int dirsToSample = Math.Min(5, hexDirs.Count); // Sample up to 5 directories
                
                for (int i = 0; i < dirsToSample; i++)
                {
                    var dir = hexDirs[i];
                    try
                    {
                        // Use du for each sample directory (faster than counting files)
                        using var process = new Process();
                        process.StartInfo = new ProcessStartInfo
                        {
                            FileName = "timeout",
                            Arguments = $"5 du -sb {dir}",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        
                        process.Start();
                        string output = await process.StandardOutput.ReadToEndAsync();
                        await process.WaitForExitAsync();
                        
                        if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                        {
                            var parts = output.Split('\t');
                            if (parts.Length > 0 && long.TryParse(parts[0], out var dirSize))
                            {
                                totalSampleSize += dirSize;
                                totalSampleFiles++;
                            }
                        }
                    }
                    catch { }
                }
                
                if (totalSampleFiles > 0)
                {
                    var avgDirSize = totalSampleSize / totalSampleFiles;
                    var estimatedTotal = avgDirSize * hexDirs.Count;
                    _logger.LogInformation($"Estimated cache size from sampling: {FormatBytes(estimatedTotal)}");
                    return estimatedTotal;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to estimate cache size from sampling");
        }
        
        // Absolute fallback: 500GB estimate (more realistic for a cache)
        _logger.LogWarning("Using fallback cache size estimate of 500GB");
        return 500L * 1024 * 1024 * 1024;
    }

    private async Task<long> GetActualCacheSize(long mountUsedBytes)
    {
        try
        {
            // Quick check if the entire mount is the cache
            // Try to run du with a timeout to see if we can get actual cache size
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "timeout",
                Arguments = $"15 du -sb {_cachePath}", // 15 second timeout
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            
            // Check periodically if process completed
            for (int i = 0; i < 15; i++)
            {
                if (process.HasExited)
                {
                    string output = await process.StandardOutput.ReadToEndAsync();
                    if (!string.IsNullOrEmpty(output))
                    {
                        var parts = output.Split('\t');
                        if (parts.Length > 0 && long.TryParse(parts[0], out var size))
                        {
                            _logger.LogInformation($"Actual cache size: {FormatBytes(size)}");
                            return size;
                        }
                    }
                    break;
                }
                await Task.Delay(1000);
            }
            
            if (!process.HasExited)
            {
                process.Kill();
                _logger.LogWarning("du timed out, using mount size as estimate");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get actual cache size");
        }
        
        // Assume most of the mount is cache
        return mountUsedBytes;
    }

    private async Task<long> GetDirectorySizeQuick(string directory)
    {
        var dirName = Path.GetFileName(directory);
        
        try
        {
            // Try to use du for quick size
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "timeout",
                Arguments = $"3 du -sb '{directory}'", // 3 second timeout per directory
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            bool completed = await Task.Run(() => process.WaitForExit(3000));
            
            if (completed && process.ExitCode == 0 && !string.IsNullOrEmpty(output))
            {
                var parts = output.Split('\t');
                if (parts.Length > 0 && long.TryParse(parts[0], out var size))
                {
                    _logger.LogTrace($"Directory {dirName} size from du: {FormatBytes(size)}");
                    return size;
                }
            }
            
            if (!completed)
            {
                process.Kill();
                _logger.LogTrace($"du timed out for directory {dirName}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace($"du failed for directory {dirName}: {ex.Message}");
        }
        
        // Fallback: count files manually but quickly
        try
        {
            long totalSize = 0;
            int fileCount = 0;
            
            // Use EnumerateFiles for better performance
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
            
            foreach (var file in files)
            {
                try
                {
                    var fi = new FileInfo(file);
                    totalSize += fi.Length;
                    fileCount++;
                    
                    // Stop sampling after 1000 files and extrapolate
                    if (fileCount >= 1000)
                    {
                        // Count remaining files quickly without getting size
                        var totalFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length;
                        if (totalFiles > fileCount)
                        {
                            var avgSize = totalSize / fileCount;
                            totalSize = avgSize * totalFiles;
                            _logger.LogTrace($"Directory {dirName}: sampled {fileCount} files, extrapolated to {totalFiles} files = {FormatBytes(totalSize)}");
                        }
                        break;
                    }
                }
                catch { }
            }
            
            _logger.LogTrace($"Directory {dirName} size from file count: {FormatBytes(totalSize)} ({fileCount} files checked)");
            return totalSize;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to get size for directory {dirName}");
            // Return a default estimate based on typical cache directory size
            return 5L * 1024 * 1024 * 1024; // 5GB default per directory
        }
    }

    private async Task<bool> DeleteDirectoryContents(string directory)
    {
        try
        {
            var dirName = Path.GetFileName(directory);
            _logger.LogDebug($"Starting deletion of directory {dirName}");
            
            // Use rm -rf with sudo if needed (for permission issues)
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"rm -rf '{directory}'/* 2>/dev/null || sudo rm -rf '{directory}'/* 2>/dev/null\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            
            // Wait for up to 120 seconds per directory (increased timeout)
            bool completed = await Task.Run(() => process.WaitForExit(120000));
            
            if (!completed)
            {
                _logger.LogWarning($"Delete operation timed out for directory {dirName}, killing process");
                process.Kill();
                
                // Try forceful deletion
                return await ForceDeleteDirectory(directory);
            }
            
            var error = await process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrEmpty(error))
            {
                _logger.LogWarning($"Deletion warnings for {dirName}: {error}");
            }
            
            // Verify deletion was successful
            var remainingFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length;
            if (remainingFiles == 0)
            {
                _logger.LogDebug($"Successfully deleted all contents of directory {dirName}");
                return true;
            }
            else
            {
                _logger.LogWarning($"Directory {dirName} still has {remainingFiles} files after deletion attempt");
                return await ForceDeleteDirectory(directory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to delete directory {directory}");
            return false;
        }
    }

    private async Task<bool> ForceDeleteDirectory(string directory)
    {
        try
        {
            var dirName = Path.GetFileName(directory);
            _logger.LogWarning($"Attempting force deletion for directory {dirName}");
            
            // Try with ionice to reduce I/O impact
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"ionice -c3 find '{directory}' -type f -delete && find '{directory}' -type d -empty -delete\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            await process.WaitForExitAsync();
            
            // Final verification
            var remainingFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length;
            var success = remainingFiles == 0;
            
            if (success)
            {
                _logger.LogInformation($"Force deletion successful for directory {dirName}");
            }
            else
            {
                _logger.LogError($"Force deletion failed for directory {dirName}, {remainingFiles} files remain");
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Force deletion failed for {directory}");
            return false;
        }
    }

    private bool IsHex(string value)
    {
        return value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }

    private string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        var sizes = new[] { "B", "KB", "MB", "GB", "TB" };
        var i = (int)Math.Floor(Math.Log(bytes) / Math.Log(1024));
        return $"{bytes / Math.Pow(1024, i):F2} {sizes[i]}";
    }

    private async Task NotifyProgress(CacheClearOperation operation)
    {
        try
        {
            var progress = new CacheClearProgress
            {
                OperationId = operation.Id,
                Status = operation.Status.ToString(),
                StatusMessage = operation.StatusMessage,
                Service = operation.Service,
                StartTime = operation.StartTime,
                EndTime = operation.EndTime,
                DirectoriesProcessed = operation.DirectoriesProcessed,
                TotalDirectories = operation.TotalDirectories,
                BytesDeleted = operation.BytesDeleted,
                TotalBytesToDelete = operation.TotalBytesToDelete,
                Errors = operation.Errors,
                Error = operation.Error,
                PercentComplete = operation.PercentComplete
            };
            
            await _hubContext.Clients.All.SendAsync("CacheClearProgress", progress);
            
            _logger.LogDebug($"Progress: {progress.PercentComplete:F1}% - {operation.StatusMessage}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending cache clear progress notification");
        }
    }

    private void LoadPersistedOperations()
    {
        try
        {
            if (File.Exists(_statusFilePath))
            {
                var json = File.ReadAllText(_statusFilePath);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() }
                };
                
                var operations = JsonSerializer.Deserialize<List<CacheClearOperation>>(json, options);
                
                if (operations != null)
                {
                    foreach (var op in operations)
                    {
                        // Keep all operations from last 24 hours for status queries
                        if (op.StartTime > DateTime.UtcNow.AddHours(-24))
                        {
                            _operations[op.Id] = op;
                        }
                    }
                    
                    _logger.LogInformation($"Loaded {_operations.Count} persisted cache clear operations");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load persisted operations");
        }
    }

    private void SavePersistedOperations()
    {
        try
        {
            var operations = _operations.Values.ToList();
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };
            
            var json = JsonSerializer.Serialize(operations, options);
            File.WriteAllText(_statusFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save persisted operations");
        }
    }

    public CacheClearProgress? GetOperationStatus(string operationId)
    {
        if (_operations.TryGetValue(operationId, out var operation))
        {
            return new CacheClearProgress
            {
                OperationId = operation.Id,
                Status = operation.Status.ToString(),
                StatusMessage = operation.StatusMessage,
                Service = operation.Service,
                StartTime = operation.StartTime,
                EndTime = operation.EndTime,
                DirectoriesProcessed = operation.DirectoriesProcessed,
                TotalDirectories = operation.TotalDirectories,
                BytesDeleted = operation.BytesDeleted,
                TotalBytesToDelete = operation.TotalBytesToDelete,
                Errors = operation.Errors,
                Error = operation.Error,
                PercentComplete = operation.PercentComplete
            };
        }
        
        return null;
    }

    public List<CacheClearProgress> GetAllOperations()
    {
        return _operations.Values.Select(op => new CacheClearProgress
        {
            OperationId = op.Id,
            Status = op.Status.ToString(),
            StatusMessage = op.StatusMessage,
            Service = op.Service,
            StartTime = op.StartTime,
            EndTime = op.EndTime,
            DirectoriesProcessed = op.DirectoriesProcessed,
            TotalDirectories = op.TotalDirectories,
            BytesDeleted = op.BytesDeleted,
            TotalBytesToDelete = op.TotalBytesToDelete,
            Errors = op.Errors,
            Error = op.Error,
            PercentComplete = op.PercentComplete
        }).ToList();
    }

    public bool CancelOperation(string operationId)
    {
        if (_operations.TryGetValue(operationId, out var operation))
        {
            _logger.LogInformation($"Cancelling cache clear operation {operationId}");
            
            operation.CancellationTokenSource?.Cancel();
            
            // Don't immediately mark as cancelled - let the operation handle it
            // This prevents race conditions
            
            return true;
        }
        
        return false;
    }

    private void CleanupOldOperations(object? state)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            var toRemove = _operations
                .Where(kvp => kvp.Value.EndTime.HasValue && kvp.Value.EndTime.Value < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var key in toRemove)
            {
                if (_operations.TryRemove(key, out var operation))
                {
                    operation.CancellationTokenSource?.Dispose();
                }
            }
            
            if (toRemove.Count > 0)
            {
                _logger.LogDebug($"Cleaned up {toRemove.Count} old cache clear operations");
                SavePersistedOperations();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old operations");
        }
    }
}

public class CacheClearOperation
{
    public string Id { get; set; } = string.Empty;
    public string? Service { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public ClearStatus Status { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string? Error { get; set; }
    public int TotalDirectories { get; set; }
    public int DirectoriesProcessed { get; set; }
    public long BytesDeleted { get; set; }
    public long TotalBytesToDelete { get; set; }
    public int Errors { get; set; }
    public double PercentComplete { get; set; }
    
    [JsonIgnore]
    public CancellationTokenSource? CancellationTokenSource { get; set; }
}

public enum ClearStatus
{
    Preparing,
    Running,
    Completed,
    Failed,
    Cancelled
}

public class CacheClearProgress
{
    public string OperationId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusMessage { get; set; } = string.Empty;
    public string? Service { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int DirectoriesProcessed { get; set; }
    public int TotalDirectories { get; set; }
    public long BytesDeleted { get; set; }
    public long TotalBytesToDelete { get; set; }
    public int Errors { get; set; }
    public string? Error { get; set; }
    public double PercentComplete { get; set; }
    
    public string BytesDeletedFormatted => FormatBytes(BytesDeleted);
    public string TotalBytesFormatted => FormatBytes(TotalBytesToDelete);
    
    private string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}