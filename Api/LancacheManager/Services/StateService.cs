using System.Text.Json;

namespace LancacheManager.Services;

/// <summary>
/// Manages consolidated operational state in state.json
/// Replaces individual files: position.txt, cache_clear_status.json, operation_states.json
/// </summary>
public class StateService
{
    private readonly ILogger<StateService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly string _stateFilePath;
    private readonly object _lock = new object();
    private AppState? _cachedState;

    public StateService(ILogger<StateService> logger, IPathResolver pathResolver)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _stateFilePath = Path.Combine(_pathResolver.GetDataDirectory(), "state.json");
    }

    public class AppState
    {
        public LogProcessingState LogProcessing { get; set; } = new();
        public DepotProcessingState DepotProcessing { get; set; } = new();
        public List<CacheClearOperation> CacheClearOperations { get; set; } = new();
        public List<OperationState> OperationStates { get; set; } = new();
        public bool SetupCompleted { get; set; } = false;
        public DateTime? LastPicsCrawl { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }

    public class LogProcessingState
    {
        public long Position { get; set; } = 0;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }

    public class DepotProcessingState
    {
        public bool IsActive { get; set; } = false;
        public string Status { get; set; } = "Idle";
        public int TotalApps { get; set; } = 0;
        public int ProcessedApps { get; set; } = 0;
        public int TotalBatches { get; set; } = 0;
        public int ProcessedBatches { get; set; } = 0;
        public double ProgressPercent { get; set; } = 0;
        public int DepotMappingsFound { get; set; } = 0;
        public DateTime? StartTime { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public uint LastChangeNumber { get; set; } = 0;
        public List<uint> RemainingApps { get; set; } = new();
    }

    public class CacheClearOperation
    {
        public string Id { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int Progress { get; set; } = 0;
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string? Error { get; set; }
    }

    public class OperationState
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public object? Data { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets the current application state
    /// </summary>
    public AppState GetState()
    {
        lock (_lock)
        {
            if (_cachedState != null)
            {
                return _cachedState;
            }

            try
            {
                if (File.Exists(_stateFilePath))
                {
                    var json = File.ReadAllText(_stateFilePath);
                    _cachedState = JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
                    CleanupStaleOperations(_cachedState);
                }
                else
                {
                    _cachedState = MigrateFromLegacyFiles();
                    SaveState(_cachedState);
                }

                return _cachedState;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load state, using default");
                _cachedState = new AppState();
                return _cachedState;
            }
        }
    }

    /// <summary>
    /// Saves the application state
    /// </summary>
    public void SaveState(AppState state)
    {
        lock (_lock)
        {
            try
            {
                state.LastUpdated = DateTime.UtcNow;
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });

                // Write to temp file first then move (atomic operation)
                var tempFile = _stateFilePath + ".tmp";
                File.WriteAllText(tempFile, json);

                // Force flush to disk
                using (var fs = File.OpenWrite(tempFile))
                {
                    fs.Flush(true);
                }

                // Atomically replace the old file
                File.Move(tempFile, _stateFilePath, true);

                _cachedState = state;
                _logger.LogTrace("State saved successfully with position: {Position}", state.LogProcessing.Position);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save state");
                // Try direct write as fallback
                try
                {
                    File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogError(fallbackEx, "Fallback save also failed");
                }
            }
        }
    }

    /// <summary>
    /// Updates a specific part of the state
    /// </summary>
    public void UpdateState(Action<AppState> updater)
    {
        lock (_lock)
        {
            var state = GetState();
            updater(state);
            SaveState(state);
        }
    }

    // Log Processing Methods
    public long GetLogPosition()
    {
        return GetState().LogProcessing.Position;
    }

    public void SetLogPosition(long position)
    {
        UpdateState(state =>
        {
            state.LogProcessing.Position = position;
            state.LogProcessing.LastUpdated = DateTime.UtcNow;
        });
    }

    // Cache Clear Operations Methods
    public List<CacheClearOperation> GetCacheClearOperations()
    {
        return GetState().CacheClearOperations;
    }

    public void AddCacheClearOperation(CacheClearOperation operation)
    {
        UpdateState(state => state.CacheClearOperations.Add(operation));
    }

    public void UpdateCacheClearOperation(string id, Action<CacheClearOperation> updater)
    {
        UpdateState(state =>
        {
            var operation = state.CacheClearOperations.FirstOrDefault(o => o.Id == id);
            if (operation != null)
            {
                updater(operation);
            }
        });
    }

    public void RemoveCacheClearOperation(string id)
    {
        UpdateState(state => state.CacheClearOperations.RemoveAll(o => o.Id == id));
    }

    // Operation States Methods
    public List<OperationState> GetOperationStates()
    {
        return GetState().OperationStates;
    }

    public void AddOperationState(OperationState operation)
    {
        UpdateState(state => state.OperationStates.Add(operation));
    }

    public void UpdateOperationState(string id, Action<OperationState> updater)
    {
        UpdateState(state =>
        {
            var operation = state.OperationStates.FirstOrDefault(o => o.Id == id);
            if (operation != null)
            {
                updater(operation);
                operation.UpdatedAt = DateTime.UtcNow;
            }
        });
    }

    public void RemoveOperationState(string id)
    {
        UpdateState(state => state.OperationStates.RemoveAll(o => o.Id == id));
    }

    // Setup Completed Methods
    public bool GetSetupCompleted()
    {
        return GetState().SetupCompleted;
    }

    public void SetSetupCompleted(bool completed)
    {
        UpdateState(state => state.SetupCompleted = completed);
    }

    // Last PICS Crawl Methods
    public DateTime? GetLastPicsCrawl()
    {
        return GetState().LastPicsCrawl;
    }

    public void SetLastPicsCrawl(DateTime crawlTime)
    {
        UpdateState(state => state.LastPicsCrawl = crawlTime);
    }

    // Depot Processing Methods
    public DepotProcessingState GetDepotProcessingState()
    {
        return GetState().DepotProcessing;
    }

    public void UpdateDepotProcessingState(Action<DepotProcessingState> updater)
    {
        UpdateState(state =>
        {
            updater(state.DepotProcessing);
            state.DepotProcessing.LastUpdated = DateTime.UtcNow;
        });
    }

    public void ClearDepotProcessingState()
    {
        UpdateState(state =>
        {
            state.DepotProcessing = new DepotProcessingState();
        });
    }

    /// <summary>
    /// Migrates from legacy individual files to consolidated state
    /// </summary>
    private AppState MigrateFromLegacyFiles()
    {
        var state = new AppState();

        try
        {
            // Migrate position.txt
            var positionFile = Path.Combine(_pathResolver.GetDataDirectory(), "position.txt");
            if (File.Exists(positionFile))
            {
                var positionText = File.ReadAllText(positionFile).Trim();
                if (long.TryParse(positionText, out var position))
                {
                    state.LogProcessing.Position = position;
                }
                _logger.LogInformation("Migrated log position: {Position}", state.LogProcessing.Position);
            }

            // Migrate cache_clear_status.json
            var cacheClearFile = Path.Combine(_pathResolver.GetDataDirectory(), "cache_clear_status.json");
            if (File.Exists(cacheClearFile))
            {
                var cacheClearJson = File.ReadAllText(cacheClearFile);
                if (!string.IsNullOrWhiteSpace(cacheClearJson) && cacheClearJson != "[]")
                {
                    var operations = JsonSerializer.Deserialize<List<CacheClearOperation>>(cacheClearJson);
                    if (operations != null)
                    {
                        state.CacheClearOperations = operations;
                    }
                }
                _logger.LogInformation("Migrated cache clear operations: {Count}", state.CacheClearOperations.Count);
            }

            // Migrate operation_states.json
            var operationStatesFile = Path.Combine(_pathResolver.GetDataDirectory(), "operation_states.json");
            if (File.Exists(operationStatesFile))
            {
                var operationStatesJson = File.ReadAllText(operationStatesFile);
                if (!string.IsNullOrWhiteSpace(operationStatesJson) && operationStatesJson != "[]")
                {
                    var operations = JsonSerializer.Deserialize<List<OperationState>>(operationStatesJson);
                    if (operations != null)
                    {
                        state.OperationStates = operations;
                    }
                }
                _logger.LogInformation("Migrated operation states: {Count}", state.OperationStates.Count);
            }

            // Migrate setup_completed.txt
            var setupCompletedFile = Path.Combine(_pathResolver.GetDataDirectory(), "setup_completed.txt");
            if (File.Exists(setupCompletedFile))
            {
                var content = File.ReadAllText(setupCompletedFile).Trim();
                state.SetupCompleted = content == "1" || content.ToLower() == "true";
                _logger.LogInformation("Migrated setup completed status: {SetupCompleted}", state.SetupCompleted);
            }

            // Migrate last_pics_crawl.txt
            var lastPicsCrawlFile = Path.Combine(_pathResolver.GetDataDirectory(), "last_pics_crawl.txt");
            if (File.Exists(lastPicsCrawlFile))
            {
                var content = File.ReadAllText(lastPicsCrawlFile).Trim();
                if (DateTime.TryParse(content, out var lastCrawl))
                {
                    state.LastPicsCrawl = lastCrawl;
                    _logger.LogInformation("Migrated last PICS crawl time: {LastPicsCrawl}", state.LastPicsCrawl);
                }
            }

            _logger.LogInformation("Migration from legacy files completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during migration from legacy files");
        }

        return state;
    }

    /// <summary>
    /// Cleans up legacy files after successful migration
    /// </summary>
    /// <summary>
    /// Cleans up stale operations that were left in processing state
    /// </summary>
    private void CleanupStaleOperations(AppState state)
    {
        var staleOperations = state.OperationStates
            .Where(o => o.Type == "log_processing" && o.Status == "processing")
            .ToList();

        if (staleOperations.Any())
        {
            foreach (var operation in staleOperations)
            {
                _logger.LogWarning("Removing stale log processing operation: {Id} from {UpdatedAt}",
                    operation.Id, operation.UpdatedAt);
                state.OperationStates.Remove(operation);
            }

            SaveState(state);
            _logger.LogInformation("Cleaned up {Count} stale operations", staleOperations.Count);
        }
    }

    public void CleanupLegacyFiles()
    {
        var filesToRemove = new[]
        {
            Path.Combine(_pathResolver.GetDataDirectory(), "position.txt"),
            Path.Combine(_pathResolver.GetDataDirectory(), "cache_clear_status.json"),
            Path.Combine(_pathResolver.GetDataDirectory(), "operation_states.json"),
            Path.Combine(_pathResolver.GetDataDirectory(), "setup_completed.txt"),
            Path.Combine(_pathResolver.GetDataDirectory(), "last_pics_crawl.txt")
        };

        foreach (var file in filesToRemove)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    _logger.LogInformation("Removed legacy file: {File}", Path.GetFileName(file));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove legacy file: {File}", file);
            }
        }
    }
}