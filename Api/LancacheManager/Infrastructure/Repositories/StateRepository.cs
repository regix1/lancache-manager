using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Core.Interfaces.Repositories;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces.Services;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Repositories;

/// <summary>
/// Repository for managing consolidated operational state in state.json
/// Replaces individual files: position.txt, cache_clear_status.json, operation_states.json
/// </summary>
public class StateRepository : IStateRepository
{
    private readonly ILogger<StateRepository> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly SecureStateEncryptionService _encryption;
    private readonly SteamAuthRepository _steamAuthStorage;
    private readonly string _stateFilePath;
    private readonly string _operationHistoryFilePath;
    private readonly string _cacheOperationsFilePath;
    private readonly object _lock = new object();
    private readonly object _operationLock = new object();
    private readonly object _cacheClearLock = new object();
    private AppState? _cachedState;
    private List<OperationState>? _cachedOperationStates;
    private List<CacheClearOperation>? _cachedCacheClearOperations;
    private int _consecutiveFailures = 0;
    private bool _migrationAttempted = false;

    public StateRepository(
        ILogger<StateRepository> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption,
        SteamAuthRepository steamAuthStorage)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _encryption = encryption;
        _steamAuthStorage = steamAuthStorage;
        _stateFilePath = Path.Combine(_pathResolver.GetDataDirectory(), "state.json");
        _operationHistoryFilePath = Path.Combine(_pathResolver.GetOperationsDirectory(), "operation_history.json");
        _cacheOperationsFilePath = Path.Combine(_pathResolver.GetOperationsDirectory(), "cache_operations.json");
    }

    public class AppState
    {
        public LogProcessingState LogProcessing { get; set; } = new();
        public DepotProcessingState DepotProcessing { get; set; } = new();
        // LEGACY: CacheClearOperations moved to separate file (data/operations/cache_operations.json)
        [JsonIgnore]
        public List<CacheClearOperation> CacheClearOperations { get; set; } = new();
        // LEGACY: OperationStates moved to separate file (data/operations/operation_history.json)
        [JsonIgnore]
        public List<OperationState> OperationStates { get; set; } = new();
        public bool SetupCompleted { get; set; } = false;
        public DateTime? LastPicsCrawl { get; set; }
        public double CrawlIntervalHours { get; set; } = 1.0; // Default to 1 hour
        public object CrawlIncrementalMode { get; set; } = true; // Default to incremental scans (true/false/"github")
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public bool HasDataLoaded { get; set; } = false;
        public bool HasProcessedLogs { get; set; } = false; // Track if logs have been processed at least once
        public int GuestSessionDurationHours { get; set; } = 6; // Default to 6 hours
        public bool GuestModeLocked { get; set; } = false; // When true, guest mode login is disabled
        public string? SelectedTheme { get; set; } = "dark-default"; // Default theme for authenticated users
        public string? DefaultGuestTheme { get; set; } = "dark-default"; // Default theme for guest users
        public string RefreshRate { get; set; } = "STANDARD"; // Default to 10 seconds (LIVE, ULTRA, REALTIME, STANDARD, RELAXED, SLOW)
        public string DefaultGuestRefreshRate { get; set; } = "STANDARD"; // Default refresh rate for guest users
        
        // Default guest preferences (applied to new guest sessions)
        public bool DefaultGuestUseLocalTimezone { get; set; } = false;
        public bool DefaultGuestUse24HourFormat { get; set; } = true;
        public bool DefaultGuestSharpCorners { get; set; } = false;
        public bool DefaultGuestDisableTooltips { get; set; } = false;
        public bool DefaultGuestShowDatasourceLabels { get; set; } = true;
        public bool DefaultGuestShowYearInDates { get; set; } = false;

        // Allowed time formats for guests (e.g., ["server-24h", "server-12h", "local-24h", "local-12h"])
        // If empty or null, all formats are allowed
        public List<string> AllowedTimeFormats { get; set; } = new() { "server-24h", "server-12h", "local-24h", "local-12h" };

        // Guest prefill permissions - controls access to the Prefill tab for guests
        public bool GuestPrefillEnabledByDefault { get; set; } = false; // Whether new guests get prefill access by default
        public int GuestPrefillDurationHours { get; set; } = 2; // Default duration for prefill access (1 or 2 hours)

        // PICS viability check caching (prevents repeated Steam API calls)
        public bool RequiresFullScan { get; set; } = false; // True if Steam requires full scan due to large change gap
        public DateTime? LastViabilityCheck { get; set; } // When we last checked with Steam
        public uint LastViabilityCheckChangeNumber { get; set; } = 0; // Change number at time of last check
        public uint ViabilityChangeGap { get; set; } = 0; // Change gap at time of last check

        // Steam session replacement tracking (persisted to survive restarts)
        public int SessionReplacedCount { get; set; } = 0; // Counter for session replacement errors
        public DateTime? LastSessionReplacement { get; set; } // When the last session replacement occurred

        // Metrics authentication toggle (null = use env var default, true/false = UI override)
        public bool? RequireAuthForMetrics { get; set; } = null;

        // Client IPs to exclude from stats calculations
        public List<string> ExcludedClientIps { get; set; } = new();

        // Client IP exclusion rules (mode controls stats-only vs hide)
        public List<ClientExclusionRule> ExcludedClientRules { get; set; } = new();

        // LEGACY: SteamAuth has been migrated to separate file (data/steam_auth/credentials.json)
        // This property is kept temporarily for backward compatibility during migration
        public SteamAuthState? SteamAuth { get; set; }
    }

    public class SteamAuthState
    {
        public string Mode { get; set; } = "anonymous"; // "anonymous" or "authenticated"
        public string? Username { get; set; }
        public string? RefreshToken { get; set; } // Decrypted in memory, encrypted in storage
        // NOTE: GuardData removed - modern Steam auth uses refresh tokens only
        public DateTime? LastAuthenticated { get; set; }
    }

    /// <summary>
    /// Internal class used for JSON serialization with encrypted fields
    /// </summary>
    private class PersistedState
    {
        public LogProcessingState LogProcessing { get; set; } = new();
        public DepotProcessingState DepotProcessing { get; set; } = new();
        // CacheClearOperations moved to data/operations/cache_operations.json
        // OperationStates moved to data/operations/operation_history.json
        public bool SetupCompleted { get; set; } = false;
        public DateTime? LastPicsCrawl { get; set; }
        public double CrawlIntervalHours { get; set; } = 1.0;
        public object CrawlIncrementalMode { get; set; } = true;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public bool HasDataLoaded { get; set; } = false;
        public bool HasProcessedLogs { get; set; } = false;
        public int GuestSessionDurationHours { get; set; } = 6;
        public bool GuestModeLocked { get; set; } = false;
        public string? SelectedTheme { get; set; } = "dark-default";
        public string? DefaultGuestTheme { get; set; } = "dark-default";
        public string RefreshRate { get; set; } = "STANDARD";
        public string DefaultGuestRefreshRate { get; set; } = "STANDARD";
        
        // Default guest preferences
        public bool DefaultGuestUseLocalTimezone { get; set; } = false;
        public bool DefaultGuestUse24HourFormat { get; set; } = true;
        public bool DefaultGuestSharpCorners { get; set; } = false;
        public bool DefaultGuestDisableTooltips { get; set; } = false;
        public bool DefaultGuestShowDatasourceLabels { get; set; } = true;
        public bool DefaultGuestShowYearInDates { get; set; } = false;

        // Allowed time formats for guests
        public List<string> AllowedTimeFormats { get; set; } = new() { "server-24h", "server-12h", "local-24h", "local-12h" };

        // Guest prefill permissions
        public bool GuestPrefillEnabledByDefault { get; set; } = false;
        public int GuestPrefillDurationHours { get; set; } = 2;

        // PICS viability check caching
        public bool RequiresFullScan { get; set; } = false;
        public DateTime? LastViabilityCheck { get; set; }
        public uint LastViabilityCheckChangeNumber { get; set; } = 0;
        public uint ViabilityChangeGap { get; set; } = 0;

        // Steam session replacement tracking
        public int SessionReplacedCount { get; set; } = 0;
        public DateTime? LastSessionReplacement { get; set; }

        // Metrics authentication toggle (null = use env var default)
        public bool? RequireAuthForMetrics { get; set; } = null;

        // Client IPs to exclude from stats calculations
        public List<string> ExcludedClientIps { get; set; } = new();

        // Client IP exclusion rules (mode controls stats-only vs hide)
        public List<ClientExclusionRule> ExcludedClientRules { get; set; } = new();

        // LEGACY: SteamAuth migrated to separate file - kept for reading old state.json during migration
        // JsonIgnore(Condition = WhenWritingNull) excludes it when saving (always null after migration)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SteamAuthState? SteamAuth { get; set; }
    }

    public class LogProcessingState
    {
        /// <summary>
        /// Legacy single position (for backward compatibility with single datasource).
        /// Use DatasourcePositions for multi-datasource support.
        /// </summary>
        public long Position { get; set; } = 0;

        /// <summary>
        /// Per-datasource log positions. Key is datasource name, value is line number.
        /// </summary>
        public Dictionary<string, long> DatasourcePositions { get; set; } = new();

        /// <summary>
        /// Per-datasource total line counts. Key is datasource name, value is total lines.
        /// Populated by Rust processor to avoid C# recounting.
        /// </summary>
        public Dictionary<string, long> DatasourceTotalLines { get; set; } = new();

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

                    // Migrate Steam auth data to separate file (one-time migration)
                    if (!_migrationAttempted)
                    {
                        _steamAuthStorage.MigrateFromStateJson(_cachedState.SteamAuth);
                        _migrationAttempted = true;

                        // Always clear Steam auth from main state after migration attempt
                        // (Steam auth now lives in separate file: data/steam_auth/credentials.json)
                        // Setting to null will exclude it from JSON serialization
                        var hadSteamAuth = _cachedState.SteamAuth != null &&
                                          (_cachedState.SteamAuth.RefreshToken != null ||
                                           _cachedState.SteamAuth.Mode != "anonymous" ||
                                           !string.IsNullOrEmpty(_cachedState.SteamAuth.Username));

                        if (hadSteamAuth)
                        {
                            _logger.LogInformation("Clearing Steam auth from state.json after migration to separate file");
                        }

                        _cachedState.SteamAuth = null;
                        SaveState(_cachedState);
                    }

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

    /// <summary>
    /// Gets the legacy single log position (for backward compatibility).
    /// Use GetLogPosition(datasourceName) for multi-datasource support.
    /// </summary>
    public long GetLogPosition()
    {
        return GetState().LogProcessing.Position;
    }

    /// <summary>
    /// Sets the legacy single log position (for backward compatibility).
    /// Use SetLogPosition(datasourceName, position) for multi-datasource support.
    /// </summary>
    public void SetLogPosition(long position)
    {
        UpdateState(state =>
        {
            state.LogProcessing.Position = position;
            state.LogProcessing.LastUpdated = DateTime.UtcNow;
        });
    }

    /// <summary>
    /// Gets the log position for a specific datasource.
    /// Falls back to legacy Position if datasource is "default" and no specific position exists.
    /// </summary>
    public long GetLogPosition(string datasourceName)
    {
        var state = GetState();

        // Try to get datasource-specific position
        if (state.LogProcessing.DatasourcePositions.TryGetValue(datasourceName, out var position))
        {
            return position;
        }

        // Fall back to legacy position for "default" datasource
        if (datasourceName == "default")
        {
            return state.LogProcessing.Position;
        }

        return 0;
    }

    /// <summary>
    /// Sets the log position for a specific datasource.
    /// </summary>
    public void SetLogPosition(string datasourceName, long position)
    {
        UpdateState(state =>
        {
            state.LogProcessing.DatasourcePositions[datasourceName] = position;
            state.LogProcessing.LastUpdated = DateTime.UtcNow;

            // Also update legacy Position if this is the "default" datasource
            if (datasourceName == "default")
            {
                state.LogProcessing.Position = position;
            }
        });
    }

    /// <summary>
    /// Gets all datasource log positions.
    /// </summary>
    public Dictionary<string, long> GetAllLogPositions()
    {
        var state = GetState();
        var positions = new Dictionary<string, long>(state.LogProcessing.DatasourcePositions);

        // Include legacy position as "default" if not already in dictionary
        if (!positions.ContainsKey("default") && state.LogProcessing.Position > 0)
        {
            positions["default"] = state.LogProcessing.Position;
        }

        return positions;
    }

    /// <summary>
    /// Gets the total line count for a specific datasource.
    /// Returns 0 if not set (caller should count files as fallback).
    /// </summary>
    public long GetLogTotalLines(string datasourceName)
    {
        var state = GetState();

        if (state.LogProcessing.DatasourceTotalLines.TryGetValue(datasourceName, out var totalLines))
        {
            return totalLines;
        }

        return 0;
    }

    /// <summary>
    /// Sets the total line count for a specific datasource.
    /// Called by Rust processor after counting all log files.
    /// </summary>
    public void SetLogTotalLines(string datasourceName, long totalLines)
    {
        UpdateState(state =>
        {
            state.LogProcessing.DatasourceTotalLines[datasourceName] = totalLines;
            state.LogProcessing.LastUpdated = DateTime.UtcNow;
        });
    }

    /// <summary>
    /// Gets all datasource total line counts.
    /// </summary>
    public Dictionary<string, long> GetAllLogTotalLines()
    {
        var state = GetState();
        return new Dictionary<string, long>(state.LogProcessing.DatasourceTotalLines);
    }

    // Cache Clear Operations Methods - now use separate file (data/operations/cache_operations.json)
    public List<CacheClearOperation> GetCacheClearOperations()
    {
        lock (_cacheClearLock)
        {
            if (_cachedCacheClearOperations != null)
            {
                return _cachedCacheClearOperations;
            }

            try
            {
                if (File.Exists(_cacheOperationsFilePath))
                {
                    var json = File.ReadAllText(_cacheOperationsFilePath);
                    _cachedCacheClearOperations = JsonSerializer.Deserialize<List<CacheClearOperation>>(json) ?? new List<CacheClearOperation>();
                }
                else
                {
                    _cachedCacheClearOperations = new List<CacheClearOperation>();
                }

                // Clean up old completed operations (older than 24 hours)
                CleanupOldCacheClearOperations();

                return _cachedCacheClearOperations;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load cache clear operations from {Path}", _cacheOperationsFilePath);
                _cachedCacheClearOperations = new List<CacheClearOperation>();
                return _cachedCacheClearOperations;
            }
        }
    }

    public void RemoveCacheClearOperation(string id)
    {
        lock (_cacheClearLock)
        {
            var operations = GetCacheClearOperations();
            var removed = operations.RemoveAll(o => o.Id == id);
            if (removed > 0)
            {
                SaveCacheClearOperations();
            }
        }
    }

    /// <summary>
    /// Updates the cache clear operations list with the provided updater action
    /// </summary>
    public void UpdateCacheClearOperations(Action<List<CacheClearOperation>> updater)
    {
        lock (_cacheClearLock)
        {
            var operations = GetCacheClearOperations();
            updater(operations);
            SaveCacheClearOperations();
        }
    }

    private void SaveCacheClearOperations()
    {
        try
        {
            if (_cachedCacheClearOperations == null)
            {
                return;
            }

            var json = JsonSerializer.Serialize(_cachedCacheClearOperations, new JsonSerializerOptions { WriteIndented = true });
            var tempFile = _cacheOperationsFilePath + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, _cacheOperationsFilePath, true);
            _logger.LogTrace("Cache clear operations saved to {Path}", _cacheOperationsFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save cache clear operations to {Path}", _cacheOperationsFilePath);
        }
    }

    /// <summary>
    /// Cleans up old completed cache clear operations (older than 24 hours)
    /// </summary>
    private void CleanupOldCacheClearOperations()
    {
        if (_cachedCacheClearOperations == null || _cachedCacheClearOperations.Count == 0)
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddHours(-24);
        var oldOps = _cachedCacheClearOperations
            .Where(o => (o.Status == "completed" || o.Status == "failed") && o.EndTime.HasValue && o.EndTime.Value < cutoff)
            .ToList();

        if (oldOps.Count > 0)
        {
            foreach (var op in oldOps)
            {
                _cachedCacheClearOperations.Remove(op);
            }
            SaveCacheClearOperations();
            _logger.LogInformation("Cleaned up {Count} old cache clear operations", oldOps.Count);
        }
    }

    // Operation States Methods - now use separate file (data/operations/operation_history.json)
    public List<OperationState> GetOperationStates()
    {
        lock (_operationLock)
        {
            if (_cachedOperationStates != null)
            {
                return _cachedOperationStates;
            }

            try
            {
                if (File.Exists(_operationHistoryFilePath))
                {
                    var json = File.ReadAllText(_operationHistoryFilePath);
                    _cachedOperationStates = JsonSerializer.Deserialize<List<OperationState>>(json) ?? new List<OperationState>();
                }
                else
                {
                    _cachedOperationStates = new List<OperationState>();
                }

                // Clean up old completed operations (older than 48 hours)
                CleanupOldOperationStates();

                return _cachedOperationStates;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load operation states from {Path}", _operationHistoryFilePath);
                _cachedOperationStates = new List<OperationState>();
                return _cachedOperationStates;
            }
        }
    }

    public void RemoveOperationState(string id)
    {
        lock (_operationLock)
        {
            var states = GetOperationStates();
            var removed = states.RemoveAll(o => o.Id == id);
            if (removed > 0)
            {
                SaveOperationStates();
            }
        }
    }

    /// <summary>
    /// Updates the operation states list with the provided updater action
    /// </summary>
    public void UpdateOperationStates(Action<List<OperationState>> updater)
    {
        lock (_operationLock)
        {
            var states = GetOperationStates();
            updater(states);
            SaveOperationStates();
        }
    }

    private void SaveOperationStates()
    {
        try
        {
            if (_cachedOperationStates == null)
            {
                return;
            }

            var json = JsonSerializer.Serialize(_cachedOperationStates, new JsonSerializerOptions { WriteIndented = true });
            var tempFile = _operationHistoryFilePath + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, _operationHistoryFilePath, true);
            _logger.LogTrace("Operation states saved to {Path}", _operationHistoryFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save operation states to {Path}", _operationHistoryFilePath);
        }
    }

    /// <summary>
    /// Cleans up old completed operation states (older than 48 hours)
    /// </summary>
    private void CleanupOldOperationStates()
    {
        if (_cachedOperationStates == null || _cachedOperationStates.Count == 0)
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddHours(-48);
        var oldStates = _cachedOperationStates
            .Where(s => s.Status == "complete" && s.UpdatedAt < cutoff)
            .ToList();

        if (oldStates.Count > 0)
        {
            foreach (var state in oldStates)
            {
                _cachedOperationStates.Remove(state);
            }
            SaveOperationStates();
            _logger.LogInformation("Cleaned up {Count} old completed operation states", oldStates.Count);
        }
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
        });
    }

    // Has Processed Logs Methods
    public bool GetHasProcessedLogs()
    {
        return GetState().HasProcessedLogs;
    }

    public void SetHasProcessedLogs(bool processed)
    {
        UpdateState(state => state.HasProcessedLogs = processed);
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
    public object GetCrawlIncrementalMode()
    {
        return GetState().CrawlIncrementalMode;
    }

    public void SetCrawlIncrementalMode(object mode)
    {
        UpdateState(state => state.CrawlIncrementalMode = mode);
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

            // Note: operation_states.json migration removed - states are temporary and don't need migration

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
            // CacheClearOperations loaded from separate file via GetCacheClearOperations()
            // OperationStates loaded from separate file via GetOperationStates()
            SetupCompleted = persisted.SetupCompleted,
            LastPicsCrawl = persisted.LastPicsCrawl,
            CrawlIntervalHours = persisted.CrawlIntervalHours,
            CrawlIncrementalMode = persisted.CrawlIncrementalMode,
            LastUpdated = persisted.LastUpdated,
            HasDataLoaded = persisted.HasDataLoaded,
            HasProcessedLogs = persisted.HasProcessedLogs,
            GuestSessionDurationHours = persisted.GuestSessionDurationHours,
            SelectedTheme = persisted.SelectedTheme ?? "dark-default",
            DefaultGuestTheme = persisted.DefaultGuestTheme ?? "dark-default",
            RefreshRate = persisted.RefreshRate ?? "STANDARD",
            DefaultGuestRefreshRate = persisted.DefaultGuestRefreshRate ?? "STANDARD",
            // Default guest preferences
            DefaultGuestUseLocalTimezone = persisted.DefaultGuestUseLocalTimezone,
            DefaultGuestUse24HourFormat = persisted.DefaultGuestUse24HourFormat,
            DefaultGuestSharpCorners = persisted.DefaultGuestSharpCorners,
            DefaultGuestDisableTooltips = persisted.DefaultGuestDisableTooltips,
            DefaultGuestShowDatasourceLabels = persisted.DefaultGuestShowDatasourceLabels,
            DefaultGuestShowYearInDates = persisted.DefaultGuestShowYearInDates,
            AllowedTimeFormats = persisted.AllowedTimeFormats ?? new List<string> { "server-24h", "server-12h", "local-24h", "local-12h" },
            // Guest prefill permissions
            GuestPrefillEnabledByDefault = persisted.GuestPrefillEnabledByDefault,
            GuestPrefillDurationHours = persisted.GuestPrefillDurationHours,
            // PICS viability check caching
            RequiresFullScan = persisted.RequiresFullScan,
            LastViabilityCheck = persisted.LastViabilityCheck,
            LastViabilityCheckChangeNumber = persisted.LastViabilityCheckChangeNumber,
            ViabilityChangeGap = persisted.ViabilityChangeGap,
            // Steam session replacement tracking
            SessionReplacedCount = persisted.SessionReplacedCount,
            LastSessionReplacement = persisted.LastSessionReplacement,
            // Metrics authentication toggle
            RequireAuthForMetrics = persisted.RequireAuthForMetrics,
            // Client IPs excluded from stats
            ExcludedClientIps = persisted.ExcludedClientIps ?? new List<string>(),
            ExcludedClientRules = ResolveExcludedClientRules(persisted),
            // LEGACY: Only load SteamAuth if present (for migration from old state.json)
            SteamAuth = persisted.SteamAuth != null ? new SteamAuthState
            {
                Mode = persisted.SteamAuth.Mode,
                Username = persisted.SteamAuth.Username,
                // Decrypt sensitive fields (GuardData not used in modern auth)
                RefreshToken = _encryption.Decrypt(persisted.SteamAuth.RefreshToken),
                LastAuthenticated = persisted.SteamAuth.LastAuthenticated
            } : null
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
            // CacheClearOperations saved to separate file via SaveCacheClearOperations()
            // OperationStates saved to separate file via SaveOperationStates()
            SetupCompleted = state.SetupCompleted,
            LastPicsCrawl = state.LastPicsCrawl,
            CrawlIntervalHours = state.CrawlIntervalHours,
            CrawlIncrementalMode = state.CrawlIncrementalMode,
            LastUpdated = state.LastUpdated,
            HasDataLoaded = state.HasDataLoaded,
            HasProcessedLogs = state.HasProcessedLogs,
            GuestSessionDurationHours = state.GuestSessionDurationHours,
            SelectedTheme = state.SelectedTheme,
            DefaultGuestTheme = state.DefaultGuestTheme,
            RefreshRate = state.RefreshRate ?? "STANDARD",
            DefaultGuestRefreshRate = state.DefaultGuestRefreshRate ?? "STANDARD",
            // Default guest preferences
            DefaultGuestUseLocalTimezone = state.DefaultGuestUseLocalTimezone,
            DefaultGuestUse24HourFormat = state.DefaultGuestUse24HourFormat,
            DefaultGuestSharpCorners = state.DefaultGuestSharpCorners,
            DefaultGuestDisableTooltips = state.DefaultGuestDisableTooltips,
            DefaultGuestShowDatasourceLabels = state.DefaultGuestShowDatasourceLabels,
            DefaultGuestShowYearInDates = state.DefaultGuestShowYearInDates,
            AllowedTimeFormats = state.AllowedTimeFormats,
            // Guest prefill permissions
            GuestPrefillEnabledByDefault = state.GuestPrefillEnabledByDefault,
            GuestPrefillDurationHours = state.GuestPrefillDurationHours,
            // PICS viability check caching
            RequiresFullScan = state.RequiresFullScan,
            LastViabilityCheck = state.LastViabilityCheck,
            LastViabilityCheckChangeNumber = state.LastViabilityCheckChangeNumber,
            ViabilityChangeGap = state.ViabilityChangeGap,
            // Steam session replacement tracking
            SessionReplacedCount = state.SessionReplacedCount,
            LastSessionReplacement = state.LastSessionReplacement,
            // Metrics authentication toggle
            RequireAuthForMetrics = state.RequireAuthForMetrics,
            // Client IPs excluded from stats (legacy)
            ExcludedClientIps = BuildExcludedIpList(state.ExcludedClientRules, state.ExcludedClientIps),
            ExcludedClientRules = state.ExcludedClientRules ?? new List<ClientExclusionRule>(),
            // LEGACY: Only persist SteamAuth if not null (will be null after migration)
            // JsonIgnore(WhenWritingNull) on property will exclude from JSON when null
            SteamAuth = state.SteamAuth != null ? new SteamAuthState
            {
                Mode = state.SteamAuth.Mode,
                Username = state.SteamAuth.Username,
                // Encrypt sensitive fields (GuardData not used in modern auth)
                RefreshToken = _encryption.Encrypt(state.SteamAuth.RefreshToken),
                LastAuthenticated = state.SteamAuth.LastAuthenticated
            } : null
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

    // Steam Authentication Methods - now delegate to SteamAuthStorageService
    public string? GetSteamAuthMode()
    {
        return _steamAuthStorage.GetSteamAuthData().Mode;
    }

    public void SetSteamAuthMode(string mode)
    {
        _steamAuthStorage.UpdateSteamAuthData(data => data.Mode = mode);
    }

    public string? GetSteamUsername()
    {
        return _steamAuthStorage.GetSteamAuthData().Username;
    }

    public void SetSteamUsername(string? username)
    {
        _steamAuthStorage.UpdateSteamAuthData(data => data.Username = username);
    }

    public string? GetSteamRefreshToken()
    {
        return _steamAuthStorage.GetSteamAuthData().RefreshToken;
    }

    public void SetSteamRefreshToken(string? token)
    {
        _steamAuthStorage.UpdateSteamAuthData(data =>
        {
            data.RefreshToken = token;
            if (token != null)
            {
                data.LastAuthenticated = DateTime.UtcNow;
            }
        });
    }

    public bool HasSteamRefreshToken()
    {
        return !string.IsNullOrEmpty(_steamAuthStorage.GetSteamAuthData().RefreshToken);
    }

    // NOTE: GuardData methods removed - modern Steam auth uses refresh tokens only
    // GetSteamGuardData() and SetSteamGuardData() are no longer needed

    // Guest Session Duration Methods
    public int GetGuestSessionDurationHours()
    {
        return GetState().GuestSessionDurationHours;
    }

    public void SetGuestSessionDurationHours(int hours)
    {
        UpdateState(state => state.GuestSessionDurationHours = hours);
    }

    // Guest Mode Lock Methods
    public bool GetGuestModeLocked()
    {
        return GetState().GuestModeLocked;
    }

    public void SetGuestModeLocked(bool locked)
    {
        UpdateState(state => state.GuestModeLocked = locked);
    }

    // Theme Preference Methods
    public string? GetSelectedTheme()
    {
        return GetState().SelectedTheme ?? "dark-default";
    }

    public void SetSelectedTheme(string? themeId)
    {
        UpdateState(state => state.SelectedTheme = themeId ?? "dark-default");
    }

    // Default Guest Theme Methods
    public string? GetDefaultGuestTheme()
    {
        return GetState().DefaultGuestTheme ?? "dark-default";
    }

    public void SetDefaultGuestTheme(string? themeId)
    {
        UpdateState(state => state.DefaultGuestTheme = themeId ?? "dark-default");
    }

    // Refresh Rate Methods
    public string GetRefreshRate()
    {
        return GetState().RefreshRate ?? "STANDARD";
    }

    public void SetRefreshRate(string rate)
    {
        // Validate the rate is a valid option
        var validRates = new[] { "LIVE", "ULTRA", "REALTIME", "STANDARD", "RELAXED", "SLOW" };
        if (!validRates.Contains(rate.ToUpperInvariant()))
        {
            rate = "STANDARD";
        }
        UpdateState(state => state.RefreshRate = rate.ToUpperInvariant());
    }

    // Default Guest Refresh Rate Methods
    public string GetDefaultGuestRefreshRate()
    {
        return GetState().DefaultGuestRefreshRate ?? "STANDARD";
    }

    public void SetDefaultGuestRefreshRate(string rate)
    {
        // Validate the rate is a valid option
        var validRates = new[] { "LIVE", "ULTRA", "REALTIME", "STANDARD", "RELAXED", "SLOW" };
        if (!validRates.Contains(rate.ToUpperInvariant()))
        {
            rate = "STANDARD";
        }
        UpdateState(state => state.DefaultGuestRefreshRate = rate.ToUpperInvariant());
    }

    // Steam Session Replacement Tracking Methods
    public int GetSessionReplacedCount()
    {
        var state = GetState();
        // Reset counter if last replacement was more than 24 hours ago
        if (state.LastSessionReplacement.HasValue &&
            DateTime.UtcNow - state.LastSessionReplacement.Value > TimeSpan.FromHours(24))
        {
            ResetSessionReplacedCount();
            return 0;
        }
        return state.SessionReplacedCount;
    }

    public void SetSessionReplacedCount(int count)
    {
        UpdateState(state => state.SessionReplacedCount = count);
    }

    public DateTime? GetLastSessionReplacement()
    {
        return GetState().LastSessionReplacement;
    }

    public void SetLastSessionReplacement(DateTime? timestamp)
    {
        UpdateState(state => state.LastSessionReplacement = timestamp);
    }

    public void IncrementSessionReplacedCount()
    {
        UpdateState(state =>
        {
            // Reset counter if last replacement was more than 24 hours ago
            if (state.LastSessionReplacement.HasValue &&
                DateTime.UtcNow - state.LastSessionReplacement.Value > TimeSpan.FromHours(24))
            {
                state.SessionReplacedCount = 0;
            }
            state.SessionReplacedCount++;
            state.LastSessionReplacement = DateTime.UtcNow;
        });
    }

    public void ResetSessionReplacedCount()
    {
        UpdateState(state =>
        {
            state.SessionReplacedCount = 0;
            state.LastSessionReplacement = null;
        });
    }

    // Metrics Authentication Toggle Methods
    public bool? GetRequireAuthForMetrics()
    {
        return GetState().RequireAuthForMetrics;
    }

    public void SetRequireAuthForMetrics(bool? value)
    {
        UpdateState(state => state.RequireAuthForMetrics = value);
    }

    // Stats Exclusion Methods
    /// <summary>
    /// Gets IPs that should be excluded from statistics calculations (both hide and exclude modes).
    /// Note: For query filtering, use GetHiddenClientIps() instead to only filter hide mode.
    /// </summary>
    public List<string> GetExcludedClientIps()
    {
        var state = GetState();
        var rules = ResolveExcludedClientRules(state);
        return rules
            .Where(rule => IsStatsExcludedMode(rule.Mode))
            .Select(rule => rule.Ip)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Gets IPs that should be excluded from calculations but NOT hidden (exclude mode only).
    /// These IPs should be included in queries but excluded from SUM/COUNT aggregations.
    /// </summary>
    public List<string> GetStatsExcludedOnlyClientIps()
    {
        return new List<string>();
    }

    public void SetExcludedClientIps(List<string> ips)
    {
        UpdateState(state =>
        {
            var normalizedIps = ips ?? new List<string>();
            state.ExcludedClientIps = normalizedIps;
            state.ExcludedClientRules = normalizedIps
                .Select(ip => new ClientExclusionRule { Ip = ip, Mode = ClientExclusionModes.Hide })
                .ToList();
        });
    }

    public List<ClientExclusionRule> GetExcludedClientRules()
    {
        var state = GetState();
        return ResolveExcludedClientRules(state)
            .Select(rule => new ClientExclusionRule { Ip = rule.Ip, Mode = NormalizeMode(rule.Mode) })
            .ToList();
    }

    public void SetExcludedClientRules(List<ClientExclusionRule> rules)
    {
        UpdateState(state =>
        {
            var normalized = rules.Count == 0
                ? new List<ClientExclusionRule>()
                : NormalizeRules(rules, state.ExcludedClientIps);
            state.ExcludedClientRules = normalized;
            state.ExcludedClientIps = normalized.Select(rule => rule.Ip).ToList();
        });
    }

    public List<string> GetHiddenClientIps()
    {
        return GetExcludedClientIps();
    }

    private static List<ClientExclusionRule> ResolveExcludedClientRules(PersistedState persisted)
    {
        if (persisted.ExcludedClientRules != null && persisted.ExcludedClientRules.Count > 0)
        {
            return NormalizeRules(persisted.ExcludedClientRules, persisted.ExcludedClientIps);
        }

        return NormalizeRules(null, persisted.ExcludedClientIps);
    }

    private static List<ClientExclusionRule> ResolveExcludedClientRules(AppState state)
    {
        if (state.ExcludedClientRules != null && state.ExcludedClientRules.Count > 0)
        {
            return NormalizeRules(state.ExcludedClientRules, state.ExcludedClientIps);
        }

        return NormalizeRules(null, state.ExcludedClientIps);
    }

    private static List<string> BuildExcludedIpList(List<ClientExclusionRule>? rules, List<string>? fallback)
    {
        if (rules != null && rules.Count > 0)
        {
            return rules.Select(rule => rule.Ip).Distinct().ToList();
        }

        return fallback ?? new List<string>();
    }

    private static List<ClientExclusionRule> NormalizeRules(List<ClientExclusionRule>? rules, List<string>? fallbackIps)
    {
        var normalized = new List<ClientExclusionRule>();
        if (rules != null && rules.Count > 0)
        {
            foreach (var rule in rules)
            {
                var ip = rule.Ip?.Trim();
                if (string.IsNullOrWhiteSpace(ip))
                {
                    continue;
                }

                var mode = NormalizeMode(rule.Mode);
                if (normalized.Any(r => r.Ip == ip))
                {
                    continue;
                }

                normalized.Add(new ClientExclusionRule { Ip = ip, Mode = mode });
            }

            return normalized;
        }

        if (fallbackIps != null && fallbackIps.Count > 0)
        {
            foreach (var ip in fallbackIps)
            {
                var trimmed = ip?.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                if (normalized.Any(r => r.Ip == trimmed))
                {
                    continue;
                }

                normalized.Add(new ClientExclusionRule { Ip = trimmed, Mode = ClientExclusionModes.Hide });
            }
        }

        return normalized;
    }

    private static bool IsHiddenMode(string? mode)
    {
        return NormalizeMode(mode) == ClientExclusionModes.Hide;
    }

    private static bool IsStatsExcludedMode(string? mode)
    {
        var normalized = NormalizeMode(mode);
        return normalized == ClientExclusionModes.Hide || normalized == ClientExclusionModes.Exclude;
    }

    private static string NormalizeMode(string? mode)
    {
        return ClientExclusionModes.Hide;
    }

    // Guest Prefill Permission Methods
    public bool GetGuestPrefillEnabledByDefault()
    {
        return GetState().GuestPrefillEnabledByDefault;
    }

    public void SetGuestPrefillEnabledByDefault(bool enabled)
    {
        UpdateState(state => state.GuestPrefillEnabledByDefault = enabled);
    }

    public int GetGuestPrefillDurationHours()
    {
        return GetState().GuestPrefillDurationHours;
    }

    public void SetGuestPrefillDurationHours(int hours)
    {
        // Validate hours (1 or 2)
        if (hours != 1 && hours != 2)
        {
            hours = 2; // Default to 2 hours
        }
        UpdateState(state => state.GuestPrefillDurationHours = hours);
    }
}
