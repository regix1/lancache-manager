using System.Collections.Concurrent;

namespace LancacheManager.Application.Services;

/// <summary>
/// Tracks active removal operations (game, service, corruption) so they can be queried on page refresh.
/// This allows the frontend to restore progress state when navigating away and back.
/// </summary>
public class RemovalOperationTracker
{
    private readonly ConcurrentDictionary<string, RemovalOperation> _gameRemovals = new();
    private readonly ConcurrentDictionary<string, RemovalOperation> _serviceRemovals = new();
    private readonly ConcurrentDictionary<string, RemovalOperation> _corruptionRemovals = new();
    private readonly ILogger<RemovalOperationTracker> _logger;

    public RemovalOperationTracker(ILogger<RemovalOperationTracker> logger)
    {
        _logger = logger;
    }

    // Game Removal Operations
    public void StartGameRemoval(int appId, string gameName)
    {
        var key = appId.ToString();
        var operation = new RemovalOperation
        {
            Id = key,
            Name = gameName,
            Status = "running",
            StartedAt = DateTime.UtcNow,
            Message = $"Removing {gameName}..."
        };
        _gameRemovals[key] = operation;
        _logger.LogInformation("Started tracking game removal for AppId: {AppId}", appId);
    }

    public void UpdateGameRemoval(int appId, string status, string message, int? filesDeleted = null, long? bytesFreed = null)
    {
        var key = appId.ToString();
        if (_gameRemovals.TryGetValue(key, out var operation))
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

    public void CompleteGameRemoval(int appId, bool success, int filesDeleted = 0, long bytesFreed = 0, string? error = null)
    {
        var key = appId.ToString();
        if (_gameRemovals.TryGetValue(key, out var operation))
        {
            operation.Status = success ? "complete" : "failed";
            operation.FilesDeleted = filesDeleted;
            operation.BytesFreed = bytesFreed;
            operation.Error = error;
            operation.CompletedAt = DateTime.UtcNow;

            // Clean up after a short delay to allow final status queries
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => _gameRemovals.TryRemove(key, out RemovalOperation? _removed));
        }
        _logger.LogInformation("Completed tracking game removal for AppId: {AppId}, Success: {Success}", appId, success);
    }

    public RemovalOperation? GetGameRemovalStatus(int appId)
    {
        var key = appId.ToString();
        return _gameRemovals.TryGetValue(key, out var operation) ? operation : null;
    }

    public IEnumerable<RemovalOperation> GetActiveGameRemovals()
    {
        return _gameRemovals.Values.Where(o => o.Status == "running");
    }

    // Service Removal Operations
    public void StartServiceRemoval(string serviceName)
    {
        var key = serviceName.ToLowerInvariant();
        var operation = new RemovalOperation
        {
            Id = key,
            Name = serviceName,
            Status = "running",
            StartedAt = DateTime.UtcNow,
            Message = $"Removing {serviceName}..."
        };
        _serviceRemovals[key] = operation;
        _logger.LogInformation("Started tracking service removal for: {Service}", serviceName);
    }

    public void UpdateServiceRemoval(string serviceName, string status, string message, int? filesDeleted = null, long? bytesFreed = null)
    {
        var key = serviceName.ToLowerInvariant();
        if (_serviceRemovals.TryGetValue(key, out var operation))
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

    public void CompleteServiceRemoval(string serviceName, bool success, int filesDeleted = 0, long bytesFreed = 0, string? error = null)
    {
        var key = serviceName.ToLowerInvariant();
        if (_serviceRemovals.TryGetValue(key, out var operation))
        {
            operation.Status = success ? "complete" : "failed";
            operation.FilesDeleted = filesDeleted;
            operation.BytesFreed = bytesFreed;
            operation.Error = error;
            operation.CompletedAt = DateTime.UtcNow;

            // Clean up after a short delay
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => _serviceRemovals.TryRemove(key, out RemovalOperation? _removed));
        }
        _logger.LogInformation("Completed tracking service removal for: {Service}, Success: {Success}", serviceName, success);
    }

    public RemovalOperation? GetServiceRemovalStatus(string serviceName)
    {
        var key = serviceName.ToLowerInvariant();
        return _serviceRemovals.TryGetValue(key, out var operation) ? operation : null;
    }

    public IEnumerable<RemovalOperation> GetActiveServiceRemovals()
    {
        return _serviceRemovals.Values.Where(o => o.Status == "running");
    }

    // Corruption Removal Operations
    public void StartCorruptionRemoval(string serviceName, string operationId)
    {
        var key = serviceName.ToLowerInvariant();
        var operation = new RemovalOperation
        {
            Id = operationId,
            Name = serviceName,
            Status = "running",
            StartedAt = DateTime.UtcNow,
            Message = $"Removing corrupted chunks for {serviceName}..."
        };
        _corruptionRemovals[key] = operation;
        _logger.LogInformation("Started tracking corruption removal for: {Service}", serviceName);
    }

    public void UpdateCorruptionRemoval(string serviceName, string status, string message)
    {
        var key = serviceName.ToLowerInvariant();
        if (_corruptionRemovals.TryGetValue(key, out var operation))
        {
            operation.Status = status;
            operation.Message = message;
            if (status == "complete" || status == "failed")
            {
                operation.CompletedAt = DateTime.UtcNow;
            }
        }
    }

    public void CompleteCorruptionRemoval(string serviceName, bool success, string? error = null)
    {
        var key = serviceName.ToLowerInvariant();
        if (_corruptionRemovals.TryGetValue(key, out var operation))
        {
            operation.Status = success ? "complete" : "failed";
            operation.Error = error;
            operation.CompletedAt = DateTime.UtcNow;

            // Clean up after a short delay
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => _corruptionRemovals.TryRemove(key, out RemovalOperation? _removed));
        }
        _logger.LogInformation("Completed tracking corruption removal for: {Service}, Success: {Success}", serviceName, success);
    }

    public RemovalOperation? GetCorruptionRemovalStatus(string serviceName)
    {
        var key = serviceName.ToLowerInvariant();
        return _corruptionRemovals.TryGetValue(key, out var operation) ? operation : null;
    }

    public IEnumerable<RemovalOperation> GetActiveCorruptionRemovals()
    {
        return _corruptionRemovals.Values.Where(o => o.Status == "running");
    }

    // Get all active removals (for universal recovery)
    public ActiveRemovalsStatus GetAllActiveRemovals()
    {
        return new ActiveRemovalsStatus
        {
            GameRemovals = GetActiveGameRemovals().ToList(),
            ServiceRemovals = GetActiveServiceRemovals().ToList(),
            CorruptionRemovals = GetActiveCorruptionRemovals().ToList()
        };
    }
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
