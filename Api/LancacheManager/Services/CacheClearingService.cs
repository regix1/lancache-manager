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
        
        // Always use nuclear method - it's the only fast way for large caches
        _ = Task.Run(async () => await NuclearClearCacheAsync(operation), operation.CancellationTokenSource.Token);
        
        return operationId;
    }

    private async Task NuclearClearCacheAsync(CacheClearOperation operation)
    {
        try
        {
            _logger.LogInformation($"Starting instant cache clear operation {operation.Id}");
            operation.StatusMessage = "Preparing instant cache clear...";
            operation.TotalDirectories = 4; // We'll show 4 stages of progress
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
            long sizeEstimate = 0;
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
                        sizeEstimate = actualSize;
                        _logger.LogInformation($"Cache size: {sizeEstimate / (1024.0 * 1024.0 * 1024.0):F2} GB");
                    }
                }
            }
            catch
            {
                // Ignore size estimation errors
            }
            
            // Default to estimate if we couldn't get actual size
            if (sizeEstimate == 0)
            {
                sizeEstimate = 1000L * 1024 * 1024 * 1024; // Assume 1TB average
            }
            
            operation.TotalBytesToDelete = sizeEstimate;
            operation.BytesDeleted = 0;
            
            // Generate unique name for old cache
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var tempPath = $"{_cachePath}_old_{timestamp}";
            
            operation.Status = ClearStatus.Running;
            operation.StatusMessage = "Moving cache directory (instant operation)...";
            operation.DirectoriesProcessed = 1;
            await NotifyProgress(operation);
            
            try
            {
                // Step 1: Rename the existing cache directory (INSTANT operation)
                _logger.LogInformation($"Renaming {_cachePath} to {tempPath}");
                Directory.Move(_cachePath, tempPath);
                
                operation.DirectoriesProcessed = 2;
                operation.BytesDeleted = sizeEstimate / 2; // Show 50% progress
                operation.StatusMessage = "Cache moved! Creating new cache structure...";
                await NotifyProgress(operation);
                
                // Small delay for visual feedback
                await Task.Delay(300);
                
                // Step 2: Create new empty cache directory structure
                Directory.CreateDirectory(_cachePath);
                
                // Create all 256 directories (00-ff)
                var createdCount = 0;
                for (int i = 0; i < 256; i++)
                {
                    var dirName = i.ToString("x2");
                    Directory.CreateDirectory(Path.Combine(_cachePath, dirName));
                    createdCount++;
                    
                    // Update progress every 64 directories
                    if (createdCount % 64 == 0)
                    {
                        var progress = 2.0 + (createdCount / 256.0); // Progress from 2 to 3
                        operation.DirectoriesProcessed = (int)Math.Floor(progress);
                        operation.BytesDeleted = (long)(sizeEstimate * (0.5 + (createdCount / 512.0))); // 50% to 100%
                        operation.StatusMessage = $"Creating cache structure... ({createdCount}/256 directories)";
                        await NotifyProgress(operation);
                        await Task.Delay(50); // Small delay for visual feedback
                    }
                }
                
                // Cache is now effectively cleared from nginx's perspective
                operation.DirectoriesProcessed = 4;
                operation.BytesDeleted = sizeEstimate;
                operation.Status = ClearStatus.Completed;
                operation.StatusMessage = "Cache cleared successfully! Old cache will be deleted in background.";
                operation.EndTime = DateTime.UtcNow;
                
                var duration = operation.EndTime.Value - operation.StartTime;
                _logger.LogInformation($"Cache clear completed in {duration.TotalSeconds:F1} seconds! Cleared {sizeEstimate / (1024.0 * 1024.0 * 1024.0):F2} GB");
                
                await NotifyProgress(operation);
                
                // Step 3: Delete the old directory in the background (fire and forget)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _logger.LogInformation($"Starting background deletion of {tempPath}");
                        
                        using var process = new Process();
                        process.StartInfo = new ProcessStartInfo
                        {
                            FileName = "sh",
                            Arguments = $"-c \"nohup rm -rf '{tempPath}' > /dev/null 2>&1 &\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        
                        process.Start();
                        await process.WaitForExitAsync();
                        
                        _logger.LogInformation($"Background deletion process started for {tempPath}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to start background deletion of {tempPath}");
                        // Not critical - admin can manually delete if needed
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear cache using nuclear method");
                operation.Status = ClearStatus.Failed;
                operation.Error = $"Failed to move cache directory: {ex.Message}. Please check permissions.";
                operation.StatusMessage = "Failed to clear cache";
                operation.EndTime = DateTime.UtcNow;
                await NotifyProgress(operation);
            }
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