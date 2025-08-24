using System.Collections.Concurrent;
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
        // Cleanup old operations every 5 minutes
        _cleanupTimer = new Timer(CleanupOldOperations, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Dispose();
        
        // Cancel all ongoing operations
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
        _ = Task.Run(async () => await ClearCacheAsync(operation), operation.CancellationTokenSource.Token);
        
        return operationId;
    }

    private async Task ClearCacheAsync(CacheClearOperation operation)
    {
        try
        {
            _logger.LogInformation($"Starting cache clear operation {operation.Id} for service: {operation.Service ?? "all"}");
            
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
            await NotifyProgress(operation);
            
            _logger.LogInformation($"Found {dirs.Count} cache directories to clear");

            // Process directories in parallel but limit concurrency
            var semaphore = new SemaphoreSlim(4); // Process 4 directories at a time
            var tasks = new List<Task>();
            
            foreach (var dir in dirs)
            {
                if (operation.CancellationTokenSource.Token.IsCancellationRequested)
                {
                    operation.Status = ClearStatus.Cancelled;
                    break;
                }
                
                tasks.Add(ProcessDirectoryAsync(dir, operation, semaphore));
            }
            
            await Task.WhenAll(tasks);
            
            if (operation.Status != ClearStatus.Cancelled)
            {
                operation.Status = ClearStatus.Completed;
            }
            
            operation.EndTime = DateTime.UtcNow;
            
            var duration = operation.EndTime.Value - operation.StartTime;
            _logger.LogInformation($"Cache clear operation {operation.Id} completed in {duration.TotalMinutes:F1} minutes. " +
                                  $"Cleared {operation.FilesDeleted} files, {operation.BytesDeleted / (1024.0 * 1024.0 * 1024.0):F2} GB");
            
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

    private async Task ProcessDirectoryAsync(string dir, CacheClearOperation operation, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            if (operation.CancellationTokenSource.Token.IsCancellationRequested)
                return;
            
            var dirName = Path.GetFileName(dir);
            _logger.LogDebug($"Processing directory: {dirName}");
            
            // Count and delete files
            await DeleteFilesRecursivelyAsync(dir, operation);
            
            // Delete subdirectories but keep the main directory (00-ff)
            try
            {
                var subdirs = Directory.GetDirectories(dir);
                foreach (var subdir in subdirs)
                {
                    if (operation.CancellationTokenSource.Token.IsCancellationRequested)
                        break;
                    
                    try
                    {
                        Directory.Delete(subdir, true);
                        operation.SubdirectoriesDeleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to delete subdirectory: {subdir}");
                        operation.Errors++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to enumerate subdirectories in: {dir}");
            }
            
            Interlocked.Increment(ref operation.DirectoriesProcessed);
            
            // Send progress update every 5 directories
            if (operation.DirectoriesProcessed % 5 == 0)
            {
                await NotifyProgress(operation);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to process directory: {dir}");
            operation.Errors++;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task DeleteFilesRecursivelyAsync(string directory, CacheClearOperation operation)
    {
        try
        {
            // Process files in batches to avoid memory issues
            const int batchSize = 1000;
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
            var batch = new List<string>(batchSize);
            
            foreach (var file in files)
            {
                if (operation.CancellationTokenSource.Token.IsCancellationRequested)
                    break;
                
                batch.Add(file);
                
                if (batch.Count >= batchSize)
                {
                    await ProcessFileBatchAsync(batch, operation);
                    batch.Clear();
                }
            }
            
            // Process remaining files
            if (batch.Count > 0)
            {
                await ProcessFileBatchAsync(batch, operation);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Error enumerating files in {directory}");
            operation.Errors++;
        }
    }

    private async Task ProcessFileBatchAsync(List<string> files, CacheClearOperation operation)
    {
        await Task.Run(() =>
        {
            foreach (var file in files)
            {
                if (operation.CancellationTokenSource.Token.IsCancellationRequested)
                    break;
                
                try
                {
                    var fileInfo = new FileInfo(file);
                    var size = fileInfo.Length;
                    
                    File.Delete(file);
                    
                    Interlocked.Add(ref operation.BytesDeleted, size);
                    Interlocked.Increment(ref operation.FilesDeleted);
                    
                    // Update progress every 100 files
                    if (operation.FilesDeleted % 100 == 0)
                    {
                        _ = NotifyProgress(operation);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogTrace($"Failed to delete file {file}: {ex.Message}");
                    operation.Errors++;
                }
            }
        });
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
                           $"{progress.FilesDeleted} files, {progress.BytesDeleted / (1024.0 * 1024.0):F1} MB deleted");
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