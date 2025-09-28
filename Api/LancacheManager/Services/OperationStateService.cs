using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using LancacheManager.Services;

namespace LancacheManager.Services;

public class OperationStateService : IHostedService
{
    private readonly ILogger<OperationStateService> _logger;
    private readonly StateService _stateService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, OperationState> _states = new();
    private Timer? _cleanupTimer;

    public OperationStateService(ILogger<OperationStateService> logger, StateService stateService, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _stateService = stateService;
        _serviceProvider = serviceProvider;
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

        // Attempt to hydrate from persisted state if not already in memory
        var hydrated = HydrateStateFromPersistence(key);
        if (hydrated != null && hydrated.ExpiresAt > DateTime.UtcNow)
        {
            return hydrated;
        }
        return null;
    }

    public void SaveState(string key, OperationState state)
    {
        state.Data ??= new Dictionary<string, object>();
        state.CreatedAt = state.CreatedAt == default ? DateTime.UtcNow : state.CreatedAt;
        if (state.ExpiresAt == default || state.ExpiresAt <= DateTime.UtcNow)
        {
            state.ExpiresAt = DateTime.UtcNow.AddHours(24);
        }
        state.UpdatedAt = DateTime.UtcNow;
        _states[key] = state;
        SaveStateToStateService(state);
    }

