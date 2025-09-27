using System.Collections.Concurrent;
using System.Text.Json;
using LancacheManager.Services;

namespace LancacheManager.Services;

public class OperationStateService : IHostedService
{
    private readonly ILogger<OperationStateService> _logger;
    private readonly StateService _stateService;
    private readonly ConcurrentDictionary<string, OperationState> _states = new();
    private Timer? _cleanupTimer;

    public OperationStateService(ILogger<OperationStateService> logger, StateService stateService)
    {
        _logger = logger;
        _stateService = stateService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadStatesFromStateService();
        // Cleanup expired operations every 5 minutes
        _cleanupTimer = new Timer(CleanupExpired, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Dispose();
        SaveAllStatesToStateService();
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
        SaveStateToStateService(state);
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
            SaveStateToStateService(state);
        }
    }

    public void RemoveState(string key)
    {
        if (_states.TryRemove(key, out _))
        {
            _stateService.RemoveOperationState(key);
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

    private void LoadStatesFromStateService()
    {
        try
        {
            var operations = _stateService.GetOperationStates();
            var now = DateTime.UtcNow;

            foreach (var op in operations)
            {
                // Only load non-expired states
                if (op.UpdatedAt > now.AddHours(-24)) // Use UpdatedAt as expiration check
                {
                    var operationState = new OperationState
                    {
                        Key = op.Id,
                        Type = op.Type,
                        Status = op.Status,
                        Data = op.Data != null ? JsonSerializer.Deserialize<Dictionary<string, object>>(op.Data.ToString() ?? "{}") ?? new() : new(),
                        CreatedAt = op.CreatedAt,
                        UpdatedAt = op.UpdatedAt,
                        ExpiresAt = op.UpdatedAt.AddHours(24) // Set expiration to 24 hours from last update
                    };
                    _states[op.Id] = operationState;
                }
            }

            _logger.LogInformation($"Loaded {_states.Count} operation states from StateService");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load operation states from StateService");
        }
    }

    private void SaveStateToStateService(OperationState state)
    {
        try
        {
            var stateOp = new StateService.OperationState
            {
                Id = state.Key,
                Type = state.Type,
                Status = state.Status ?? "",
                Data = state.Data,
                CreatedAt = state.CreatedAt,
                UpdatedAt = state.UpdatedAt
            };

            _stateService.UpdateState(appState =>
            {
                var existing = appState.OperationStates.FirstOrDefault(o => o.Id == state.Key);
                if (existing != null)
                {
                    appState.OperationStates.Remove(existing);
                }
                appState.OperationStates.Add(stateOp);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save operation state to StateService");
        }
    }

    private void SaveAllStatesToStateService()
    {
        try
        {
            var operations = _states.Values.Select(state => new StateService.OperationState
            {
                Id = state.Key,
                Type = state.Type,
                Status = state.Status ?? "",
                Data = state.Data,
                CreatedAt = state.CreatedAt,
                UpdatedAt = state.UpdatedAt
            }).ToList();

            _stateService.UpdateState(appState =>
            {
                appState.OperationStates = operations;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save all operation states to StateService");
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

                // Remove from state service as well
                foreach (var key in expired)
                {
                    _stateService.RemoveOperationState(key);
                }
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