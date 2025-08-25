using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using LancacheManager.Hubs;
using System.Text.Json;

namespace LancacheManager.Services;

public class CacheClearingService : IHostedService
{
    private readonly ILogger<CacheClearingService> _logger;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, CacheClearOperation> _operations = new();
    private readonly string _cachePath;
    private readonly string _statusFilePath = "/tmp/cache_clear_status.json"; // Persistent status file
    private Timer? _cleanupTimer;
    private Timer? _progressTimer;

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
        // Load any persisted operations on startup
        LoadPersistedOperations();
        
        _cleanupTimer = new Timer(CleanupOldOperations, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        
        // Check for any active operations that need monitoring
        CheckForActiveOperations();
        
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Dispose();
        _progressTimer?.Dispose();
        
        // Save current operations before shutdown
        SavePersistedOperations();
        
        foreach (var operation in _operations.Values)
        {
            operation.CancellationTokenSource?.Cancel();
        }
        
        return Task.CompletedTask;
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
                        // Only load operations that are still relevant (less than 1 hour old or still running)
                        if (op.Status == ClearStatus.Running || 
                            op.Status == ClearStatus.Preparing ||
                            (op.StartTime > DateTime.UtcNow.AddHours(-1)))
                        {
                            _operations[op.Id] = op;
                            
                            // If operation was running, check if the background process is still active
                            if (op.Status == ClearStatus.Running && !string.IsNullOrEmpty(op.BackgroundProcessId))
                            {
                                _ = Task.Run(() => MonitorBackgroundProcess(op));
                            }
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
            
            // Start monitoring active operations
            foreach (var op in activeOps)
            {
                _ = Task.Run(() => MonitorBackgroundProcess(op));
            }
        }
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
        SavePersistedOperations(); // Persist immediately
        
        // Use the improved clear method that actually works
        _ = Task.Run(async () => await ClearCacheWithProgress(operation), operation.CancellationTokenSource.Token);
        
        return operationId;
    }

