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
    private const int DIRECTORY_TIMEOUT_SECONDS = 120; // 2 minutes per directory max

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
            operation.StatusMessage = "Preparing cache clear operation...";
            await NotifyProgress(operation);
            
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
                .OrderBy(d => d) // Process in order for predictable progress
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
            
            // Skip size estimation if it takes too long - just use a rough estimate
            operation.StatusMessage = "Starting cache clear...";
            await NotifyProgress(operation);
            
            // Use a rough estimate based on typical cache sizes (10GB per directory average)
            var initialSize = (long)dirs.Count * 10L * 1024 * 1024 * 1024; // Rough estimate
            operation.BytesDeleted = 0;
            operation.TotalBytesToDelete = initialSize;
            
            _logger.LogInformation($"Found {dirs.Count} cache directories to clear");
            
            // Choose deletion method
            var deletionMethod = await DetermineBestDeletionMethod();
            _logger.LogInformation($"Using deletion method: {deletionMethod}");
            operation.StatusMessage = $"Clearing cache using {deletionMethod} method...";
            await NotifyProgress(operation);

            // Process directories one by one with progress updates
            foreach (var dir in dirs)
            {
                if (operation.CancellationTokenSource.Token.IsCancellationRequested)
                {
                    operation.Status = ClearStatus.Cancelled;
                    break;
                }
                
                var dirName = Path.GetFileName(dir);
                operation.CurrentDirectory = dirName;
                operation.StatusMessage = $"Clearing directory {dirName} ({operation.DirectoriesProcessed + 1}/{operation.TotalDirectories})...";
                
                try
                {
                    // Clear directory with timeout
                    var cleared = await ClearDirectoryWithTimeoutAsync(dir, deletionMethod, DIRECTORY_TIMEOUT_SECONDS, operation);
                    
                    if (cleared)
                    {
                        operation.DirectoriesProcessed++;
                        // Update bytes deleted based on progress
                        operation.BytesDeleted = (long)(initialSize * ((double)operation.DirectoriesProcessed / dirs.Count));
                    }
                    else
                    {
                        operation.SkippedDirectories++;
                        _logger.LogWarning($"Skipped directory {dirName} due to timeout or error");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error clearing directory {dirName}");
                    operation.Errors++;
                    operation.SkippedDirectories++;
                }
                
                // Calculate ETA
                if (operation.DirectoriesProcessed > 0)
                {
                    var elapsed = DateTime.UtcNow - operation.StartTime;
                    var avgTimePerDir = elapsed.TotalSeconds / operation.DirectoriesProcessed;
                    var remainingDirs = dirs.Count - operation.DirectoriesProcessed;
                    var estimatedSecondsRemaining = (int)(avgTimePerDir * remainingDirs);
                    operation.EstimatedSecondsRemaining = estimatedSecondsRemaining;
                }
                
                // Update progress after each directory
                await NotifyProgress(operation);
            }
            
            if (operation.Status != ClearStatus.Cancelled)
            {
                operation.Status = ClearStatus.Completed;
                operation.StatusMessage = "Cache clearing completed!";
            }
            else
            {
                operation.StatusMessage = "Cache clearing cancelled";
            }
            
            operation.EndTime = DateTime.UtcNow;
            
            var duration = operation.EndTime.Value - operation.StartTime;
            _logger.LogInformation($"Cache clear operation {operation.Id} completed in {duration.TotalMinutes:F1} minutes. " +
                                  $"Cleared {operation.DirectoriesProcessed} directories, skipped {operation.SkippedDirectories}");
            
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

    private async Task<bool> ClearDirectoryWithTimeoutAsync(string dir, DeletionMethod method, int timeoutSeconds, CacheClearOperation operation)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(operation.CancellationTokenSource.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        
        try
        {
            switch (method)
            {
                case DeletionMethod.RsyncDelete:
                    return await ClearUsingRsyncAsync(dir, cts.Token);
                case DeletionMethod.FindDelete:
                    return await ClearUsingFindAsync(dir, cts.Token);
                case DeletionMethod.ParallelRm:
                    return await ClearUsingRmAsync(dir, cts.Token);
                default:
                    return await ClearUsingRmAsync(dir, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning($"Timeout clearing directory {Path.GetFileName(dir)} after {timeoutSeconds} seconds");
            
            // Try to kill any hanging processes
            await KillHangingProcessesAsync(dir);
            
            return false;
        }
    }

    private async Task<bool> ClearUsingRsyncAsync(string dir, CancellationToken cancellationToken)
    {
        var dirName = Path.GetFileName(dir);
        var emptyDir = Path.Combine(Path.GetTempPath(), $"empty_{Guid.NewGuid()}");
        Directory.CreateDirectory(emptyDir);
        
        try
        {
            _logger.LogDebug($"Clearing {dirName} with rsync");
            
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "timeout", // Use timeout command to ensure process doesn't hang
                Arguments = $"60 rsync -a --delete \"{emptyDir}/\" \"{dir}/\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            
            process.Start();
            
            // Wait for process with cancellation
            var completed = await WaitForProcessAsync(process, 60000, cancellationToken);
            
            if (!completed)
            {
                _logger.LogWarning($"Rsync timeout for {dirName}");
                try { process.Kill(true); } catch { }
                return false;
            }
            
            if (process.ExitCode != 0 && process.ExitCode != 124) // 124 is timeout exit code
            {
                var error = await process.StandardError.ReadToEndAsync();
                _logger.LogWarning($"Rsync failed for {dirName}: {error}");
                return false;
            }
            
            _logger.LogDebug($"Successfully cleared {dirName} with rsync");
            return true;
        }
        finally
        {
            try { Directory.Delete(emptyDir, true); } catch { }
        }
    }

    private async Task<bool> ClearUsingFindAsync(string dir, CancellationToken cancellationToken)
    {
        var dirName = Path.GetFileName(dir);
        _logger.LogDebug($"Clearing {dirName} with find -delete");
        
        // First delete all files
        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "timeout",
                Arguments = $"60 find \"{dir}\" -type f -delete",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            
            process.Start();
            
            var completed = await WaitForProcessAsync(process, 60000, cancellationToken);
            
            if (!completed)
            {
                _logger.LogWarning($"Find timeout for {dirName}");
                try { process.Kill(true); } catch { }
                return false;
            }
        }
        
        // Then remove empty directories (quick operation)
        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "timeout",
                Arguments = $"10 find \"{dir}\" -mindepth 1 -type d -empty -delete",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            await WaitForProcessAsync(process, 10000, cancellationToken);
        }
        
        _logger.LogDebug($"Successfully cleared {dirName} with find");
        return true;
    }

    private async Task<bool> ClearUsingRmAsync(string dir, CancellationToken cancellationToken)
    {
        var dirName = Path.GetFileName(dir);
        _logger.LogDebug($"Clearing {dirName} with rm -rf");
        
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "timeout",
            Arguments = $"60 sh -c \"rm -rf {dir}/* {dir}/.[!.]* {dir}/..?* 2>/dev/null\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        process.Start();
        
        var completed = await WaitForProcessAsync(process, 60000, cancellationToken);
        
        if (!completed)
        {
            _logger.LogWarning($"Rm timeout for {dirName}");
            try { process.Kill(true); } catch { }
            return false;
        }
        
        _logger.LogDebug($"Successfully cleared {dirName} with rm");
        return process.ExitCode == 0 || process.ExitCode == 124; // 124 is timeout exit code
    }

    private async Task<bool> WaitForProcessAsync(Process process, int timeoutMs, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>();
        
        process.EnableRaisingEvents = true;
        process.Exited += (sender, args) => tcs.TrySetResult(true);
        
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);
        
        using (cts.Token.Register(() => tcs.TrySetResult(false)))
        {
            return await tcs.Task;
        }
    }

    private async Task KillHangingProcessesAsync(string dir)
    {
        try
        {
            // Kill any rm, rsync, or find processes operating on this directory
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "sh",
                Arguments = $"-c \"pkill -f '{Path.GetFileName(dir)}' || true\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error killing hanging processes");
        }
    }

    private async Task<long> EstimateCacheSizeQuickAsync(string path)
    {
        try
        {
            // Try to get a quick size estimate - timeout after 5 seconds
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "timeout",
                Arguments = $"5 du -sb \"{path}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            await process.WaitForExitAsync();
            
            if (process.ExitCode == 0)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var parts = output.Split('\t');
                if (parts.Length > 0 && long.TryParse(parts[0], out var size))
                {
                    return size;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to estimate cache size");
        }
        
        // Return a default estimate based on typical cache
        return 100L * 1024 * 1024 * 1024; // 100GB default
    }

    private async Task<DeletionMethod> DetermineBestDeletionMethod()
    {
        try
        {
            // Quick check for rsync only - it's the fastest
            if (File.Exists("/usr/bin/rsync") || File.Exists("/bin/rsync"))
            {
                _logger.LogInformation("Found rsync, using fastest deletion method");
                return DeletionMethod.RsyncDelete;
            }
            
            // Default to find if available
            if (File.Exists("/usr/bin/find") || File.Exists("/bin/find"))
            {
                _logger.LogInformation("Found find, using fast deletion method");
                return DeletionMethod.FindDelete;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking for commands, using basic rm");
        }
        
        _logger.LogInformation("Using basic rm deletion method");
        return DeletionMethod.BasicRm;
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
                CurrentDirectory = operation.CurrentDirectory,
                SkippedDirectories = operation.SkippedDirectories,
                FilesDeleted = operation.FilesDeleted,
                BytesDeleted = operation.BytesDeleted,
                SubdirectoriesDeleted = operation.SubdirectoriesDeleted,
                Errors = operation.Errors,
                Error = operation.Error,
                EstimatedSecondsRemaining = operation.EstimatedSecondsRemaining,
                PercentComplete = operation.TotalDirectories > 0 
                    ? (operation.DirectoriesProcessed * 100.0 / operation.TotalDirectories) 
                    : 0
            };
            
            await _hubContext.Clients.All.SendAsync("CacheClearProgress", progress);
            
            _logger.LogDebug($"Cache clear progress: {progress.PercentComplete:F1}% - " +
                           $"Dir {operation.CurrentDirectory} - ~{progress.BytesDeleted / (1024.0 * 1024.0 * 1024.0):F2} GB deleted");
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
                CurrentDirectory = operation.CurrentDirectory,
                SkippedDirectories = operation.SkippedDirectories,
                FilesDeleted = operation.FilesDeleted,
                BytesDeleted = operation.BytesDeleted,
                SubdirectoriesDeleted = operation.SubdirectoriesDeleted,
                Errors = operation.Errors,
                Error = operation.Error,
                EstimatedSecondsRemaining = operation.EstimatedSecondsRemaining,
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
            operation.StatusMessage = "Operation cancelled by user";
            operation.EndTime = DateTime.UtcNow;
            
            // Send immediate notification before cancelling token
            _ = NotifyProgress(operation);
            
            // Then cancel the operation
            operation.CancellationTokenSource?.Cancel();
            
            // Kill any running processes
            _ = Task.Run(async () =>
            {
                try
                {
                    // Kill any rm, rsync, or find processes
                    using var process = new Process();
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = "sh",
                        Arguments = "-c \"pkill -f 'rsync|find.*delete|rm.*rf' || true\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    process.Start();
                    await process.WaitForExitAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error killing processes during cancel");
                }
            });
            
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
    public string CurrentDirectory { get; set; } = string.Empty;
    public string? Error { get; set; }
    public int TotalDirectories { get; set; }
    public int DirectoriesProcessed;
    public int SkippedDirectories;
    public long FilesDeleted;
    public long BytesDeleted;
    public long TotalBytesToDelete;
    public int SubdirectoriesDeleted;
    public int Errors;
    public int EstimatedSecondsRemaining;
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
    public string StatusMessage { get; set; } = string.Empty;
    public string CurrentDirectory { get; set; } = string.Empty;
    public string? Service { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int DirectoriesProcessed { get; set; }
    public int TotalDirectories { get; set; }
    public int SkippedDirectories { get; set; }
    public long FilesDeleted { get; set; }
    public long BytesDeleted { get; set; }
    public int SubdirectoriesDeleted { get; set; }
    public int Errors { get; set; }
    public string? Error { get; set; }
    public int EstimatedSecondsRemaining { get; set; }
    public double PercentComplete { get; set; }
    public string BytesDeletedFormatted => FormatBytes(BytesDeleted);
    public string EstimatedTimeRemaining => FormatTime(EstimatedSecondsRemaining);
    
    private string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
    
    private string FormatTime(int seconds)
    {
        if (seconds <= 0) return "";
        if (seconds < 60) return $"{seconds} seconds";
        if (seconds < 3600) return $"{seconds / 60} minutes";
        return $"{seconds / 3600:F1} hours";
    }
}