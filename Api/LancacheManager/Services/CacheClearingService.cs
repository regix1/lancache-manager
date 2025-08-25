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

            // Quick size estimation using du command (faster than counting files)
            operation.StatusMessage = "Estimating cache size...";
            await NotifyProgress(operation);
            
            long estimatedSize = await EstimateCacheSizeQuick();
            operation.TotalBytesToDelete = estimatedSize;
            
            _logger.LogInformation($"Estimated cache size: {FormatBytes(estimatedSize)}");
            
            // Start clearing
            operation.Status = ClearStatus.Running;
            operation.StatusMessage = "Clearing cache files...";
            await NotifyProgress(operation);
            SavePersistedOperations();
            
            // Clear directories one by one with progress tracking
            long totalBytesDeleted = 0;
            int dirsProcessed = 0;
            
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
                
                // Get size of this directory before deleting
                long dirSize = await GetDirectorySizeQuick(dir);
                
                // Delete the directory contents
                bool success = await DeleteDirectoryContents(dir);
                
                if (success)
                {
                    totalBytesDeleted += dirSize;
                    operation.BytesDeleted = totalBytesDeleted;
                }
                
                dirsProcessed++;
                operation.DirectoriesProcessed = dirsProcessed;
                
                // Update progress
                operation.StatusMessage = $"Clearing cache... ({dirsProcessed}/{hexDirs.Count} directories)";
                
                // Calculate percentage based on directories if we don't have size, otherwise use bytes
                if (operation.TotalBytesToDelete > 0)
                {
                    operation.PercentComplete = (totalBytesDeleted * 100.0) / operation.TotalBytesToDelete;
                }
                else
                {
                    operation.PercentComplete = (dirsProcessed * 100.0) / hexDirs.Count;
                }
                
                // Send update every 5 directories or every 5% progress
                if (dirsProcessed % 5 == 0 || (int)operation.PercentComplete % 5 == 0)
                {
                    await NotifyProgress(operation);
                    
                    // Save state every 10 directories
                    if (dirsProcessed % 10 == 0)
                    {
                        SavePersistedOperations();
                    }
                }
            }
            
            // Final update
            operation.Status = ClearStatus.Completed;
            operation.StatusMessage = $"Successfully cleared {FormatBytes(totalBytesDeleted)}";
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
            // Use du command for quick size estimation (with timeout)
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "timeout",
                Arguments = $"5 du -sb {_cachePath}", // 5 second timeout
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
                if (parts.Length > 0 && long.TryParse(parts[0], out var size))
                {
                    return size;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get cache size with du, using fallback estimation");
        }
        
        // Fallback: estimate based on directory count
        try
        {
            var hexDirs = Directory.GetDirectories(_cachePath)
                .Where(d => Path.GetFileName(d).Length == 2)
                .ToList();
            
            // Sample first directory to estimate average
            if (hexDirs.Any())
            {
                long sampleSize = 0;
                var sampleDir = hexDirs.First();
                
                try
                {
                    var files = Directory.GetFiles(sampleDir, "*", SearchOption.AllDirectories);
                    foreach (var file in files.Take(100)) // Sample first 100 files
                    {
                        var fi = new FileInfo(file);
                        sampleSize += fi.Length;
                    }
                    
                    // Extrapolate
                    if (files.Length > 0)
                    {
                        var avgFileSize = sampleSize / Math.Min(files.Length, 100);
                        // Rough estimate: assume each directory has similar number of files
                        return avgFileSize * files.Length * hexDirs.Count;
                    }
                }
                catch { }
            }
        }
        catch { }
        
        // Default fallback: 100GB estimate
        return 100L * 1024 * 1024 * 1024;
    }

    private async Task<long> GetDirectorySizeQuick(string directory)
    {
        try
        {
            // Try to use du for quick size
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "timeout",
                Arguments = $"2 du -sb {directory}", // 2 second timeout per directory
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
                if (parts.Length > 0 && long.TryParse(parts[0], out var size))
                {
                    return size;
                }
            }
        }
        catch { }
        
        // Fallback: count files manually (but with limit)
        try
        {
            long totalSize = 0;
            var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            
            // Only check first 1000 files to avoid blocking
            foreach (var file in files.Take(1000))
            {
                try
                {
                    var fi = new FileInfo(file);
                    totalSize += fi.Length;
                }
                catch { }
            }
            
            // If we have more files, extrapolate
            if (files.Length > 1000)
            {
                var avgSize = totalSize / 1000;
                return avgSize * files.Length;
            }
            
            return totalSize;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<bool> DeleteDirectoryContents(string directory)
    {
        try
        {
            // Use rm -rf for faster deletion
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"-c \"find {directory} -type f -delete 2>/dev/null; find {directory} -type d -empty -delete 2>/dev/null\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            await process.WaitForExitAsync();
            
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to delete directory {directory} using find, trying manual deletion");
            
            // Fallback to manual deletion
            try
            {
                var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch { }
                }
                
                // Delete empty subdirectories
                var subdirs = Directory.GetDirectories(directory);
                foreach (var subdir in subdirs)
                {
                    try
                    {
                        Directory.Delete(subdir, true);
                    }
                    catch { }
                }
                
                return true;
            }
            catch
            {
                return false;
            }
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