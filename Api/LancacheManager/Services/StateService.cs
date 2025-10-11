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
    private readonly SecureStateEncryptionService _encryption;
    private readonly string _stateFilePath;
    private readonly object _lock = new object();
    private AppState? _cachedState;
    private int _consecutiveFailures = 0;

    public StateService(
        ILogger<StateService> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _encryption = encryption;
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
        public double CrawlIntervalHours { get; set; } = 1.0; // Default to 1 hour
        public bool CrawlIncrementalMode { get; set; } = true; // Default to incremental scans
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public bool HasDataLoaded { get; set; } = false;
        public DateTime? LastDataLoadTime { get; set; }
        public int LastDataMappingCount { get; set; } = 0;
        public SteamAuthState SteamAuth { get; set; } = new();
    }

    public class SteamAuthState
    {
        public string Mode { get; set; } = "anonymous"; // "anonymous" or "authenticated"
        public string? Username { get; set; }
        public string? RefreshToken { get; set; } // Decrypted in memory, encrypted in storage
        public string? GuardData { get; set; } // Decrypted in memory, encrypted in storage
        public DateTime? LastAuthenticated { get; set; }
    }

    /// <summary>
    /// Internal class used for JSON serialization with encrypted fields
    /// </summary>
    private class PersistedState
    {
        public LogProcessingState LogProcessing { get; set; } = new();
        public DepotProcessingState DepotProcessing { get; set; } = new();
        public List<CacheClearOperation> CacheClearOperations { get; set; } = new();
        public List<OperationState> OperationStates { get; set; } = new();
        public bool SetupCompleted { get; set; } = false;
        public DateTime? LastPicsCrawl { get; set; }
        public double CrawlIntervalHours { get; set; } = 1.0;
        public bool CrawlIncrementalMode { get; set; } = true;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public bool HasDataLoaded { get; set; } = false;
        public DateTime? LastDataLoadTime { get; set; }
        public int LastDataMappingCount { get; set; } = 0;
        public SteamAuthState SteamAuth { get; set; } = new(); // Contains encrypted tokens
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
    /// Gets the current application state (with decrypted sensitive fields)
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
                    var persisted = JsonSerializer.Deserialize<PersistedState>(json) ?? new PersistedState();

                    // Convert persisted state to app state, decrypting sensitive fields
                    _cachedState = ConvertFromPersistedState(persisted);
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
    /// Saves the application state (encrypts sensitive fields before storage)
    /// </summary>
    public void SaveState(AppState state)
    {
        // Skip saves if we've had too many failures
        if (_consecutiveFailures > 5)
        {
            return;
        }

        lock (_lock)
        {
            try
            {
                state.LastUpdated = DateTime.UtcNow;

                // Convert to persisted state with encrypted sensitive fields
                var persisted = ConvertToPersistedState(state);

                var json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true });

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
                _consecutiveFailures = 0; // Reset on success
                _logger.LogTrace("State saved successfully with encrypted sensitive data");
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;

                // Only log first few failures to avoid spam
                if (_consecutiveFailures <= 3)
                {
                    _logger.LogWarning(ex, "Failed to save state (failure #{Count})", _consecutiveFailures);
                }
                else if (_consecutiveFailures == 6)
                {
                    _logger.LogError("Too many consecutive save failures ({Count}), disabling state saves", _consecutiveFailures);
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

    // Data Availability Methods
    public bool HasDataLoaded()
    {
        return GetState().HasDataLoaded;
    }

    public void SetDataLoaded(bool loaded, int mappingCount = 0)
    {
        UpdateState(state =>
        {
            state.HasDataLoaded = loaded;
            if (loaded)
            {
                state.LastDataLoadTime = DateTime.UtcNow;
                state.LastDataMappingCount = mappingCount;
            }
        });
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

    // Crawl Interval Methods
    public double GetCrawlIntervalHours()
    {
        return GetState().CrawlIntervalHours;
    }

    public void SetCrawlIntervalHours(double hours)
    {
        UpdateState(state => state.CrawlIntervalHours = hours);
    }

    // Crawl Mode Methods
    public bool GetCrawlIncrementalMode()
    {
        return GetState().CrawlIncrementalMode;
    }

    public void SetCrawlIncrementalMode(bool incremental)
    {
        UpdateState(state => state.CrawlIncrementalMode = incremental);
    }

    // Depot Processing Methods
    public DepotProcessingState GetDepotProcessingState()
    {
        return GetState().DepotProcessing;
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
    /// Converts persisted state (with encrypted fields) to app state (with decrypted fields)
    /// </summary>
    private AppState ConvertFromPersistedState(PersistedState persisted)
    {
        var state = new AppState
        {
            LogProcessing = persisted.LogProcessing,
            DepotProcessing = persisted.DepotProcessing,
            CacheClearOperations = persisted.CacheClearOperations,
            OperationStates = persisted.OperationStates,
            SetupCompleted = persisted.SetupCompleted,
            LastPicsCrawl = persisted.LastPicsCrawl,
            CrawlIntervalHours = persisted.CrawlIntervalHours,
            CrawlIncrementalMode = persisted.CrawlIncrementalMode,
            LastUpdated = persisted.LastUpdated,
            HasDataLoaded = persisted.HasDataLoaded,
            LastDataLoadTime = persisted.LastDataLoadTime,
            LastDataMappingCount = persisted.LastDataMappingCount,
            SteamAuth = new SteamAuthState
            {
                Mode = persisted.SteamAuth.Mode,
                Username = persisted.SteamAuth.Username,
                // Decrypt sensitive fields
                RefreshToken = _encryption.Decrypt(persisted.SteamAuth.RefreshToken),
                GuardData = _encryption.Decrypt(persisted.SteamAuth.GuardData),
                LastAuthenticated = persisted.SteamAuth.LastAuthenticated
            }
        };

        return state;
    }

    /// <summary>
    /// Converts app state (with decrypted fields) to persisted state (with encrypted fields)
    /// </summary>
    private PersistedState ConvertToPersistedState(AppState state)
    {
        var persisted = new PersistedState
        {
            LogProcessing = state.LogProcessing,
            DepotProcessing = state.DepotProcessing,
            CacheClearOperations = state.CacheClearOperations,
            OperationStates = state.OperationStates,
            SetupCompleted = state.SetupCompleted,
            LastPicsCrawl = state.LastPicsCrawl,
            CrawlIntervalHours = state.CrawlIntervalHours,
            CrawlIncrementalMode = state.CrawlIncrementalMode,
            LastUpdated = state.LastUpdated,
            HasDataLoaded = state.HasDataLoaded,
            LastDataLoadTime = state.LastDataLoadTime,
            LastDataMappingCount = state.LastDataMappingCount,
            SteamAuth = new SteamAuthState
            {
                Mode = state.SteamAuth.Mode,
                Username = state.SteamAuth.Username,
                // Encrypt sensitive fields
                RefreshToken = _encryption.Encrypt(state.SteamAuth.RefreshToken),
                GuardData = _encryption.Encrypt(state.SteamAuth.GuardData),
                LastAuthenticated = state.SteamAuth.LastAuthenticated
            }
        };

        return persisted;
    }

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

    // Steam Authentication Methods
    public string? GetSteamAuthMode()
    {
        return GetState().SteamAuth.Mode;
    }

    public void SetSteamAuthMode(string mode)
    {
        UpdateState(state => state.SteamAuth.Mode = mode);
    }

    public string? GetSteamUsername()
    {
        return GetState().SteamAuth.Username;
    }

    public void SetSteamUsername(string? username)
    {
        UpdateState(state => state.SteamAuth.Username = username);
    }

    public string? GetSteamRefreshToken()
    {
        return GetState().SteamAuth.RefreshToken;
    }

    public void SetSteamRefreshToken(string? token)
    {
        UpdateState(state =>
        {
            state.SteamAuth.RefreshToken = token;
            if (token != null)
            {
                state.SteamAuth.LastAuthenticated = DateTime.UtcNow;
            }
        });
    }

    public bool HasSteamRefreshToken()
    {
        return !string.IsNullOrEmpty(GetState().SteamAuth.RefreshToken);
    }

    public string? GetSteamGuardData()
    {
        return GetState().SteamAuth.GuardData;
    }

    public void SetSteamGuardData(string? guardData)
    {
        UpdateState(state => state.SteamAuth.GuardData = guardData);
    }
}