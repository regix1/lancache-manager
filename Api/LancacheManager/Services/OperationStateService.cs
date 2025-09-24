using System.Collections.Concurrent;
using System.Text.Json;
using LancacheManager.Constants;

namespace LancacheManager.Services;

public class OperationStateService : IHostedService
{
    private readonly ILogger<OperationStateService> _logger;
    private readonly string _stateFilePath;
    private readonly ConcurrentDictionary<string, OperationState> _states = new();
    private Timer? _cleanupTimer;
    private readonly object _fileLock = new object();

    public OperationStateService(ILogger<OperationStateService> logger)
    {
        _logger = logger;
        _stateFilePath = Path.Combine(LancacheConstants.DATA_DIRECTORY, "operation_states.json");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadStates();
        // Cleanup expired operations every 5 minutes
        _cleanupTimer = new Timer(CleanupExpired, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Dispose();
        SaveStates();
        return Task.CompletedTask;
    }

    public OperationState? GetState(string key)
    {
        if (_states.TryGetValue(key, out var state))
        {
            // Check expiration
            if (state.ExpiresAt > DateTime.UtcNow)
            {
                return state;
            }
            
            // Remove expired state
            _states.TryRemove(key, out _);
        }
        return null;
    }

    public void SaveState(string key, OperationState state)
    {
        state.UpdatedAt = DateTime.UtcNow;
        _states[key] = state;
        SaveStates();
    }

    public void UpdateState(string key, Dictionary<string, object> updates)
    {
        if (_states.TryGetValue(key, out var state))
        {
            foreach (var kvp in updates)
            {
                state.Data[kvp.Key] = kvp.Value;
            }
            state.UpdatedAt = DateTime.UtcNow;
            SaveStates();
        }
    }

    public void RemoveState(string key)
    {
        if (_states.TryRemove(key, out _))
        {
            SaveStates();
        }
    }

    public List<OperationState> GetAllStates()
    {
        // Return only non-expired states
        var now = DateTime.UtcNow;
        return _states.Values
            .Where(s => s.ExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
    }

    public List<OperationState> GetStatesByType(string type)
    {
        var now = DateTime.UtcNow;
        return _states.Values
            .Where(s => s.Type == type && s.ExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
    }

    private void LoadStates()
    {
        try
        {
            var dataDirectory = LancacheConstants.DATA_DIRECTORY;
            if (!Directory.Exists(dataDirectory))
            {
                Directory.CreateDirectory(dataDirectory);
            }

            if (File.Exists(_stateFilePath))
            {
                lock (_fileLock)
                {
                    var json = File.ReadAllText(_stateFilePath);
                    var states = JsonSerializer.Deserialize<List<OperationState>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (states != null)
                    {
                        var now = DateTime.UtcNow;
                        foreach (var state in states)
                        {
                            // Only load non-expired states
                            if (state.ExpiresAt > now)
                            {
                                _states[state.Key] = state;
                            }
                        }
                    }
                }
                
                _logger.LogInformation($"Loaded {_states.Count} operation states from disk");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load operation states");
        }
    }

    private void SaveStates()
    {
        try
        {
            lock (_fileLock)
            {
                var json = JsonSerializer.Serialize(_states.Values.ToList(), new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(_stateFilePath, json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save operation states");
        }
    }

    private void CleanupExpired(object? state)
    {
        try
        {
            var now = DateTime.UtcNow;
            var expired = _states
                .Where(kvp => kvp.Value.ExpiresAt <= now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expired)
            {
                _states.TryRemove(key, out _);
            }

            if (expired.Count > 0)
            {
                _logger.LogDebug($"Cleaned up {expired.Count} expired operation states");
                SaveStates();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up expired states");
        }
    }
}

public class OperationState
{
    public string Key { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
}