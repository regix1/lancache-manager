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
        
        // Start the clearing operation in the background
        _ = Task.Run(async () => await ClearCacheOptimizedAsync(operation), operation.CancellationTokenSource.Token);
        
        return operationId;
    }

    private async Task ClearCacheOptimizedAsync(CacheClearOperation operation)
    {
        try
        {
            _logger.LogInformation($"Starting optimized cache clear operation {operation.Id}");
            
            if (!Directory.Exists(_cachePath))
            {
                operation.Status = ClearStatus.Failed;
                operation.Error = $"Cache path does not exist: {_cachePath}";
                operation.EndTime = DateTime.UtcNow;
                await NotifyProgress(operation);
                return;
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
                operation.Status = ClearStatus.Failed;
                operation.Error = $"No cache directories (00-ff) found in {_cachePath}";
                operation.EndTime = DateTime.UtcNow;
                await NotifyProgress(operation);
                return;
            }
            
            operation.TotalDirectories = dirs.Count;
            operation.Status = ClearStatus.Running;
            
            // Get initial size estimate (quick sampling)
            var initialSize = await EstimateCacheSizeAsync(_cachePath);
            operation.BytesDeleted = 0;
            operation.TotalBytesToDelete = initialSize;
            
            _logger.LogInformation($"Found {dirs.Count} cache directories to clear, estimated size: {initialSize / (1024.0 * 1024.0 * 1024.0):F2} GB");
            await NotifyProgress(operation);

            // Choose deletion method based on what's available
            var deletionMethod = await DetermineBestDeletionMethod();
            _logger.LogInformation($"Using deletion method: {deletionMethod}");

            switch (deletionMethod)
            {
                case DeletionMethod.RsyncDelete:
                    await ClearUsingRsyncAsync(operation, dirs);
                    break;
                case DeletionMethod.FindDelete:
                    await ClearUsingFindAsync(operation, dirs);
                    break;
                case DeletionMethod.ParallelRm:
                    await ClearUsingParallelRmAsync(operation, dirs);
                    break;
                default:
                    await ClearUsingBasicRmAsync(operation, dirs);
                    break;
            }
            
            if (operation.Status != ClearStatus.Cancelled)
            {
                operation.Status = ClearStatus.Completed;
                operation.BytesDeleted = initialSize; // Set to initial size on successful completion
            }
            
            operation.EndTime = DateTime.UtcNow;
            
            var duration = operation.EndTime.Value - operation.StartTime;
            _logger.LogInformation($"Cache clear operation {operation.Id} completed in {duration.TotalMinutes:F1} minutes. " +
                                  $"Cleared approximately {initialSize / (1024.0 * 1024.0 * 1024.0):F2} GB");
            
            await NotifyProgress(operation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in cache clear operation {operation.Id}");
            operation.Status = ClearStatus.Failed;
            operation.Error = ex.Message;
            operation.EndTime = DateTime.UtcNow;
            await NotifyProgress(operation);
        }
    }

    private async Task<DeletionMethod> DetermineBestDeletionMethod()
    {
        // Check for available commands
        var hasRsync = await CheckCommandExists("rsync");
        var hasFind = await CheckCommandExists("find");
        var hasParallel = await CheckCommandExists("parallel");
        
        if (hasRsync)
            return DeletionMethod.RsyncDelete;
        if (hasFind)
            return DeletionMethod.FindDelete;
        if (hasParallel)
            return DeletionMethod.ParallelRm;
        
        return DeletionMethod.BasicRm;
    }

    private async Task<bool> CheckCommandExists(string command)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = command,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task ClearUsingRsyncAsync(CacheClearOperation operation, List<string> dirs)
    {
        // Rsync with empty directory is one of the fastest methods for mass deletion
        var emptyDir = Path.Combine(Path.GetTempPath(), $"empty_{Guid.NewGuid()}");
        Directory.CreateDirectory(emptyDir);
        
        try
        {
            foreach (var dir in dirs)
            {
                if (operation.CancellationTokenSource.Token.IsCancellationRequested)
                {
                    operation.Status = ClearStatus.Cancelled;
                    break;
                }
                
                var dirName = Path.GetFileName(dir);
                _logger.LogDebug($"Clearing directory {dirName} using rsync");
                
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "rsync",
                    Arguments = $"-a --delete \"{emptyDir}/\" \"{dir}/\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                
                process.Start();
                await process.WaitForExitAsync(operation.CancellationTokenSource.Token);
                
                if (process.ExitCode != 0)
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    _logger.LogWarning($"Rsync failed for {dirName}: {error}");
                    operation.Errors++;
                }
                
                operation.DirectoriesProcessed++;
                
                // Update progress
                var percentComplete = (operation.DirectoriesProcessed * 100.0) / operation.TotalDirectories;
                operation.BytesDeleted = (long)(operation.TotalBytesToDelete * (percentComplete / 100.0));
                
                if (operation.DirectoriesProcessed % 5 == 0)
                {
                    await NotifyProgress(operation);
                }
            }
        }
        finally
        {
            Directory.Delete(emptyDir, true);
        }
    }

    private async Task ClearUsingFindAsync(CacheClearOperation operation, List<string> dirs)
    {
        // Using find with -delete is much faster than rm for many files
        foreach (var dir in dirs)
        {
            if (operation.CancellationTokenSource.Token.IsCancellationRequested)
            {
                operation.Status = ClearStatus.Cancelled;
                break;
            }
            
            var dirName = Path.GetFileName(dir);
            _logger.LogDebug($"Clearing directory {dirName} using find -delete");
            
            // First delete all files, then remove empty directories
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "find",
                    Arguments = $"\"{dir}\" -type f -delete",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                
                process.Start();
                await process.WaitForExitAsync(operation.CancellationTokenSource.Token);
                
                if (process.ExitCode != 0)
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    _logger.LogWarning($"Find -delete failed for files in {dirName}: {error}");
                    operation.Errors++;
                }
            }
            
            // Remove empty directories (except the main cache directory)
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "find",
                    Arguments = $"\"{dir}\" -mindepth 1 -type d -empty -delete",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                
                process.Start();
                await process.WaitForExitAsync(operation.CancellationTokenSource.Token);
            }
            
            operation.DirectoriesProcessed++;
            
            // Update progress
            var percentComplete = (operation.DirectoriesProcessed * 100.0) / operation.TotalDirectories;
            operation.BytesDeleted = (long)(operation.TotalBytesToDelete * (percentComplete / 100.0));
            
            if (operation.DirectoriesProcessed % 5 == 0)
            {
                await NotifyProgress(operation);
            }
        }
    }

    private async Task ClearUsingParallelRmAsync(CacheClearOperation operation, List<string> dirs)
    {
        // Process multiple directories in parallel for faster deletion
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 4, // Limit to 4 parallel operations
            CancellationToken = operation.CancellationTokenSource.Token
        };
        
        await Parallel.ForEachAsync(dirs, parallelOptions, async (dir, ct) =>
        {
            if (ct.IsCancellationRequested)
                return;
            
            var dirName = Path.GetFileName(dir);
            _logger.LogDebug($"Clearing directory {dirName} using rm -rf");
            
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "sh",
                Arguments = $"-c \"rm -rf {dir}/* {dir}/.[!.]* {dir}/..?* 2>/dev/null\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            await process.WaitForExitAsync(ct);
            
            Interlocked.Increment(ref operation.DirectoriesProcessed);
            
            // Update progress
            var percentComplete = (operation.DirectoriesProcessed * 100.0) / operation.TotalDirectories;
            var estimatedBytesDeleted = (long)(operation.TotalBytesToDelete * (percentComplete / 100.0));
            Interlocked.Exchange(ref operation.BytesDeleted, estimatedBytesDeleted);
            
            if (operation.DirectoriesProcessed % 5 == 0)
            {
                await NotifyProgress(operation);
            }
        });
    }

    private async Task ClearUsingBasicRmAsync(CacheClearOperation operation, List<string> dirs)
    {
        // Fallback to basic rm -rf
        foreach (var dir in dirs)
        {
            if (operation.CancellationTokenSource.Token.IsCancellationRequested)
            {
                operation.Status = ClearStatus.Cancelled;
                break;
            }
            
            var dirName = Path.GetFileName(dir);
            _logger.LogDebug($"Clearing directory {dirName} using rm -rf");
            
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "sh",
                Arguments = $"-c \"rm -rf {dir}/* {dir}/.[!.]* {dir}/..?* 2>/dev/null\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            await process.WaitForExitAsync(operation.CancellationTokenSource.Token);
            
            operation.DirectoriesProcessed++;
            
            // Update progress
            var percentComplete = (operation.DirectoriesProcessed * 100.0) / operation.TotalDirectories;
            operation.BytesDeleted = (long)(operation.TotalBytesToDelete * (percentComplete / 100.0));
            
            if (operation.DirectoriesProcessed % 5 == 0)
            {
                await NotifyProgress(operation);
            }
        }
    }

    private async Task<long> EstimateCacheSizeAsync(string path)
    {
        try
        {
            // Use du for a quick size estimate (with timeout)
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "du",
                Arguments = $"-sb \"{path}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            
            // Wait max 10 seconds for size calculation
            var completed = await Task.Run(() => process.WaitForExit(10000));
            
            if (completed && process.ExitCode == 0)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var parts = output.Split('\t');
                if (parts.Length > 0 && long.TryParse(parts[0], out var size))
                {
                    return size;
                }
            }
            else
            {
                process.Kill(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to estimate cache size, using default");
        }
        
        // Return a default estimate if we can't calculate
        return 100L * 1024 * 1024 * 1024; // 100GB default estimate
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
                Service = operation.Service,
                StartTime = operation.StartTime,
                EndTime = operation.EndTime,
                DirectoriesProcessed = operation.DirectoriesProcessed,
                TotalDirectories = operation.TotalDirectories,
                FilesDeleted = operation.FilesDeleted,
                BytesDeleted = operation.BytesDeleted,
                SubdirectoriesDeleted = operation.SubdirectoriesDeleted,
                Errors = operation.Errors,
                Error = operation.Error,
                PercentComplete = operation.TotalDirectories > 0 
                    ? (operation.DirectoriesProcessed * 100.0 / operation.TotalDirectories) 
                    : 0
            };
            
            await _hubContext.Clients.All.SendAsync("CacheClearProgress", progress);
            
            _logger.LogDebug($"Cache clear progress: {progress.PercentComplete:F1}% - " +
                           $"~{progress.BytesDeleted / (1024.0 * 1024.0 * 1024.0):F2} GB deleted");
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
                Service = operation.Service,
                StartTime = operation.StartTime,
                EndTime = operation.EndTime,
                DirectoriesProcessed = operation.DirectoriesProcessed,
                TotalDirectories = operation.TotalDirectories,
                FilesDeleted = operation.FilesDeleted,
                BytesDeleted = operation.BytesDeleted,
                SubdirectoriesDeleted = operation.SubdirectoriesDeleted,
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
            operation.CancellationTokenSource?.Cancel();
            operation.Status = ClearStatus.Cancelled;
            operation.EndTime = DateTime.UtcNow;
            _logger.LogInformation($"Cache clear operation {operationId} cancelled");
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
    public string? Error { get; set; }
    public int TotalDirectories { get; set; }
    public int DirectoriesProcessed;
    public long FilesDeleted;
    public long BytesDeleted;
    public long TotalBytesToDelete;
    public int SubdirectoriesDeleted;
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

public enum DeletionMethod
{
    RsyncDelete,    // Fastest - uses rsync --delete with empty directory
    FindDelete,     // Fast - uses find -delete
    ParallelRm,     // Medium - parallel rm -rf
    BasicRm         // Slowest - sequential rm -rf
}

public class CacheClearProgress
{
    public string OperationId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Service { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int DirectoriesProcessed { get; set; }
    public int TotalDirectories { get; set; }
    public long FilesDeleted { get; set; }
    public long BytesDeleted { get; set; }
    public int SubdirectoriesDeleted { get; set; }
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