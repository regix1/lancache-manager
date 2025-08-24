using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using LancacheManager.Hubs;

namespace LancacheManager.Services;

public class CacheClearingService : IHostedService
{
    private readonly ILogger<CacheClearingService> _logger;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, CacheClearOperation> _operations = new();
    private readonly string _cachePath;
    private Timer? _cleanupTimer;

    public CacheClearingService(
        ILogger<CacheClearingService> logger,
        IHubContext<DownloadHub> hubContext,
        IConfiguration configuration)
    {
        _logger = logger;
        _hubContext = hubContext;
        _configuration = configuration;
        
        // Determine cache path
        if (Directory.Exists("/cache") && Directory.GetDirectories("/cache").Any(d => Path.GetFileName(d).Length == 2))
        {
            _cachePath = "/cache";
        }
        else if (Directory.Exists("/mnt/cache/cache"))
        {
            _cachePath = "/mnt/cache/cache";
        }
        else
        {
            _cachePath = configuration["LanCache:CachePath"] ?? "/cache";
        }
        
        _logger.LogInformation($"CacheClearingService initialized with cache path: {_cachePath}");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer = new Timer(CleanupOldOperations, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Dispose();
        
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
            CancellationTokenSource = new CancellationTokenSource()
        };
        
        _operations[operationId] = operation;
        
        // Use the improved instant clear method that works with nginx running
        _ = Task.Run(async () => await InstantClearCacheAsync(operation), operation.CancellationTokenSource.Token);
        
        return operationId;
    }

