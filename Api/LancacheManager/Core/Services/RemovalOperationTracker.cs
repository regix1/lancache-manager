using System.Collections.Concurrent;
using System.Diagnostics;
using LancacheManager.Core.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Types of removal operations tracked by the system.
/// </summary>
public enum RemovalOperationType
{
    Game,
    Service,
    Corruption,
    CacheClearing
}

/// <summary>
/// Tracks active removal operations (game, service, corruption) so they can be queried on page refresh.
/// This allows the frontend to restore progress state when navigating away and back.
/// </summary>
public class RemovalOperationTracker
{
    private readonly ConcurrentDictionary<(RemovalOperationType Type, string Key), RemovalOperation> _operations = new();
    private readonly ILogger<RemovalOperationTracker> _logger;

    public RemovalOperationTracker(ILogger<RemovalOperationTracker> logger)
    {
        _logger = logger;
    }

    #region Generic Core Methods

    private void StartRemoval(RemovalOperationType type, string key, string id, string name, string message)
    {
        var operation = new RemovalOperation
        {
            Id = id,
            Name = name,
            Status = OperationStatus.Running,
            StartedAt = DateTime.UtcNow,
            Message = message
        };
        _operations[(type, key)] = operation;
        _logger.LogInformation("Started tracking {Type} removal for: {Name}", type, name);
    }

    private void UpdateRemoval(RemovalOperationType type, string key, string status, string message, int? filesDeleted = null, long? bytesFreed = null)
    {
        if (_operations.TryGetValue((type, key), out var operation))
        {
            operation.Status = status;
            operation.Message = message;
            operation.FilesDeleted = filesDeleted ?? operation.FilesDeleted;
            operation.BytesFreed = bytesFreed ?? operation.BytesFreed;
            if (status == OperationStatus.Completed || status == OperationStatus.Failed)
            {
                operation.CompletedAt = DateTime.UtcNow;
            }
        }
    }

    private void CompleteRemoval(RemovalOperationType type, string key, bool success, int filesDeleted = 0, long bytesFreed = 0, string? error = null)
    {
        if (_operations.TryGetValue((type, key), out var operation))
        {
            operation.Status = success ? OperationStatus.Completed : OperationStatus.Failed;
            operation.FilesDeleted = filesDeleted;
            operation.BytesFreed = bytesFreed;
            operation.Error = error;
            operation.Success = success;
            // Update message to show error when failing
            if (!success && !string.IsNullOrEmpty(error))
            {
                operation.Message = error;
            }
            operation.CompletedAt = DateTime.UtcNow;

            // Clean up after a short delay to allow final status queries
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(task =>
                _operations.TryRemove((type, key), out RemovalOperation? _removed));
        }
        _logger.LogInformation("Completed tracking {Type} removal for key: {Key}, Success: {Success}", type, key, success);
    }

    private RemovalOperation? GetRemovalStatus(RemovalOperationType type, string key)
    {
        return _operations.TryGetValue((type, key), out var operation) ? operation : null;
    }

    private IEnumerable<RemovalOperation> GetActiveRemovals(RemovalOperationType type)
    {
        return _operations
            .Where(kvp => kvp.Key.Type == type && kvp.Value.Status != OperationStatus.Completed && kvp.Value.Status != OperationStatus.Failed)
            .Select(kvp => kvp.Value);
    }

    #endregion

    #region Game Removal Operations

    public void StartGameRemoval(int appId, string gameName)
    {
        var key = appId.ToString();
        StartRemoval(RemovalOperationType.Game, key, key, gameName, $"Removing {gameName}...");
    }

    public void UpdateGameRemoval(int appId, string status, string message, int? filesDeleted = null, long? bytesFreed = null)
    {
        UpdateRemoval(RemovalOperationType.Game, appId.ToString(), status, message, filesDeleted, bytesFreed);
    }

    public void CompleteGameRemoval(int appId, bool success, int filesDeleted = 0, long bytesFreed = 0, string? error = null)
    {
        CompleteRemoval(RemovalOperationType.Game, appId.ToString(), success, filesDeleted, bytesFreed, error);
    }

    public RemovalOperation? GetGameRemovalStatus(int appId)
    {
        return GetRemovalStatus(RemovalOperationType.Game, appId.ToString());
    }

