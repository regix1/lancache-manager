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
    
    // For better performance tracking
    private readonly SemaphoreSlim _clearingSemaphore = new(1, 1); // Only one clear operation at a time

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
        LoadPersistedOperations();
        _cleanupTimer = new Timer(CleanupOldOperations, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        CheckForActiveOperations();
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
            CancellationTokenSource = new CancellationTokenSource()
        };
        
        _operations[operationId] = operation;
        SavePersistedOperations();
        
        // Start the improved clear method
        _ = Task.Run(async () => await ClearCacheImproved(operation), operation.CancellationTokenSource.Token);
        
        return operationId;
    }

    private async Task ClearCacheImproved(CacheClearOperation operation)
    {
        await _clearingSemaphore.WaitAsync();
        try
        {
            _logger.LogInformation($"Starting cache clear operation {operation.Id}");
            operation.StatusMessage = "Calculating cache size...";
            operation.Status = ClearStatus.Preparing;
            await NotifyProgress(operation);
            SavePersistedOperations();
            
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
            
            // Calculate actual cache size (with progress updates)
            _logger.LogInformation("Calculating actual cache size...");
            operation.StatusMessage = "Calculating cache size...";
            
            long totalSize = 0;
            var sizeCalculationTasks = new List<Task<long>>();
            var dirIndex = 0;
            
            foreach (var dir in hexDirs)
            {
                var currentIndex = dirIndex++;
                sizeCalculationTasks.Add(Task.Run(() => CalculateDirectorySize(dir, operation, currentIndex, hexDirs.Count)));
            }
            
            var sizes = await Task.WhenAll(sizeCalculationTasks);
            totalSize = sizes.Sum();
            
            operation.TotalBytesToDelete = totalSize;
            _logger.LogInformation($"Total cache size: {FormatBytes(totalSize)}");
            
            operation.Status = ClearStatus.Running;
            operation.StatusMessage = "Clearing cache files...";
            await NotifyProgress(operation);
            SavePersistedOperations();
            
            // Clear cache with actual byte tracking
            var success = await ClearWithByteTracking(operation, hexDirs);
            
            if (success)
            {
                operation.Status = ClearStatus.Completed;
                operation.StatusMessage = $"Successfully cleared {FormatBytes(operation.BytesDeleted)}";
                operation.EndTime = DateTime.UtcNow;
                
                var duration = operation.EndTime.Value - operation.StartTime;
                _logger.LogInformation($"Cache clear completed in {duration.TotalSeconds:F1} seconds - Cleared {FormatBytes(operation.BytesDeleted)}");
            }
            else if (operation.CancellationTokenSource?.Token.IsCancellationRequested == true)
            {
                operation.Status = ClearStatus.Cancelled;
                operation.StatusMessage = $"Operation cancelled - Cleared {FormatBytes(operation.BytesDeleted)} before cancellation";
                operation.EndTime = DateTime.UtcNow;
            }
            else
            {
                operation.Status = ClearStatus.Failed;
                operation.Error = "Failed to clear cache";
                operation.StatusMessage = $"Cache clear failed - Partially cleared {FormatBytes(operation.BytesDeleted)}";
                operation.EndTime = DateTime.UtcNow;
            }
            
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
        finally
        {
            _clearingSemaphore.Release();
        }
    }

    private async Task<long> CalculateDirectorySize(string directory, CacheClearOperation operation, int index, int total)
    {
        try
        {
            long size = 0;
            
            // Use parallel processing for subdirectories
            var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            
            // Process files in batches for better performance
            const int batchSize = 100;
            for (int i = 0; i < files.Length; i += batchSize)
            {
                var batch = files.Skip(i).Take(batchSize);
                var batchSizes = await Task.Run(() =>
                {
                    return batch.Select(file =>
                    {
                        try
                        {
                            var fileInfo = new FileInfo(file);
                            return fileInfo.Length;
                        }
                        catch
                        {
                            return 0L;
                        }
                    }).ToArray();
                });
                
                size += batchSizes.Sum();
                
                // Update progress during calculation
                if (i % 1000 == 0)
                {
                    operation.StatusMessage = $"Calculating size... ({index + 1}/{total} directories)";
                    await NotifyProgress(operation);
                }
            }
            
            return size;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Error calculating size for directory {directory}");
            return 0;
        }
    }

    private async Task<bool> ClearWithByteTracking(CacheClearOperation operation, List<string> hexDirs)
    {
        try
        {
            _logger.LogInformation("Starting cache clear with byte tracking");
            
            // Process directories in parallel batches for better performance
            const int parallelism = 4; // Process 4 directories at once
            var semaphore = new SemaphoreSlim(parallelism);
            var tasks = new List<Task<(bool Success, long BytesDeleted)>>();
            
            for (int i = 0; i < hexDirs.Count; i++)
            {
                if (operation.CancellationTokenSource?.Token.IsCancellationRequested == true)
                    break;
                    
                var dir = hexDirs[i];
                var dirIndex = i;
                
                await semaphore.WaitAsync();
                
                var task = Task.Run(async () =>
                {
                    try
                    {
                        var bytesDeleted = await ClearDirectory(dir, operation);
                        
                        // Update progress
                        lock (operation)
                        {
                            operation.BytesDeleted += bytesDeleted;
                            operation.DirectoriesProcessed++;
                        }
                        
                        // Send progress update
                        operation.StatusMessage = $"Clearing cache... ({operation.DirectoriesProcessed}/{hexDirs.Count} directories) - {FormatBytes(operation.BytesDeleted)} cleared";
                        await NotifyProgress(operation);
                        
                        // Save progress periodically
                        if (operation.DirectoriesProcessed % 10 == 0)
                        {
                            SavePersistedOperations();
                        }
                        
                        return (true, bytesDeleted);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                
                tasks.Add(task);
            }
            
            var results = await Task.WhenAll(tasks);
            
            var totalBytesDeleted = results.Sum(r => r.BytesDeleted);
            var successCount = results.Count(r => r.Success);
            
            _logger.LogInformation($"Cleared {successCount}/{hexDirs.Count} directories, {FormatBytes(totalBytesDeleted)} total");
            
            return successCount > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear cache with byte tracking");
            return false;
        }
    }

    private async Task<long> ClearDirectory(string directory, CacheClearOperation operation)
    {
        long bytesDeleted = 0;
        
        try
        {
            // Get all files in the directory
            var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            
            // Process files in batches
            const int batchSize = 100;
            for (int i = 0; i < files.Length; i += batchSize)
            {
                if (operation.CancellationTokenSource?.Token.IsCancellationRequested == true)
                    break;
                    
                var batch = files.Skip(i).Take(batchSize).ToArray();
                
                // Delete files and track bytes
                var batchBytes = await Task.Run(() =>
                {
                    long bytes = 0;
                    foreach (var file in batch)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(file);
                            var fileSize = fileInfo.Length;
                            
                            File.Delete(file);
                            bytes += fileSize;
                        }
                        catch (Exception ex)
                        {
                            // Log but continue with other files
                            _logger.LogTrace($"Failed to delete file {file}: {ex.Message}");
                        }
                    }
                    return bytes;
                });
                
                bytesDeleted += batchBytes;
                
                // Update progress more frequently for large directories
                if (i % 500 == 0 && i > 0)
                {
                    lock (operation)
                    {
                        operation.BytesDeleted += batchBytes;
                    }
                    
                    operation.StatusMessage = $"Processing {Path.GetFileName(directory)}... {FormatBytes(operation.BytesDeleted)} cleared";
                    await NotifyProgress(operation);
                }
            }
            
            // Clean up empty subdirectories
            try
            {
                var subDirs = Directory.GetDirectories(directory);
                foreach (var subDir in subDirs)
                {
                    try
                    {
                        Directory.Delete(subDir, true);
                    }
                    catch
                    {
                        // Ignore errors when deleting directories
                    }
                }
            }
            catch
            {
                // Ignore directory cleanup errors
            }
            
            _logger.LogDebug($"Cleared {directory}: {FormatBytes(bytesDeleted)}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Error clearing directory {directory}");
        }
        
        return bytesDeleted;
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
                PercentComplete = operation.TotalBytesToDelete > 0 
                    ? (operation.BytesDeleted * 100.0 / operation.TotalBytesToDelete) 
                    : (operation.TotalDirectories > 0 
                        ? (operation.DirectoriesProcessed * 100.0 / operation.TotalDirectories) 
                        : 0)
            };
            
            await _hubContext.Clients.All.SendAsync("CacheClearProgress", progress);
            
            _logger.LogDebug($"Cache clear progress: {progress.PercentComplete:F1}% - {FormatBytes(operation.BytesDeleted)} / {FormatBytes(operation.TotalBytesToDelete)}");
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
                var operations = JsonSerializer.Deserialize<List<CacheClearOperation>>(json);
                
                if (operations != null)
                {
                    foreach (var op in operations)
                    {
                        // Only load recent or active operations
                        if (op.Status == ClearStatus.Running || 
                            op.Status == ClearStatus.Preparing ||
                            (op.StartTime > DateTime.UtcNow.AddHours(-1)))
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
            var json = JsonSerializer.Serialize(operations, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_statusFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save persisted operations");
        }
    }

    private void CheckForActiveOperations()
    {
        var activeOps = _operations.Values.Where(op => 
            op.Status == ClearStatus.Running || op.Status == ClearStatus.Preparing).ToList();
        
        if (activeOps.Any())
        {
            _logger.LogInformation($"Found {activeOps.Count} active operations on startup");
            
            // Mark them as failed since we can't resume them
            foreach (var op in activeOps)
            {
                op.Status = ClearStatus.Failed;
                op.Error = "Operation interrupted by service restart";
                op.EndTime = DateTime.UtcNow;
            }
            
            SavePersistedOperations();
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
                PercentComplete = operation.TotalBytesToDelete > 0 
                    ? (operation.BytesDeleted * 100.0 / operation.TotalBytesToDelete) 
                    : (operation.TotalDirectories > 0 
                        ? (operation.DirectoriesProcessed * 100.0 / operation.TotalDirectories) 
                        : 0)
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
            PercentComplete = op.TotalBytesToDelete > 0 
                ? (op.BytesDeleted * 100.0 / op.TotalBytesToDelete) 
                : (op.TotalDirectories > 0 
                    ? (op.DirectoriesProcessed * 100.0 / op.TotalDirectories) 
                    : 0)
        }).ToList();
    }

    public bool CancelOperation(string operationId)
    {
        if (_operations.TryGetValue(operationId, out var operation))
        {
            _logger.LogInformation($"Cancelling cache clear operation {operationId}");
            
            operation.Status = ClearStatus.Cancelled;
            operation.StatusMessage = $"Operation cancelled - Cleared {FormatBytes(operation.BytesDeleted)}";
            operation.EndTime = DateTime.UtcNow;
            operation.CancellationTokenSource?.Cancel();
            
            _ = NotifyProgress(operation);
            SavePersistedOperations();
            
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

// Keep the existing model classes unchanged
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
    public string? BackgroundProcessId { get; set; }
    
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