    private async Task InstantClearCacheAsync(CacheClearOperation operation)
    {
        try
        {
            _logger.LogInformation($"Starting instant cache clear operation {operation.Id}");
            operation.StatusMessage = "Preparing instant cache clear...";
            operation.TotalDirectories = 256; // We have 256 hex directories (00-ff)
            operation.DirectoriesProcessed = 0;
            await NotifyProgress(operation);
            
            // Small delay to show preparation
            await Task.Delay(500);
            
            if (!Directory.Exists(_cachePath))
            {
                operation.Status = ClearStatus.Failed;
                operation.Error = $"Cache path does not exist: {_cachePath}";
                operation.EndTime = DateTime.UtcNow;
                await NotifyProgress(operation);
                return;
            }

            // Get quick size estimate for display
            long sizeEstimate = await GetCacheSizeEstimate();
            operation.TotalBytesToDelete = sizeEstimate;
            operation.BytesDeleted = 0;
            
            operation.Status = ClearStatus.Running;
            operation.StatusMessage = "Clearing cache files (instant operation)...";
            await NotifyProgress(operation);
            
            // Use the fastest method available
            bool success = false;
            
            // Method 1: Try using find command to delete all files (fastest)
            success = await ClearUsingFindCommand(operation);
            
            if (!success && operation.CancellationTokenSource?.Token.IsCancellationRequested != true)
            {
                // Method 2: Try parallel deletion
                success = await ClearUsingParallelDeletion(operation);
            }
            
            if (!success && operation.CancellationTokenSource?.Token.IsCancellationRequested != true)
            {
                // Method 3: Fallback to truncation method
                success = await ClearUsingTruncation(operation);
            }
            
            if (success)
            {
                operation.Status = ClearStatus.Completed;
                operation.StatusMessage = "Cache cleared successfully!";
                operation.BytesDeleted = sizeEstimate;
                operation.DirectoriesProcessed = 256;
                operation.EndTime = DateTime.UtcNow;
                
                var duration = operation.EndTime.Value - operation.StartTime;
                _logger.LogInformation($"Cache clear completed in {duration.TotalSeconds:F1} seconds! Cleared {sizeEstimate / (1024.0 * 1024.0 * 1024.0):F2} GB");
            }
            else if (operation.CancellationTokenSource?.Token.IsCancellationRequested == true)
            {
                operation.Status = ClearStatus.Cancelled;
                operation.StatusMessage = "Operation cancelled";
                operation.EndTime = DateTime.UtcNow;
            }
            else
            {
                operation.Status = ClearStatus.Failed;
                operation.Error = "All cache clear methods failed. Please check permissions and try again.";
                operation.StatusMessage = "Failed to clear cache";
                operation.EndTime = DateTime.UtcNow;
            }
            
            await NotifyProgress(operation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in cache clear operation {operation.Id}");
            operation.Status = ClearStatus.Failed;
            operation.Error = ex.Message;
            operation.StatusMessage = $"Failed: {ex.Message}";
            operation.EndTime = DateTime.UtcNow;
            await NotifyProgress(operation);
        }
    }

    private async Task<bool> ClearUsingFindCommand(CacheClearOperation operation)
    {
        try
        {
            _logger.LogInformation("Attempting instant clear using find command");
            operation.StatusMessage = "Deleting cache files (optimized method)...";
            await NotifyProgress(operation);
            
            // Use find to delete all files but preserve directory structure
            // This is very fast and doesn't interfere with nginx
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"-c \"find {_cachePath} -type f -delete\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            
            // Update progress while waiting
            var progressTask = Task.Run(async () =>
            {
                var elapsed = 0;
                while (!process.HasExited && elapsed < 60)
                {
                    await Task.Delay(500);
                    elapsed++;
                    
                    // Simulate progress based on time
                    var progress = Math.Min(elapsed * 4, 250);
                    operation.DirectoriesProcessed = progress;
                    operation.BytesDeleted = (long)(operation.TotalBytesToDelete * (progress / 256.0));
                    operation.StatusMessage = $"Clearing cache... ({progress}/256 directories)";
                    await NotifyProgress(operation);
                    
                    if (operation.CancellationTokenSource?.Token.IsCancellationRequested == true)
                    {
                        process.Kill();
                        return;
                    }
                }
            });
            
            var completed = await Task.Run(() => process.WaitForExit(60000)); // 60 second timeout
            
            if (completed && process.ExitCode == 0)
            {
                _logger.LogInformation("Successfully cleared cache using find command");
                return true;
            }
            
            if (!completed)
            {
                process.Kill();
                _logger.LogWarning("Find command timed out");
            }
            else
            {
                var error = await process.StandardError.ReadToEndAsync();
                _logger.LogWarning($"Find command failed with exit code {process.ExitCode}: {error}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to use find command");
        }
        
        return false;
    }

    private async Task<bool> ClearUsingParallelDeletion(CacheClearOperation operation)
    {
        try
        {
            _logger.LogInformation("Attempting clear using parallel deletion");
            operation.StatusMessage = "Clearing cache using parallel deletion...";
            await NotifyProgress(operation);
            
            // Get all hex directories
            var dirs = Directory.GetDirectories(_cachePath)
                .Where(d => {
                    var name = Path.GetFileName(d);
                    return name.Length == 2 && IsHex(name);
                })
                .OrderBy(d => d)
                .ToList();
            
            if (dirs.Count == 0)
            {
                _logger.LogWarning("No cache directories found");
                return false;
            }
            
            var processedCount = 0;
            var semaphore = new SemaphoreSlim(8); // Limit parallel operations
            
            var tasks = dirs.Select(async dir =>
            {
                await semaphore.WaitAsync();
                try
                {
                    if (operation.CancellationTokenSource?.Token.IsCancellationRequested == true)
                        return false;
                    
                    // Delete all files in this hex directory
                    using var process = new Process();
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/sh",
                        Arguments = $"-c \"find '{dir}' -type f -delete\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    
                    process.Start();
                    await process.WaitForExitAsync();
                    
                    Interlocked.Increment(ref processedCount);
                    
                    operation.DirectoriesProcessed = processedCount;
                    operation.BytesDeleted = (long)(operation.TotalBytesToDelete * (processedCount / 256.0));
                    operation.StatusMessage = $"Clearing cache... ({processedCount}/256 directories)";
                    await NotifyProgress(operation);
                    
                    return process.ExitCode == 0;
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();
            
            var results = await Task.WhenAll(tasks);
            var successCount = results.Count(r => r);
            
            _logger.LogInformation($"Cleared {successCount}/{dirs.Count} directories using parallel deletion");
            
            return successCount == dirs.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to use parallel deletion");
        }
        
        return false;
    }

    private async Task<bool> ClearUsingTruncation(CacheClearOperation operation)
    {
        try
        {
            _logger.LogInformation("Attempting clear using truncation method");
            operation.StatusMessage = "Clearing cache using truncation...";
            await NotifyProgress(operation);
            
            // Truncate all files to 0 bytes (makes them invalid to nginx)
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"-c \"find {_cachePath} -type f -exec truncate -s 0 {{}} +\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            var completed = await Task.Run(() => process.WaitForExit(30000)); // 30 second timeout
            
            if (completed && process.ExitCode == 0)
            {
                _logger.LogInformation("Files truncated successfully");
                
                // Schedule background deletion of truncated files
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    using var deleteProcess = new Process();
                    deleteProcess.StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/sh",
                        Arguments = $"-c \"find {_cachePath} -type f -size 0 -delete\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    deleteProcess.Start();
                    await deleteProcess.WaitForExitAsync();
                    _logger.LogInformation("Truncated files deleted in background");
                });
                
                return true;
            }
            
            if (!completed)
            {
                process.Kill();
                _logger.LogWarning("Truncation command timed out");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to use truncation method");
        }
        
        return false;
    }

    private async Task<long> GetCacheSizeEstimate()
    {
        try
        {
            // Try to get actual size quickly (with 2 second timeout)
            using var sizeProcess = new Process();
            sizeProcess.StartInfo = new ProcessStartInfo
            {
                FileName = "timeout",
                Arguments = $"2 du -sb \"{_cachePath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            sizeProcess.Start();
            var sizeTask = sizeProcess.WaitForExitAsync();
            if (await Task.WhenAny(sizeTask, Task.Delay(2000)) == sizeTask && sizeProcess.ExitCode == 0)
            {
                var output = await sizeProcess.StandardOutput.ReadToEndAsync();
                var parts = output.Split('\t');
                if (parts.Length > 0 && long.TryParse(parts[0], out var actualSize))
                {
                    _logger.LogInformation($"Cache size: {actualSize / (1024.0 * 1024.0 * 1024.0):F2} GB");
                    return actualSize;
                }
            }
        }
        catch
        {
            // Ignore size estimation errors
        }
        
        // Default estimate if we couldn't get actual size
        return 500L * 1024 * 1024 * 1024; // Assume 500GB average
    }

    private bool IsHex(string value)
    {
        return value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
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
                PercentComplete = operation.TotalDirectories > 0 
                    ? (operation.DirectoriesProcessed * 100.0 / operation.TotalDirectories) 
                    : 0
            };
            
            await _hubContext.Clients.All.SendAsync("CacheClearProgress", progress);
            
            _logger.LogDebug($"Cache clear progress: {progress.PercentComplete:F1}% - " +
                           $"{progress.StatusMessage}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending cache clear progress notification");
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
                PercentComplete = operation.TotalDirectories > 0 
                    ? (operation.DirectoriesProcessed * 100.0 / operation.TotalDirectories) 
                    : 0
            };
        }
        
        return null;
    }

    public bool CancelOperation(string operationId)
    {
        if (_operations.TryGetValue(operationId, out var operation))
        {
            _logger.LogInformation($"Cancelling cache clear operation {operationId}");
            
            // Update status immediately
            operation.Status = ClearStatus.Cancelled;
            operation.StatusMessage = "Operation cancelled";
            operation.EndTime = DateTime.UtcNow;
            
            // Send immediate notification
            _ = NotifyProgress(operation);
            
            // Then cancel the operation
            operation.CancellationTokenSource?.Cancel();
            
            _logger.LogInformation($"Cache clear operation {operationId} cancelled successfully");
            return true;
        }
        
        return false;
    }

    private void CleanupOldOperations(object? state)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
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
    public int DirectoriesProcessed;
    public long BytesDeleted;
    public long TotalBytesToDelete;
    public int Errors;
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
    
    private string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}