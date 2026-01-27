using System.Collections.Concurrent;

namespace LancacheManager.Core.Services;

/// <summary>
/// Types of removal operations tracked by the system.
/// </summary>
public enum RemovalOperationType
{
    Game,
    Service,
    Corruption
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
            Status = "running",
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
            if (status == "complete" || status == "failed")
            {
                operation.CompletedAt = DateTime.UtcNow;
            }
        }
    }

    private void CompleteRemoval(RemovalOperationType type, string key, bool success, int filesDeleted = 0, long bytesFreed = 0, string? error = null)
    {
        if (_operations.TryGetValue((type, key), out var operation))
        {
            operation.Status = success ? "complete" : "failed";
            operation.FilesDeleted = filesDeleted;
            operation.BytesFreed = bytesFreed;
            operation.Error = error;
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
            .Where(kvp => kvp.Key.Type == type && kvp.Value.Status != "complete" && kvp.Value.Status != "failed")
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

    public void StartServiceRemoval(string serviceName)
    {
        var key = serviceName.ToLowerInvariant();
        StartRemoval(RemovalOperationType.Service, key, key, serviceName, $"Removing {serviceName}...");
    }

    public void UpdateServiceRemoval(string serviceName, string status, string message, int? filesDeleted = null, long? bytesFreed = null)
    {
        UpdateRemoval(RemovalOperationType.Service, serviceName.ToLowerInvariant(), status, message, filesDeleted, bytesFreed);
    }

    public void CompleteServiceRemoval(string serviceName, bool success, int filesDeleted = 0, long bytesFreed = 0, string? error = null)
    {
        CompleteRemoval(RemovalOperationType.Service, serviceName.ToLowerInvariant(), success, filesDeleted, bytesFreed, error);
    }

    public RemovalOperation? GetServiceRemovalStatus(string serviceName)
    {
        return GetRemovalStatus(RemovalOperationType.Service, serviceName.ToLowerInvariant());
    }

    public IEnumerable<RemovalOperation> GetActiveServiceRemovals()
    {
        return GetActiveRemovals(RemovalOperationType.Service);
    }

    #endregion

    #region Corruption Removal Operations

    public void StartCorruptionRemoval(string serviceName, string operationId)
    {
        var key = serviceName.ToLowerInvariant();
        StartRemoval(RemovalOperationType.Corruption, key, operationId, serviceName, $"Removing corrupted chunks for {serviceName}...");
    }

    public void UpdateCorruptionRemoval(string serviceName, string status, string? message = null)
    {
        UpdateRemoval(RemovalOperationType.Corruption, serviceName.ToLowerInvariant(), status, message ?? string.Empty);
    }

    public void CompleteCorruptionRemoval(string serviceName, bool success, string? error = null)
    {
        CompleteRemoval(RemovalOperationType.Corruption, serviceName.ToLowerInvariant(), success, 0, 0, error);
    }

    public RemovalOperation? GetCorruptionRemovalStatus(string serviceName)
    {
        return GetRemovalStatus(RemovalOperationType.Corruption, serviceName.ToLowerInvariant());
    }

    public IEnumerable<RemovalOperation> GetActiveCorruptionRemovals()
    {
        return GetActiveRemovals(RemovalOperationType.Corruption);
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
            CorruptionRemovals = GetActiveCorruptionRemovals().ToList()
        };
    }

    #endregion
}

public class RemovalOperation
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "pending"; // pending, running, complete, failed
    public string Message { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public string? Error { get; set; }
}

public class ActiveRemovalsStatus
{
    public List<RemovalOperation> GameRemovals { get; set; } = new();
    public List<RemovalOperation> ServiceRemovals { get; set; } = new();
    public List<RemovalOperation> CorruptionRemovals { get; set; } = new();

    public bool HasActiveOperations =>
        GameRemovals.Any() || ServiceRemovals.Any() || CorruptionRemovals.Any();
}
