using System.Collections.Concurrent;
using System.Text.Json;
using LancacheManager.Models;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using ModelOperationState = LancacheManager.Models.OperationState;

namespace LancacheManager.Core.Services;

public class OperationStateService : ScheduledBackgroundService
{
    private readonly StateService _stateService;
    private readonly ConcurrentDictionary<string, OperationState> _states = new();

    protected override string ServiceName => "OperationStateService";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);
    protected override TimeSpan StartupDelay => TimeSpan.FromSeconds(2);
    public override bool DefaultRunOnStartup => true;

    public OperationStateService(
        ILogger<OperationStateService> logger,
        IConfiguration configuration,
        StateService stateService)
        : base(logger, configuration)
    {
        _stateService = stateService;
    }

    protected override Task OnStartupAsync(CancellationToken stoppingToken)
    {
        LoadStates();
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        PersistAllStates();
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
        var hydrated = LoadState(key);
        if (hydrated != null && hydrated.ExpiresAt > DateTime.UtcNow)
        {
            return hydrated;
        }
        return null;
    }

    public void SaveState(string key, OperationState state)
    {
        state.CreatedAt = state.CreatedAt == default ? DateTime.UtcNow : state.CreatedAt;
        if (state.ExpiresAt == default || state.ExpiresAt <= DateTime.UtcNow)
        {
            state.ExpiresAt = DateTime.UtcNow.AddHours(24);
        }
        state.UpdatedAt = DateTime.UtcNow;
        _states[key] = state;
        PersistState(state);
    }

    public void UpdateState(string key, Dictionary<string, object> updates)
    {
        if (updates == null || updates.Count == 0)
        {
            return;
        }

        var state = GetOrCreateState(key);
        var dataDict = state.GetDataAsDictionary();

        foreach (var kvp in updates)
        {
            dataDict[kvp.Key] = kvp.Value;
        }

        // Convert back to JsonElement
        var json = JsonSerializer.Serialize(dataDict);
        state.Data = JsonSerializer.Deserialize<JsonElement>(json);

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
        PersistState(state);
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

        var hydrated = LoadState(key);
        if (hydrated != null)
        {
            return hydrated;
        }

        var created = new OperationState
        {
            Key = key,
            Type = "unknown",
            Data = JsonSerializer.SerializeToElement(new Dictionary<string, object>()),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        };

        _states[key] = created;
        return created;
    }

    /// <summary>
    /// Reads one persisted operation state back into the in-memory map.
    /// </summary>
    /// <returns>
    /// Null both when nothing was persisted under this key and when reading it back failed. A
    /// caller cannot tell the two apart, so it must treat null as "no state to resume", never as
    /// proof the operation never ran.
    /// </returns>
    private OperationState? LoadState(string key)
    {
        try
        {
            if (!Guid.TryParse(key, out var parsedKey))
            {
                return null;
            }

            var persisted = _stateService.GetOperationStates()
                .FirstOrDefault(o => o.Id == parsedKey);

            if (persisted == null)
            {
                return null;
            }

            if (persisted.UpdatedAt <= DateTime.UtcNow.AddHours(-24))
            {
                return null;
            }

            var mapped = ToOperationState(persisted);
            _states[key] = mapped;
            return mapped;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to hydrate operation state {Key} from persistence", key);
            return null;
        }
    }

    private OperationState ToOperationState(ModelOperationState persisted)
    {
        return new OperationState
        {
            Key = persisted.Id.ToString(),
            Type = persisted.Type.ToWireString(),
            Status = persisted.Status.ToWireString(),
            Message = persisted.Message,
            Data = ToJsonElement(persisted.Data),
            CreatedAt = persisted.CreatedAt,
            UpdatedAt = persisted.UpdatedAt,
            ExpiresAt = persisted.UpdatedAt.AddHours(24)
        };
    }

    private static JsonElement? ToJsonElement(object? data)
    {
        if (data == null)
        {
            return null;
        }

        if (data is JsonElement jsonElement)
        {
            return jsonElement;
        }

        if (data is Dictionary<string, object> dictionary)
        {
            return JsonSerializer.SerializeToElement(dictionary);
        }

        if (data is string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<JsonElement>(raw);
            }
            catch
            {
                // If it's not valid JSON, wrap it as a string value
                return JsonSerializer.SerializeToElement(new Dictionary<string, string> { { "value", raw } });
            }
        }

        // For any other type, try to serialize it
        try
        {
            return JsonSerializer.SerializeToElement(data);
        }
        catch
        {
            return null;
        }
    }

    private void LoadStates()
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
                    var operationState = ToOperationState(op);
                    operationState.ExpiresAt = operationState.UpdatedAt.AddHours(24);
                    _states[op.Id.ToString()] = operationState;
                }
            }

            _logger.LogInformation("Loaded {Count} operation states from StateService", _states.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load operation states from StateService");
        }
    }

    private static ModelOperationState? TryToModel(OperationState state)
    {
        if (!Guid.TryParse(state.Key, out var parsedKey))
        {
            return null;
        }

        var parsedType = OperationTypeExtensions.TryParseWire(state.Type);
        if (!parsedType.HasValue)
        {
            return null;
        }

        return new ModelOperationState
        {
            Id = parsedKey,
            Type = parsedType.Value,
            Status = Enum.TryParse<OperationStatus>(state.Status, ignoreCase: true, out var parsedStatus)
                ? parsedStatus
                : OperationStatus.Running,
            Data = state.Data,
            CreatedAt = state.CreatedAt,
            UpdatedAt = state.UpdatedAt
        };
    }

    private void PersistState(OperationState state)
    {
        try
        {
            var stateOp = TryToModel(state);
            if (stateOp == null)
            {
                return;
            }

            _stateService.UpdateOperationStates(states =>
            {
                var existing = states.FirstOrDefault(o => o.Id == stateOp.Id);
                if (existing != null)
                {
                    states.Remove(existing);
                }
                states.Add(stateOp);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save operation state to StateService");
        }
    }

    private void PersistAllStates()
    {
        try
        {
            var operations = _states.Values
                .Select(TryToModel)
                .Where(op => op != null)
                .Cast<ModelOperationState>()
                .ToList();

            _stateService.UpdateOperationStates(states =>
            {
                states.Clear();
                states.AddRange(operations);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save all operation states to StateService");
        }
    }

    protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
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
            // Remove from state service as well
            foreach (var key in expired)
            {
                _stateService.RemoveOperationState(key);
            }
        }

        return Task.CompletedTask;
    }

}

public class OperationState
{
    public string Key { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public JsonElement? Data { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }

    /// <summary>
    /// Helper method to get Data as a dictionary for backward compatibility
    /// </summary>
    public Dictionary<string, object> GetDataAsDictionary()
    {
        if (Data == null || Data.Value.ValueKind == JsonValueKind.Null || Data.Value.ValueKind == JsonValueKind.Undefined)
            return new Dictionary<string, object>();

        if (Data.Value.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, object>();
            foreach (var prop in Data.Value.EnumerateObject())
            {
                dict[prop.Name] = ConvertJsonElementToObject(prop.Value);
            }
            return dict;
        }

        return new Dictionary<string, object>();
    }

    private static object ConvertJsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null!,
            JsonValueKind.Object => element.Deserialize<Dictionary<string, object>>() ?? new Dictionary<string, object>(),
            JsonValueKind.Array => element.Deserialize<List<object>>() ?? new List<object>(),
            _ => element.GetRawText()
        };
    }
}