    private async Task ClearCacheWithProgress(CacheClearOperation operation)
    {
        try
        {
            _logger.LogInformation($"Starting cache clear operation {operation.Id}");
            operation.StatusMessage = "Analyzing cache directory...";
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
            operation.DirectoriesProcessed = 0;
            
            // Quick size estimate (with timeout)
            long totalSize = await EstimateCacheSize();
            operation.TotalBytesToDelete = totalSize;
            
            operation.Status = ClearStatus.Running;
            operation.StatusMessage = "Clearing cache files...";
            await NotifyProgress(operation);
            SavePersistedOperations();
            
            // Method 1: Try fast parallel find command first
            bool success = await ClearUsingFindCommand(operation, hexDirs);
            
            if (!success && operation.CancellationTokenSource?.Token.IsCancellationRequested != true)
            {
                // Method 2: Try directory-by-directory clearing
                success = await ClearDirectoryByDirectory(operation, hexDirs);
            }
            
            if (success)
            {
                operation.Status = ClearStatus.Completed;
                operation.StatusMessage = $"Successfully cleared {FormatBytes(operation.BytesDeleted)}";
                operation.EndTime = DateTime.UtcNow;
                
                var duration = operation.EndTime.Value - operation.StartTime;
                _logger.LogInformation($"Cache clear completed in {duration.TotalSeconds:F1} seconds");
            }
            else if (operation.CancellationTokenSource?.Token.IsCancellationRequested == true)
            {
                operation.Status = ClearStatus.Cancelled;
                operation.StatusMessage = "Operation cancelled by user";
                operation.EndTime = DateTime.UtcNow;
            }
            else
            {
                operation.Status = ClearStatus.Failed;
                operation.Error = "Failed to clear cache - check permissions";
                operation.StatusMessage = "Cache clear failed";
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
    }

    private async Task<bool> ClearUsingFindCommand(CacheClearOperation operation, List<string> hexDirs)
    {
        try
        {
            _logger.LogInformation("Using find command to clear cache");
            
            // Start the find command in background
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"-c \"find {_cachePath} -type f -delete 2>&1 | tee /tmp/cache_clear_{operation.Id}.log\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            operation.BackgroundProcessId = process.Id.ToString();
            SavePersistedOperations();
            
            // Monitor progress by checking remaining files periodically
            var startTime = DateTime.UtcNow;
            var lastFileCount = -1;
            var stableCountChecks = 0;
            
            while (!process.HasExited)
            {
                if (operation.CancellationTokenSource?.Token.IsCancellationRequested == true)
                {
                    process.Kill();
                    return false;
                }
                
                await Task.Delay(2000); // Check every 2 seconds
                
                // Count remaining files in a few sample directories for progress
                var sampleDirs = hexDirs.Take(4).ToList(); // Sample first 4 dirs
                var currentFileCount = 0;
                
                foreach (var dir in sampleDirs)
                {
                    try
                    {
                        currentFileCount += Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
                    }
                    catch { }
                }
                
                // Estimate progress based on sample
                if (lastFileCount == -1)
                {
                    lastFileCount = currentFileCount * (hexDirs.Count / 4); // Extrapolate
                }
                
                if (currentFileCount < lastFileCount)
                {
                    var filesDeleted = lastFileCount - currentFileCount;
                    var estimatedTotalDeleted = filesDeleted * (hexDirs.Count / 4);
                    
                    operation.DirectoriesProcessed = Math.Min(hexDirs.Count - 1, 
                        (int)(hexDirs.Count * (1 - (double)currentFileCount / lastFileCount)));
                    
                    // Estimate bytes deleted (rough estimate: 10MB per file average)
                    operation.BytesDeleted = estimatedTotalDeleted * 10 * 1024 * 1024;
                    
                    operation.StatusMessage = $"Clearing cache... ({operation.DirectoriesProcessed}/{hexDirs.Count} directories)";
                    await NotifyProgress(operation);
                    
                    stableCountChecks = 0;
                }
                else
                {
                    stableCountChecks++;
                    if (stableCountChecks > 30) // No progress for 60 seconds
                    {
                        _logger.LogWarning("Cache clear appears stuck, killing process");
                        process.Kill();
                        break;
                    }
                }
                
                // Timeout after 5 minutes
                if ((DateTime.UtcNow - startTime).TotalMinutes > 5)
                {
                    _logger.LogWarning("Cache clear timeout, killing process");
                    process.Kill();
                    break;
                }
            }
            
            if (process.ExitCode == 0)
            {
                operation.DirectoriesProcessed = hexDirs.Count;
                operation.BytesDeleted = operation.TotalBytesToDelete;
                _logger.LogInformation("Cache cleared successfully using find command");
                
                // Clean up log file
                try { File.Delete($"/tmp/cache_clear_{operation.Id}.log"); } catch { }
                
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to use find command");
            return false;
        }
    }

    private async Task<bool> ClearDirectoryByDirectory(CacheClearOperation operation, List<string> hexDirs)
    {
        try
        {
            _logger.LogInformation("Clearing cache directory by directory");
            var successCount = 0;
            
            for (int i = 0; i < hexDirs.Count; i++)
            {
                if (operation.CancellationTokenSource?.Token.IsCancellationRequested == true)
                    return false;
                
                var dir = hexDirs[i];
                
                try
                {
                    // Delete all files in this directory
                    var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                    var dirBytes = 0L;
                    
                    foreach (var file in files)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(file);
                            dirBytes += fileInfo.Length;
                            File.Delete(file);
                        }
                        catch { }
                    }
                    
                    // Delete empty subdirectories
                    var subDirs = Directory.GetDirectories(dir);
                    foreach (var subDir in subDirs)
                    {
                        try { Directory.Delete(subDir, true); } catch { }
                    }
                    
                    operation.BytesDeleted += dirBytes;
                    successCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to clear directory {dir}");
                }
                
                operation.DirectoriesProcessed = i + 1;
                operation.StatusMessage = $"Clearing cache... ({operation.DirectoriesProcessed}/{hexDirs.Count} directories)";
                await NotifyProgress(operation);
                
                // Save progress periodically
                if (i % 10 == 0)
                {
                    SavePersistedOperations();
                }
            }
            
            return successCount > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear directories");
            return false;
        }
    }

    private async Task<long> EstimateCacheSize()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "timeout",
                Arguments = $"3 du -sb {_cachePath}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            process.Start();
            if (await Task.Run(() => process.WaitForExit(3000)))
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                if (long.TryParse(output.Split('\t')[0], out var size))
                {
                    return size;
                }
            }
        }
        catch { }
        
        // Default estimate if actual size check fails
        return 500L * 1024 * 1024 * 1024; // 500GB default
    }

    private async Task MonitorBackgroundProcess(CacheClearOperation operation)
    {
        try
        {
            if (string.IsNullOrEmpty(operation.BackgroundProcessId))
                return;
                
            // Check if process is still running
            while (operation.Status == ClearStatus.Running)
            {
                try
                {
                    var process = Process.GetProcessById(int.Parse(operation.BackgroundProcessId));
                    if (process.HasExited)
                    {
                        // Process finished
                        operation.Status = ClearStatus.Completed;
                        operation.StatusMessage = "Background cache clear completed";
                        operation.EndTime = DateTime.UtcNow;
                        await NotifyProgress(operation);
                        SavePersistedOperations();
                        break;
                    }
                }
                catch
                {
                    // Process not found - assume completed
                    operation.Status = ClearStatus.Completed;
                    operation.StatusMessage = "Background cache clear completed";
                    operation.EndTime = DateTime.UtcNow;
                    await NotifyProgress(operation);
                    SavePersistedOperations();
                    break;
                }
                
                await Task.Delay(5000); // Check every 5 seconds
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error monitoring background process for operation {operation.Id}");
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
                PercentComplete = operation.TotalDirectories > 0 
                    ? (operation.DirectoriesProcessed * 100.0 / operation.TotalDirectories) 
                    : 0
            };
            
            await _hubContext.Clients.All.SendAsync("CacheClearProgress", progress);
            
            _logger.LogDebug($"Cache clear progress: {progress.PercentComplete:F1}% - {progress.StatusMessage}");
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
            PercentComplete = op.TotalDirectories > 0 
                ? (op.DirectoriesProcessed * 100.0 / op.TotalDirectories) 
                : 0
        }).ToList();
    }

    public bool CancelOperation(string operationId)
    {
        if (_operations.TryGetValue(operationId, out var operation))
        {
            _logger.LogInformation($"Cancelling cache clear operation {operationId}");
            
            operation.Status = ClearStatus.Cancelled;
            operation.StatusMessage = "Operation cancelled";
            operation.EndTime = DateTime.UtcNow;
            operation.CancellationTokenSource?.Cancel();
            
            // Kill background process if running
            if (!string.IsNullOrEmpty(operation.BackgroundProcessId))
            {
                try
                {
                    var process = Process.GetProcessById(int.Parse(operation.BackgroundProcessId));
                    process.Kill();
                }
                catch { }
            }
            
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
            var cutoff = DateTime.UtcNow.AddHours(-24); // Keep for 24 hours
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
    
    private string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}