    public IEnumerable<RemovalOperation> GetActiveGameRemovals()
    {
        return GetActiveRemovals(RemovalOperationType.Game);
    }

    #endregion

    #region Service Removal Operations

    public void StartServiceRemoval(string serviceName, string operationId, CancellationTokenSource? cts = null)
    {
        var key = serviceName.ToLowerInvariant();
        StartRemoval(RemovalOperationType.Service, key, operationId, serviceName, $"Removing {serviceName}...");

        // Store the CancellationTokenSource for cancellation support
        if (cts != null && _operations.TryGetValue((RemovalOperationType.Service, key), out var operation))
        {
            operation.CancellationTokenSource = cts;
        }
    }

    public void UpdateServiceRemoval(string serviceName, string status, string message, int? filesDeleted = null, long? bytesFreed = null)
    {
        UpdateRemoval(RemovalOperationType.Service, serviceName.ToLowerInvariant(), status, message, filesDeleted, bytesFreed);
    }

    public void CompleteServiceRemoval(string serviceName, bool success, int filesDeleted = 0, long bytesFreed = 0, string? error = null)
    {
        var key = serviceName.ToLowerInvariant();
        // Dispose the CancellationTokenSource when completing
        if (_operations.TryGetValue((RemovalOperationType.Service, key), out var operation))
        {
            operation.CancellationTokenSource?.Dispose();
            operation.CancellationTokenSource = null;
        }
        CompleteRemoval(RemovalOperationType.Service, key, success, filesDeleted, bytesFreed, error);
    }

    public RemovalOperation? GetServiceRemovalStatus(string serviceName)
    {
        return GetRemovalStatus(RemovalOperationType.Service, serviceName.ToLowerInvariant());
    }

    public IEnumerable<RemovalOperation> GetActiveServiceRemovals()
    {
        return GetActiveRemovals(RemovalOperationType.Service);
    }

    public bool CancelServiceRemoval(string serviceName)
    {
        var key = serviceName.ToLowerInvariant();
        if (_operations.TryGetValue((RemovalOperationType.Service, key), out var operation))
        {
            if (operation.CancellationTokenSource != null)
            {
                // If cancellation was already requested, return true (idempotent)
                // This prevents 404 errors when user clicks cancel button multiple times
                if (operation.CancellationTokenSource.IsCancellationRequested)
                {
                    _logger.LogDebug("Cancellation already in progress for service removal: {Service}", serviceName);
                    return true;
                }

                _logger.LogInformation("Requesting cancellation for service removal: {Service}", serviceName);
                operation.CancellationTokenSource.Cancel();
                operation.Status = OperationStatus.Cancelling;
                operation.Message = "Cancellation requested...";
                return true;
            }
        }
        return false;
    }

    #endregion

    #region Corruption Removal Operations

    public void StartCorruptionRemoval(string serviceName, string operationId, CancellationTokenSource? cts = null)
    {
        var key = serviceName.ToLowerInvariant();
        StartRemoval(RemovalOperationType.Corruption, key, operationId, serviceName, $"Removing corrupted chunks for {serviceName}...");
        
        // Store the CancellationTokenSource for cancellation support
        if (cts != null && _operations.TryGetValue((RemovalOperationType.Corruption, key), out var operation))
        {
            operation.CancellationTokenSource = cts;
        }
    }

    public void UpdateCorruptionRemoval(string serviceName, string status, string? message = null)
    {
        UpdateRemoval(RemovalOperationType.Corruption, serviceName.ToLowerInvariant(), status, message ?? string.Empty);
    }

    public void UpdateCorruptionRemovalProgress(string serviceName, string status, string? message, int filesProcessed, int totalFiles, double percentComplete)
    {
        var key = serviceName.ToLowerInvariant();
        if (_operations.TryGetValue((RemovalOperationType.Corruption, key), out var operation))
        {
            operation.Status = status;
            operation.Message = message ?? string.Empty;
            operation.FilesProcessed = filesProcessed;
            operation.TotalFiles = totalFiles;
            operation.PercentComplete = percentComplete;
        }
    }