    public void UpdateState(string key, Dictionary<string, object> updates)
    {
        if (updates == null || updates.Count == 0)
        {
            return;
        }

        var state = GetOrCreateState(key);

        foreach (var kvp in updates)
        {
            state.Data[kvp.Key] = kvp.Value;
        }

        if (updates.TryGetValue("status", out var statusObj) && statusObj is string statusString)
        {
            state.Status = statusString;
        }

        if (updates.TryGetValue("message", out var messageObj) && messageObj is string messageString)
        {
            state.Message = messageString;
        }

        state.UpdatedAt = DateTime.UtcNow;
        state.ExpiresAt = state.UpdatedAt.AddHours(24);
        _states[key] = state;
        SaveStateToStateService(state);
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

    private OperationState GetOrCreateState(string key)
    {
        if (_states.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var hydrated = HydrateStateFromPersistence(key);
        if (hydrated != null)
        {
            return hydrated;
        }

        var created = new OperationState
        {
            Key = key,
            Type = "unknown",
            Data = new Dictionary<string, object>(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        };

        _states[key] = created;
        return created;
    }

    private OperationState? HydrateStateFromPersistence(string key)
    {
        try
        {
            var persisted = _stateService.GetOperationStates()
                .FirstOrDefault(o => o.Id == key);

            if (persisted == null)
            {
                return null;
            }

            if (persisted.UpdatedAt <= DateTime.UtcNow.AddHours(-24))
            {
                _logger.LogDebug("Persisted state {Key} is older than 24 hours and will be ignored", key);
                return null;
            }

            var mapped = MapPersistedState(persisted);
            _states[key] = mapped;
            return mapped;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to hydrate operation state {Key} from persistence", key);
            return null;
        }
    }

    private OperationState MapPersistedState(StateService.OperationState persisted)
    {
        return new OperationState
        {
            Key = persisted.Id,
            Type = string.IsNullOrWhiteSpace(persisted.Type) ? "unknown" : persisted.Type,
            Status = persisted.Status,
            Message = persisted.Message,
            Data = ConvertDataToDictionary(persisted.Data),
            CreatedAt = persisted.CreatedAt,
            UpdatedAt = persisted.UpdatedAt,
            ExpiresAt = persisted.UpdatedAt.AddHours(24)
        };
    }

    private static Dictionary<string, object> ConvertDataToDictionary(object? data)
    {
        if (data is Dictionary<string, object> dictionary)
        {
            return new Dictionary<string, object>(dictionary);
        }

        if (data is JsonElement jsonElement)
        {
            switch (jsonElement.ValueKind)
            {
                case JsonValueKind.Object:
                    var result = new Dictionary<string, object>();
                    foreach (var property in jsonElement.EnumerateObject())
                    {
                        result[property.Name] = property.Value.ValueKind switch
                        {
                            JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                            JsonValueKind.Number => property.Value.TryGetInt64(out var longValue)
                                ? longValue
                                : property.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => null,
                            JsonValueKind.Undefined => null,
                            _ => property.Value.ToString() ?? string.Empty
                        };
                    }
                    return result;
                case JsonValueKind.Array:
                    return new Dictionary<string, object>
                    {
                        { "value", jsonElement.ToString() ?? string.Empty }
                    };
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return new Dictionary<string, object>();
                default:
                    return new Dictionary<string, object>
                    {
                        { "value", jsonElement.ToString() ?? string.Empty }
                    };
            }
        }

        if (data is string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new Dictionary<string, object>();
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(raw);
                if (parsed != null)
                {
                    return parsed;
                }
            }
            catch
            {
                // Ignore parse failures and fall back to empty dictionary below
            }

            return new Dictionary<string, object>();
        }

        if (data != null)
        {
            return new Dictionary<string, object>
            {
                { "value", data }
            };
        }

        return new Dictionary<string, object>();
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
                    var operationState = MapPersistedState(op);
                    operationState.ExpiresAt = operationState.UpdatedAt.AddHours(24);
                    _states[op.Id] = operationState;
                }
            }

            _logger.LogInformation($"Loaded {_states.Count} operation states from StateService");

            // Check for interrupted log processing operations and mark them for resume
            CheckAndMarkInterruptedOperationsForResume();
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

    /// <summary>
    /// Checks for interrupted log processing operations and marks them for resume
    /// </summary>
    private void CheckAndMarkInterruptedOperationsForResume()
    {
        _logger.LogInformation("Checking for interrupted log processing operations...");

        try
        {
            // Look for activeLogProcessing operation
            if (_states.TryGetValue("activeLogProcessing", out var activeLogOperation) &&
                activeLogOperation.Type == "logProcessing" &&
                activeLogOperation.Data != null)
            {
                _logger.LogInformation("Found activeLogProcessing operation");

                // Check if the operation was processing when interrupted
                if (activeLogOperation.Data.TryGetValue("isProcessing", out var isProcessingObj))
                {
                    bool isProcessing = false;

                    // Handle both boolean and JsonElement types
                    if (isProcessingObj is bool boolValue)
                    {
                        isProcessing = boolValue;
                    }
                    else if (isProcessingObj is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.True)
                    {
                        isProcessing = true;
                    }

                    _logger.LogInformation("isProcessing value: {Value}, type: {Type}, evaluated as: {IsProcessing}",
                        isProcessingObj, isProcessingObj?.GetType(), isProcessing);

                    if (isProcessing)
                    {
                        // Check if the operation is actually complete (stuck at 99.9% or higher)
                        double percentComplete = 0;
                        if (activeLogOperation.Data.TryGetValue("percentComplete", out var percentObj))
                        {
                            if (percentObj is double dbl)
                                percentComplete = dbl;
                            else if (percentObj is JsonElement elem && elem.ValueKind == JsonValueKind.Number)
                                percentComplete = elem.GetDouble();
                            else if (double.TryParse(percentObj?.ToString(), out var parsed))
                                percentComplete = parsed;
                        }

                        _logger.LogInformation("Found interrupted log processing operation with {PercentComplete}% complete", percentComplete);

                        // If we're at 99.9% or higher, mark as complete instead of resuming
                        if (percentComplete >= 99.9)
                        {
                            _logger.LogInformation("Operation was near completion ({PercentComplete}%), marking as complete instead of resuming", percentComplete);

                            // Mark as complete
                            activeLogOperation.Data["isProcessing"] = false;
                            activeLogOperation.Data["status"] = "complete";
                            activeLogOperation.Data["percentComplete"] = 100.0;
                            activeLogOperation.Data["completedAt"] = DateTime.UtcNow;
                            activeLogOperation.Status = "complete";
                            activeLogOperation.UpdatedAt = DateTime.UtcNow;

                            // Update the state in memory and persist it
                            _states["activeLogProcessing"] = activeLogOperation;
                            SaveStateToStateService(activeLogOperation);

                            _logger.LogInformation("Marked stuck operation as complete");
                        }
                        else
                        {
                            _logger.LogInformation("Found interrupted log processing operation at {PercentComplete}%, marking for resume", percentComplete);

                            // Set resume flag to true
                            activeLogOperation.Data["resume"] = true;
                            activeLogOperation.Data["status"] = "resuming";
                            activeLogOperation.UpdatedAt = DateTime.UtcNow;

                            // Update the state in memory and persist it
                            _states["activeLogProcessing"] = activeLogOperation;
                            SaveStateToStateService(activeLogOperation);

                            _logger.LogInformation("Updated operation state to resume mode");

                            // Start the log processing services to actually resume processing
                            _ = Task.Run(async () => await StartLogProcessingServices());
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Found log processing operation but isProcessing is false");
                    }
                }
                else
                {
                    _logger.LogInformation("isProcessing key not found in operation data");
                }
            }
            else
            {
                _logger.LogInformation("No activeLogProcessing operation found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check for interrupted log processing operations");
        }
    }

    /// <summary>
    /// Starts the log processing services to resume interrupted log processing
    /// </summary>
    private async Task StartLogProcessingServices()
    {
        try
        {
            _logger.LogInformation("Starting log processing services to resume interrupted operation");

            using var scope = _serviceProvider.CreateScope();
            var logProcessingService = scope.ServiceProvider.GetRequiredService<LogProcessingService>();
            var logWatcherService = scope.ServiceProvider.GetRequiredService<LogWatcherService>();

            // Create a cancellation token for the services (similar to ManagementController)
            var cancellationTokenSource = new CancellationTokenSource();

            // Start the log processing services for manual processing
            _logger.LogInformation("Starting LogProcessingService for resumed log processing");
            await logProcessingService.StartAsync(cancellationTokenSource.Token);

            _logger.LogInformation("Starting LogWatcherService for resumed log processing");
            await logWatcherService.StartAsync(cancellationTokenSource.Token);

            _logger.LogInformation("Log processing services started successfully for resume operation");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start log processing services for resume operation");
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