    public void CompleteCorruptionRemoval(string serviceName, bool success, string? error = null)
    {
        var key = serviceName.ToLowerInvariant();
        // Dispose the CancellationTokenSource when completing
        if (_operations.TryGetValue((RemovalOperationType.Corruption, key), out var operation))
        {
            operation.CancellationTokenSource?.Dispose();
            operation.CancellationTokenSource = null;
        }
        CompleteRemoval(RemovalOperationType.Corruption, key, success, 0, 0, error);
    }

    public RemovalOperation? GetCorruptionRemovalStatus(string serviceName)
    {
        return GetRemovalStatus(RemovalOperationType.Corruption, serviceName.ToLowerInvariant());
    }

    public IEnumerable<RemovalOperation> GetActiveCorruptionRemovals()
    {
        return GetActiveRemovals(RemovalOperationType.Corruption);
    }

    /// <summary>
    /// Request cancellation of a corruption removal operation.
    /// Returns true if cancellation was requested, false if no operation found.
    /// </summary>
    public bool CancelCorruptionRemoval(string serviceName)
    {
        var key = serviceName.ToLowerInvariant();
        if (_operations.TryGetValue((RemovalOperationType.Corruption, key), out var operation))
        {
            if (operation.CancellationTokenSource != null)
            {
                // If cancellation was already requested, return true (idempotent)
                // This prevents 404 errors when user clicks cancel button multiple times
                if (operation.CancellationTokenSource.IsCancellationRequested)
                {
                    _logger.LogDebug("Cancellation already in progress for corruption removal: {Service}", serviceName);
                    return true;
                }
                
                _logger.LogInformation("Requesting cancellation for corruption removal: {Service}", serviceName);
                operation.CancellationTokenSource.Cancel();
                operation.Status = OperationStatus.Cancelling;
                operation.Message = "Cancellation requested...";
                return true;
            }
        }
        return false;
    }

    #endregion

    #region Cache Clearing Operations

    public void StartCacheClearing(string datasourceName, string operationId, CancellationTokenSource? cts = null, Process? process = null)
    {
        var key = datasourceName.ToLowerInvariant();
        StartRemoval(RemovalOperationType.CacheClearing, key, operationId, datasourceName, $"Clearing cache for {datasourceName}...");
        
        // Store the CancellationTokenSource and Process for cancellation support
        if (_operations.TryGetValue((RemovalOperationType.CacheClearing, key), out var operation))
        {
            if (cts != null) operation.CancellationTokenSource = cts;
            if (process != null) operation.RustProcess = process;
        }
    }

    public void UpdateCacheClearing(string datasourceName, string status, string? message = null)
    {
        UpdateRemoval(RemovalOperationType.CacheClearing, datasourceName.ToLowerInvariant(), status, message ?? string.Empty);
    }

    public void UpdateCacheClearingProgress(string datasourceName, int directoriesProcessed, int totalDirectories, long bytesDeleted, double percentComplete)
    {
        var key = datasourceName.ToLowerInvariant();
        if (_operations.TryGetValue((RemovalOperationType.CacheClearing, key), out var operation))
        {
            operation.DirectoriesProcessed = directoriesProcessed;
            operation.TotalDirectories = totalDirectories;
            operation.BytesFreed = bytesDeleted;
            operation.PercentComplete = percentComplete;
        }
    }

    public void CompleteCacheClearing(string datasourceName, bool success, string? error = null)
    {
        var key = datasourceName.ToLowerInvariant();
        // Dispose the CancellationTokenSource and clear process reference when completing
        if (_operations.TryGetValue((RemovalOperationType.CacheClearing, key), out var operation))
        {
            operation.CancellationTokenSource?.Dispose();
            operation.CancellationTokenSource = null;
            operation.RustProcess = null;
        }
        CompleteRemoval(RemovalOperationType.CacheClearing, key, success, 0, 0, error);
    }

    public RemovalOperation? GetCacheClearingStatus(string datasourceName)
    {
        return GetRemovalStatus(RemovalOperationType.CacheClearing, datasourceName.ToLowerInvariant());
    }

    public IEnumerable<RemovalOperation> GetActiveCacheClearings()
    {
        return GetActiveRemovals(RemovalOperationType.CacheClearing);
    }

    /// <summary>
    /// Request cancellation of a cache clearing operation.
    /// Returns true if cancellation was requested, false if no operation found.
    /// </summary>
    public bool CancelCacheClearing(string datasourceName)
    {
        var key = datasourceName.ToLowerInvariant();
        if (_operations.TryGetValue((RemovalOperationType.CacheClearing, key), out var operation))
        {
            if (operation.CancellationTokenSource != null)
            {
                // If cancellation was already requested, return true (idempotent)
                // This prevents 404 errors when user clicks cancel button multiple times
                if (operation.CancellationTokenSource.IsCancellationRequested)
                {
                    _logger.LogDebug("Cancellation already in progress for cache clearing: {Datasource}", datasourceName);
                    return true;
                }
                
                _logger.LogInformation("Requesting cancellation for cache clearing: {Datasource}", datasourceName);
                operation.CancellationTokenSource.Cancel();
                operation.Status = OperationStatus.Cancelling;
                operation.Message = "Cancellation requested...";
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Force kills the Rust process for a cache clearing operation.
    /// Returns true if the process was killed, false if no operation or process found.
    /// </summary>
    public bool ForceKillCacheClearing(string datasourceName)
    {
        var key = datasourceName.ToLowerInvariant();
        if (_operations.TryGetValue((RemovalOperationType.CacheClearing, key), out var operation))
        {
            // First cancel the token
            operation.CancellationTokenSource?.Cancel();
            
            // Kill the Rust process if it exists and is still running
            if (operation.RustProcess != null && !operation.RustProcess.HasExited)
            {
                _logger.LogWarning("Force killing Rust cache_cleaner process (PID: {Pid}) for datasource {Datasource}", 
                    operation.RustProcess.Id, datasourceName);
                try
                {
                    operation.RustProcess.Kill(entireProcessTree: true);
                    operation.Status = OperationStatus.Cancelled;
                    operation.Message = "Force killed by user";
                    operation.Cancelled = true;
                    operation.CompletedAt = DateTime.UtcNow;
                    operation.RustProcess = null;
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error force killing process for datasource {Datasource}", datasourceName);
                    return false;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Sets the Rust process reference for a cache clearing operation after spawn.
    /// </summary>
    public void SetCacheClearingProcess(string datasourceName, Process process)
    {
        var key = datasourceName.ToLowerInvariant();
        if (_operations.TryGetValue((RemovalOperationType.CacheClearing, key), out var operation))
        {
            operation.RustProcess = process;
        }
    }

    #endregion

    #region Aggregate Methods

    /// <summary>
    /// Get all active removals across all types (for universal recovery).
    /// </summary>
    public ActiveRemovalsStatus GetAllActiveRemovals()
    {
        return new ActiveRemovalsStatus
        {
            GameRemovals = GetActiveGameRemovals().ToList(),
            ServiceRemovals = GetActiveServiceRemovals().ToList(),
            CorruptionRemovals = GetActiveCorruptionRemovals().ToList(),
            CacheClearings = GetActiveCacheClearings().ToList()
        };
    }

    #endregion
}

public class RemovalOperation
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = OperationStatus.Pending;
    public string Message { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Indicates if the operation completed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Indicates if the operation was cancelled.
    /// </summary>
    public bool Cancelled { get; set; }
    
    // Progress tracking for corruption removal and cache clearing
    public double PercentComplete { get; set; }
    public int FilesProcessed { get; set; }
    public int TotalFiles { get; set; }
    
    // Additional properties for cache clearing (directories instead of files)
    public int DirectoriesProcessed { get; set; }
    public int TotalDirectories { get; set; }
    
    /// <summary>
    /// CancellationTokenSource for cancelling the operation.
    /// Not serialized to JSON responses.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CancellationTokenSource? CancellationTokenSource { get; set; }
    
    /// <summary>
    /// Reference to the Rust process for force kill capability.
    /// Not serialized to JSON responses.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Process? RustProcess { get; set; }
}

public class ActiveRemovalsStatus
{
    public List<RemovalOperation> GameRemovals { get; set; } = new();
    public List<RemovalOperation> ServiceRemovals { get; set; } = new();
    public List<RemovalOperation> CorruptionRemovals { get; set; } = new();
    public List<RemovalOperation> CacheClearings { get; set; } = new();

    public bool HasActiveOperations =>
        GameRemovals.Any() || ServiceRemovals.Any() || CorruptionRemovals.Any() || CacheClearings.Any();
}
