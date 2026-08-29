using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Services.Scheduling;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service for managing consolidated operational state in state.json
/// Replaces individual files: position.txt, cache_clear_status.json, operation_states.json
/// </summary>
public class StateService : IStateService
{
    private readonly ILogger<StateService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly SecureStateEncryptionService _encryption;
    private readonly SteamAuthStorageService _steamAuthStorage;
    private readonly string _stateFilePath;
    private readonly string _operationHistoryFilePath;
    private readonly string _cacheOperationsFilePath;
    private readonly object _lock = new object();
    private readonly object _operationLock = new object();
    private readonly object _cacheClearLock = new object();
    private readonly object _signalLock = new object();
    private TaskCompletionSource<bool> _setupCompletedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _logsProcessedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AppState? _cachedState;
    private List<OperationState>? _cachedOperationStates;
    private List<CacheClearOperation>? _cachedCacheClearOperations;
    private int _consecutiveFailures = 0;
    private bool _stateSavesDisabledLogged = false;
    private bool _migrationAttempted = false;

    // Retention windows for the two persisted operation lists. Both prune on the same
    // condition - the record reached a terminal state and has aged past its window - but the
    // windows and the timestamp they measure differ on purpose. Cache clear rows carry an
    // explicit EndTime and only need to outlive a restart, while operation history rows have
    // no end timestamp (UpdatedAt stands in) and back the longer-lived history view.
    private static readonly TimeSpan _cacheClearOperationRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan _operationStateRetention = TimeSpan.FromHours(48);

    public bool IsPersistenceAvailable => _consecutiveFailures <= 5;

    public StateService(
        ILogger<StateService> logger,
        IPathResolver pathResolver,
        SecureStateEncryptionService encryption,
        SteamAuthStorageService steamAuthStorage)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _encryption = encryption;
        _steamAuthStorage = steamAuthStorage;
        _stateFilePath = Path.Combine(_pathResolver.GetStateDirectory(), "state.json");
        _operationHistoryFilePath = Path.Combine(_pathResolver.GetOperationsDirectory(), "operation_history.json");
        _cacheOperationsFilePath = Path.Combine(_pathResolver.GetOperationsDirectory(), "cache_operations.json");

        var stateDir = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrEmpty(stateDir) && !Directory.Exists(stateDir))
        {
            Directory.CreateDirectory(stateDir);
        }
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

            // Whether we may persist the normalized/seeded scheduled-prefill result below. Left true on the
            // normal load / legacy-migration paths; the outer corrupt-load fallback flips it false so a
            // transient read error never overwrites a possibly-recoverable state.json.
            var canPersistScheduledPrefillInit = true;

            // True when a corrupt SECTION was recovered per-section below. A recovery load must not rewrite
            // state.json (neither the one-time Steam-auth migration save nor the anchor-seed save) so the
            // original file survives untouched for manual repair until an explicit later mutation saves it.
            var loadedWithSectionRecovery = false;

            try
            {
                if (File.Exists(_stateFilePath))
                {
                    var json = File.ReadAllText(_stateFilePath);
                    AppState persisted;
                    try
                    {
                        persisted = JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
                    }
                    catch (JsonException ex)
                    {
                        // A single corrupt section (e.g. an out-of-range enum rejected by a strict converter)
                        // must not discard every OTHER persisted setting. Re-parse and bind each top-level
                        // section independently so only the offending block degrades; a genuinely unparseable
                        // file re-throws from the helper to the outer fallback (today's whole-state default).
                        persisted = DeserializePersistedStateWithSectionIsolation(json, ex);
                        loadedWithSectionRecovery = true;
                    }

                    // Repair legacy/null blocks and decrypt sensitive fields on the loaded state
                    _cachedState = NormalizeLoadedState(persisted);
                    CleanupStaleOperations(_cachedState);

                    // Migrate Steam auth data to separate file (one-time migration)
                    if (!_migrationAttempted)
                    {
                        _steamAuthStorage.MigrateFromStateJson(_cachedState.SteamAuth);
                        _migrationAttempted = true;

                        // Always clear Steam auth from main state after migration attempt
                        // (Steam auth now lives in separate file: data/security/steam_auth/credentials.json)
                        // Setting to null will exclude it from JSON serialization
                        var hadSteamAuth = _cachedState.SteamAuth != null &&
                                          (_cachedState.SteamAuth.RefreshToken != null ||
                                           _cachedState.SteamAuth.Mode != SteamAuthMode.Anonymous ||
                                           !string.IsNullOrEmpty(_cachedState.SteamAuth.Username));

                        if (hadSteamAuth)
                        {
                            _logger.LogInformation("Clearing Steam auth from state.json after migration to separate file");
                        }

                        _cachedState.SteamAuth = null;

                        // Don't rewrite the file on a section-recovery load: persisting here would overwrite
                        // the still-hand-repairable original with the recovered-and-defaulted state.
                        if (!loadedWithSectionRecovery)
                        {
                            SaveState(_cachedState);
                        }
                    }

                }
                else
                {
                    _cachedState = MigrateFromLegacyFiles();
                    SaveState(_cachedState);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load state, using default");
                _cachedState = new AppState();
                canPersistScheduledPrefillInit = false;
            }

            // Fix 2: seed first-run anchors for enabled positive-interval services that have no last-run key
            // yet, so the default config, a v1->v2 migration, and a fresh install wait one full interval
            // instead of instant-running on the next poll. Seeds only MISSING keys, so a normal restart
            // (keys already persisted and reloaded) is a no-op and the schedule never shifts. (AppState
            // defaults ScheduledPrefill to a non-null CreateDefault(), so no null-normalization is needed.)
            // The in-memory state is always seeded; persist only when it seeded, the load succeeded, and no
            // section recovery ran, so a corrupt-load fallback still avoids an instant run without clobbering
            // a recoverable state.json.
            var loadedState = _cachedState!;
            var anchorsSeeded = SeedInitialFirstRunAnchors(loadedState, DateTime.UtcNow);
            var notificationModeMigrated = MigrateEvictionNotificationsToServiceMode(loadedState);
            var utcTimeFormatMigrated = MigrateAllowedTimeFormatsForUtc(loadedState);
            if ((anchorsSeeded || notificationModeMigrated || utcTimeFormatMigrated) && canPersistScheduledPrefillInit && !loadedWithSectionRecovery)
            {
                SaveState(loadedState);
            }

            return loadedState;
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
            if (!_stateSavesDisabledLogged)
            {
                _logger.LogError("State persistence is disabled after repeated save failures");
                _stateSavesDisabledLogged = true;
            }
            return;
        }

        lock (_lock)
        {
            try
            {
                state.LastUpdated = DateTime.UtcNow;

                // Serialize a copy whose sensitive/derived fields are in their wire form
                var persisted = PrepareWireState(state);

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
                _stateSavesDisabledLogged = false;
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
    /// Gets the per-source-stem series offsets for a datasource (a copy). Empty when no
    /// per-source checkpoint has ever been persisted (pre-upgrade state or fresh install).
    /// </summary>
    public Dictionary<string, long> GetLogSourcePositions(string datasourceName)
    {
        // Copy under the state lock: ClearLogSourcePositions mutates the inner map in
        // place under the same lock, and enumerating it unlocked can throw mid-copy.
        lock (_lock)
        {
            var state = GetState();
            return state.LogProcessing.DatasourceSourcePositions.TryGetValue(datasourceName, out var positions)
                ? new Dictionary<string, long>(positions)
                : new Dictionary<string, long>();
        }
    }

    /// <summary>
    /// Atomically replaces the per-source-stem series offsets for a datasource and keeps
    /// the legacy aggregate position in sync (aggregate = sum of stems, presentation only).
    /// </summary>
    public void SetLogSourcePositions(string datasourceName, Dictionary<string, long> positions)
    {
        UpdateState(state =>
        {
            state.LogProcessing.DatasourceSourcePositions[datasourceName] =
                new Dictionary<string, long>(positions);
            state.LogProcessing.DatasourcePositions[datasourceName] = positions.Values.Sum();
            state.LogProcessing.LastUpdated = DateTime.UtcNow;
            if (datasourceName == "default")
            {
                state.LogProcessing.Position = positions.Values.Sum();
            }
        });
    }

    /// <summary>
    /// Subtracts purge-removed line counts (per source stem) from a datasource's saved log
    /// positions and total-line count. A log purge rewrites the access log in place, so every
    /// line index past the removed lines shifts down by the removed count; left unadjusted,
    /// the position points that many lines into never-ingested content and the next
    /// incremental run silently skips it - the skipped lines' cache files then surface as
    /// permanently unmapped. Clamped at zero: over-subtraction merely replays lines, which
    /// ingestion dedupe absorbs, while under-reading loses data forever.
    /// </summary>
    public void ReduceLogPositionsAfterPurge(
        string datasourceName,
        IReadOnlyDictionary<string, long> linesRemovedBeforePositionByStem,
        IReadOnlyDictionary<string, long> linesRemovedByStem)
    {
        // The position comes back only by lines removed BELOW it (lines it had read); the
        // on-disk total-line count comes down by ALL removed lines. Lines purged ahead of
        // the position were never read, so moving the position for them would replay
        // already-ingested lines (harmless via dedupe, but imprecise).
        long positionReduction = 0;
        foreach (var removed in linesRemovedBeforePositionByStem.Values)
        {
            if (removed > 0)
            {
                positionReduction += removed;
            }
        }

        long totalReduction = 0;
        foreach (var removed in linesRemovedByStem.Values)
        {
            if (removed > 0)
            {
                totalReduction += removed;
            }
        }

        if (positionReduction == 0 && totalReduction == 0)
        {
            return;
        }

        // One UpdateState so a concurrent reducer (a cache removal and the eviction purge
        // run under different locks) subtracts from the value the other one wrote instead
        // of overwriting it - the read and the write must sit under the same lock hold.
        UpdateState(state =>
        {
            var logProcessing = state.LogProcessing;

            if (logProcessing.DatasourceSourcePositions.TryGetValue(datasourceName, out var positions))
            {
                foreach (var (stem, removed) in linesRemovedBeforePositionByStem)
                {
                    if (removed <= 0)
                    {
                        continue;
                    }

                    if (positions.TryGetValue(stem, out var current))
                    {
                        positions[stem] = Math.Max(0, current - removed);
                    }
                }

                logProcessing.DatasourcePositions[datasourceName] = positions.Values.Sum();
                if (datasourceName == "default")
                {
                    logProcessing.Position = positions.Values.Sum();
                }
            }

            if (totalReduction > 0 &&
                logProcessing.DatasourceTotalLines.TryGetValue(datasourceName, out var totalLines))
            {
                logProcessing.DatasourceTotalLines[datasourceName] = Math.Max(0, totalLines - totalReduction);
            }

            logProcessing.LastUpdated = DateTime.UtcNow;
        });

        _logger.LogInformation(
            "Reduced log positions for datasource '{Datasource}' by {PositionReduction} already-read purged line(s) ({TotalReduction} removed in total)",
            datasourceName, positionReduction, totalReduction);
    }

    /// <summary>
    /// Snapshots a datasource's per-stem positions into a temp JSON file for a Rust log
    /// purge's <c>--stem-positions</c> argument, so the purge can split removed lines at
    /// each stem's read position. Null when no positions exist (the purge then treats every
    /// removed line as already read, the replay-safe fallback). Caller deletes the file.
    /// </summary>
    public async Task<string?> WriteStemPositionsTempFileAsync(string datasourceName)
    {
        var positions = GetLogSourcePositions(datasourceName);
        if (positions.Count == 0)
        {
            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), $"stem-positions-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(positions));
        return path;
    }

    /// <summary>
    /// Clears the checkpoints for specific stems of a datasource (after a per-service
    /// log removal deletes those file series). Missing stems are ignored.
    /// </summary>
    public void ClearLogSourcePositions(string datasourceName, IEnumerable<string> stems)
    {
        UpdateState(state =>
        {
            if (!state.LogProcessing.DatasourceSourcePositions.TryGetValue(datasourceName, out var positions))
            {
                return;
            }
            foreach (var stem in stems)
            {
                positions.Remove(stem);
            }
            state.LogProcessing.DatasourcePositions[datasourceName] = positions.Values.Sum();
            state.LogProcessing.LastUpdated = DateTime.UtcNow;
            if (datasourceName == "default")
            {
                state.LogProcessing.Position = positions.Values.Sum();
            }
        });
    }

    /// <summary>
    /// Gets the persisted ingestion diagnostics for a datasource, or null.
    /// </summary>
    public LogIngestDiagnostics? GetLogIngestDiagnostics(string datasourceName)
    {
        // Returned as a copy taken under the state lock: the stored object is mutated in
        // place by SetLogMissingSourcesWarning, and callers must never see a torn read.
        lock (_lock)
        {
            var state = GetState();
            if (!state.LogProcessing.DatasourceDiagnostics.TryGetValue(datasourceName, out var diagnostics))
            {
                return null;
            }
            return new LogIngestDiagnostics
            {
                Layout = diagnostics.Layout,
                TerminalStatus = diagnostics.TerminalStatus,
                UnparsedLines = diagnostics.UnparsedLines,
                HintlessHttpDetailedLines = diagnostics.HintlessHttpDetailedLines,
                SkippedFallbackLines = diagnostics.SkippedFallbackLines,
                InvalidEncodingLines = diagnostics.InvalidEncodingLines,
                RecognizedIgnoredLines = diagnostics.RecognizedIgnoredLines,
                IncompleteFinalRecords = diagnostics.IncompleteFinalRecords,
                FilesWithErrors = new List<string>(diagnostics.FilesWithErrors),
                MissingSourcesMessage = diagnostics.MissingSourcesMessage,
                LastRunUtc = diagnostics.LastRunUtc
            };
        }
    }

    /// <summary>
    /// Persists ingestion diagnostics for a datasource, preserving any live
    /// missing-sources warning (that flag is owned by the live monitor).
    /// </summary>
    public void SetLogIngestDiagnostics(string datasourceName, LogIngestDiagnostics diagnostics)
    {
        UpdateState(state =>
        {
            if (state.LogProcessing.DatasourceDiagnostics.TryGetValue(datasourceName, out var existing))
            {
                diagnostics.MissingSourcesMessage = existing.MissingSourcesMessage;
            }
            state.LogProcessing.DatasourceDiagnostics[datasourceName] = diagnostics;
            state.LogProcessing.LastUpdated = DateTime.UtcNow;
        });
    }

    /// <summary>
    /// Sets or clears the live monitor's missing-sources warning for a datasource without
    /// touching the run counters.
    /// </summary>
    public void SetLogMissingSourcesWarning(string datasourceName, string? message)
    {
        UpdateState(state =>
        {
            if (!state.LogProcessing.DatasourceDiagnostics.TryGetValue(datasourceName, out var diagnostics))
            {
                diagnostics = new LogIngestDiagnostics();
                state.LogProcessing.DatasourceDiagnostics[datasourceName] = diagnostics;
            }
            diagnostics.MissingSourcesMessage = message;
            state.LogProcessing.LastUpdated = DateTime.UtcNow;
        });
    }

    /// <summary>
    /// Generic load-from-JSON helper shared by both operation-list getters.
    /// Must be called <em>inside</em> the caller's lock — it does not acquire any lock itself.
    /// Returns the cached list (populating it on first call) and invokes <paramref name="cleanup"/>
    /// after a successful deserialisation.
    /// </summary>
    private List<T> LoadOperationList<T>(ref List<T>? cache, string filePath, Action cleanup)
    {
        if (cache != null)
        {
            return cache;
        }

        try
        {
            cache = File.Exists(filePath)
                ? JsonSerializer.Deserialize<List<T>>(File.ReadAllText(filePath)) ?? new List<T>()
                : new List<T>();

            cleanup();

            return cache;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load operation list from {Path}", filePath);
            cache = new List<T>();
            return cache;
        }
    }

    // Cache Clear Operations Methods - now use separate file (data/operations/cache_operations.json)
    public List<CacheClearOperation> GetCacheClearOperations()
    {
        lock (_cacheClearLock)
        {
            return LoadOperationList(ref _cachedCacheClearOperations, _cacheOperationsFilePath, PruneCacheClearOperations);
        }
    }

    public void RemoveCacheClearOperation(Guid id)
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
    /// Drops cache clear operations that finished (completed, failed or cancelled) longer ago
    /// than <see cref="_cacheClearOperationRetention"/>. Rows that have not reached a terminal
    /// state are kept regardless of age: they are either still running or waiting to be
    /// reconciled on the next startup, and they have no end time to measure against.
    /// </summary>
    private void PruneCacheClearOperations()
    {
        if (_cachedCacheClearOperations == null || _cachedCacheClearOperations.Count == 0)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - _cacheClearOperationRetention;
        var oldOps = _cachedCacheClearOperations
            .Where(o => o.Status.IsTerminal() && o.EndTime.HasValue && o.EndTime.Value < cutoff)
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
            return LoadOperationList(ref _cachedOperationStates, _operationHistoryFilePath, PruneOperationStates);
        }
    }

    public void RemoveOperationState(string id)
    {
        if (!Guid.TryParse(id, out var parsedId))
        {
            return;
        }

        lock (_operationLock)
        {
            var states = GetOperationStates();
            var removed = states.RemoveAll(o => o.Id == parsedId);
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
    /// Drops operation history rows that reached a terminal state (completed, failed or
    /// cancelled) longer ago than <see cref="_operationStateRetention"/>. This record has no end
    /// timestamp, so UpdatedAt stands in for when the operation finished. Non-terminal rows are
    /// kept regardless of age so an operation interrupted by a restart can still be resumed.
    /// This is a backstop only: <c>OperationStateService</c> expires its in-memory states after
    /// 24 hours and rewrites this list from what remains, so most rows never reach the window.
    /// </summary>
    private void PruneOperationStates()
    {
        if (_cachedOperationStates == null || _cachedOperationStates.Count == 0)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - _operationStateRetention;
        var oldStates = _cachedOperationStates
            .Where(s => s.Status.IsTerminal() && s.UpdatedAt < cutoff)
            .ToList();

        if (oldStates.Count > 0)
        {
            foreach (var state in oldStates)
            {
                _cachedOperationStates.Remove(state);
            }
            SaveOperationStates();
            _logger.LogInformation("Cleaned up {Count} old finished operation states", oldStates.Count);
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
        lock (_signalLock)
        {
            if (completed)
                _setupCompletedSignal.TrySetResult(true);
            else if (_setupCompletedSignal.Task.IsCompleted)
                _setupCompletedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public async Task WaitForSetupCompletedAsync(CancellationToken cancellationToken)
    {
        if (GetSetupCompleted()) return;

        Task signal;
        lock (_signalLock) { signal = _setupCompletedSignal.Task; }

        // Re-check after capturing signal to avoid TOCTOU race
        if (GetSetupCompleted()) return;

        await signal.WaitAsync(cancellationToken);
    }

    // Setup Wizard State Methods
    public string? GetCurrentSetupStep()
    {
        return GetState().CurrentSetupStep?.ToWireString();
    }

    public void SetCurrentSetupStep(string? step)
    {
        var parsed = SetupStepExtensions.TryParseWire(step);
        UpdateState(state => state.CurrentSetupStep = parsed);
    }

    public string? GetDataSourceChoice()
    {
        return GetState().DataSourceChoice?.ToWireString();
    }

    public void SetDataSourceChoice(string? choice)
    {
        var parsed = DataSourceChoiceExtensions.TryParseWire(choice);
        UpdateState(state => state.DataSourceChoice = parsed);
    }

    public string? GetCompletedPlatforms()
    {
        return GetState().CompletedPlatforms;
    }

    public void SetCompletedPlatforms(string? platforms)
    {
        UpdateState(state => state.CompletedPlatforms = platforms);
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
    public bool HasProcessedLogs()
    {
        return GetState().HasProcessedLogs;
    }

    public void SetHasProcessedLogs(bool processed)
    {
        UpdateState(state => state.HasProcessedLogs = processed);
        lock (_signalLock)
        {
            if (processed)
                _logsProcessedSignal.TrySetResult(true);
            else if (_logsProcessedSignal.Task.IsCompleted)
                _logsProcessedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public async Task WaitForLogsProcessedAsync(CancellationToken cancellationToken)
    {
        if (HasProcessedLogs()) return;

        Task signal;
        lock (_signalLock) { signal = _logsProcessedSignal.Task; }

        // Re-check after capturing signal to avoid TOCTOU race
        if (HasProcessedLogs()) return;

        await signal.WaitAsync(cancellationToken);
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

    public DateTime? GetLastFullPicsCrawl()
    {
        return GetState().LastFullPicsCrawl;
    }

    public void SetLastFullPicsCrawl(DateTime crawlTime)
    {
        UpdateState(state => state.LastFullPicsCrawl = crawlTime);
    }

    // Status Check (DNS diagnostics) Methods
    public StatusCheckResult? GetStatusCheckResult()
    {
        return GetState().StatusCheckResult;
    }

    public void SetStatusCheckResult(StatusCheckResult result)
    {
        UpdateState(state => state.StatusCheckResult = result);
    }

    public string GetStatusCheckResolverMode()
    {
        return GetState().StatusCheckResolverMode;
    }

    public void SetStatusCheckResolverMode(string mode)
    {
        UpdateState(state => state.StatusCheckResolverMode = mode);
    }

    // Epic Mapping Last-Collection Methods
    public DateTime? GetEpicMappingCollectedAt()
    {
        return GetState().EpicMappingLastCollection;
    }

    public void SetEpicMappingLastCollection(DateTime collectionTime)
    {
        UpdateState(state => state.EpicMappingLastCollection = collectionTime);
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

    // Per-Game Metric Cap Methods
    public int GetTopGameCount()
    {
        return GetState().TopGameCount;
    }

    public void SetTopGameCount(int count)
    {
        UpdateState(state => state.TopGameCount = count);
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

    // Service Interval Methods
    public double? GetServiceInterval(string serviceKey)
    {
        var intervals = GetState().ServiceIntervals;
        return intervals.TryGetValue(serviceKey, out var value) ? value : null;
    }

    public void SetServiceInterval(string serviceKey, double hours)
    {
        UpdateState(state =>
        {
            state.ServiceIntervals[serviceKey] = hours;
        });
    }

    public void ClearServiceInterval(string serviceKey)
    {
        UpdateState(state =>
        {
            state.ServiceIntervals.Remove(serviceKey);
        });
    }

    // Service Custom Schedule Methods (absent key = the service runs on its interval)
    public CustomSchedule? GetServiceCustomSchedule(string serviceKey)
    {
        var schedules = GetState().ServiceCustomSchedule;
        return schedules.TryGetValue(serviceKey, out var value) ? value : null;
    }

    public void SetServiceCustomSchedule(string serviceKey, CustomSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        UpdateState(state =>
        {
            state.ServiceCustomSchedule[serviceKey] = schedule;
        });
    }

    public void ClearServiceCustomSchedule(string serviceKey)
    {
        UpdateState(state =>
        {
            state.ServiceCustomSchedule.Remove(serviceKey);
        });
    }

    // Datasource Cache Size Override Methods
    public Dictionary<string, long> GetDatasourceCacheSizeOverrides()
    {
        return NormalizeDatasourceCacheSizeOverrides(GetState().DatasourceCacheSizeOverrides);
    }

    public void SetDatasourceCacheSizeOverride(string datasourceName, long? bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasourceName);
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "Cache-size override cannot be negative.");
        }

        UpdateState(state =>
        {
            state.DatasourceCacheSizeOverrides ??= new Dictionary<string, long>();
            var existingKeys = state.DatasourceCacheSizeOverrides.Keys
                .Where(key => key.Equals(datasourceName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var existingKey in existingKeys)
            {
                state.DatasourceCacheSizeOverrides.Remove(existingKey);
            }

            if (bytes is > 0)
            {
                state.DatasourceCacheSizeOverrides[datasourceName] = bytes.Value;
            }
        });
    }

    // Service RunOnStartup Methods
    public bool? GetServiceRunOnStartup(string serviceKey)
    {
        var values = GetState().ServiceRunOnStartup;
        return values.TryGetValue(serviceKey, out var value) ? value : null;
    }

    public void SetServiceRunOnStartup(string serviceKey, bool runOnStartup)
    {
        UpdateState(state =>
        {
            state.ServiceRunOnStartup[serviceKey] = runOnStartup;
        });
    }

    public void ClearServiceRunOnStartup(string serviceKey)
    {
        UpdateState(state =>
        {
            state.ServiceRunOnStartup.Remove(serviceKey);
        });
    }

    // Service NotificationMode Methods
    public NotificationMode? GetServiceNotificationMode(string serviceKey)
    {
        var values = GetState().ServiceNotificationMode;
        return values.TryGetValue(serviceKey, out var value) ? value : null;
    }

    public void SetServiceNotificationMode(string serviceKey, NotificationMode mode)
    {
        UpdateState(state =>
        {
            state.ServiceNotificationMode[serviceKey] = mode;
        });
    }

    public void ClearServiceNotificationMode(string serviceKey)
    {
        UpdateState(state =>
        {
            state.ServiceNotificationMode.Remove(serviceKey);
        });
    }

    // Service NotificationDisplayMode Methods
    public NotificationDisplayMode? GetServiceNotificationDisplayMode(string serviceKey)
    {
        var values = GetState().ServiceNotificationDisplayMode;
        return values.TryGetValue(serviceKey, out var value) ? value : null;
    }

    public void SetServiceNotificationDisplayMode(string serviceKey, NotificationDisplayMode mode)
    {
        UpdateState(state =>
        {
            state.ServiceNotificationDisplayMode[serviceKey] = mode;
        });
    }

    public void ClearServiceNotificationDisplayMode(string serviceKey)
    {
        UpdateState(state =>
        {
            state.ServiceNotificationDisplayMode.Remove(serviceKey);
        });
    }

    // Scheduled Prefill Config Methods
    public ScheduledPrefillConfigDto GetScheduledPrefillConfig()
    {
        // AppState.ScheduledPrefill is a non-nullable property defaulted to CreateDefault() (StateModels.cs),
        // and every load path resolves it via ResolveScheduledPrefillConfig, so it is never null here.
        // Revalidate so callers always receive a known-good object.
        return ScheduledPrefillConfigFactory.Validate(GetState().ScheduledPrefill);
    }

    public void SetScheduledPrefillConfig(ScheduledPrefillConfigDto config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Validate before mutating state so an invalid config surfaces an explicit error to the
        // caller and never gets persisted. ToPersisted re-validates as a final guard.
        var validated = ScheduledPrefillConfigFactory.Validate(config);
        UpdateState(state =>
        {
            // Capture the OLD per-service enabled flags BEFORE overwriting the config, so we can tell a
            // brand-new / re-enabled service (whose first run we anchor to save-time) apart from one that
            // was already enabled (whose genuine last-run we must never clobber). Null-safe: no prior
            // config means "not enabled before" for every service.
            var previousEnabled = new Dictionary<string, bool>();
            var previousConfig = state.ScheduledPrefill;
            if (previousConfig is not null)
            {
                foreach (var previousService in previousConfig.GetServicesInRunOrder())
                {
                    previousEnabled[previousService.ServiceId.ToString()] = previousService.Enabled;
                }
            }

            state.ScheduledPrefill = validated;

            // Anchor first-run for each newly-enabled positive-interval service to save-time, so the next
            // 1-minute poll sees lastRun = now (not null) and schedules it one interval out instead of
            // running it immediately. Uses the SAME per-service key the poll loop and
            // Get/SetScheduledPrefillServiceLastRun use (ServiceId.ToString()). Run Now stays instant.
            // A service on a custom schedule is anchored on the same terms whatever its interval value
            // is: the stamp is the point occurrences are measured from, and without one a schedule
            // saved on a paused or startup-only interval would never become due at all.
            var anchoredAt = DateTime.UtcNow;
            foreach (var service in validated.GetServicesInRunOrder())
            {
                var key = service.ServiceId.ToString();
                var hasExistingLastRun = state.ScheduledPrefillServiceLastRunUtc.ContainsKey(key);
                var wasEnabledBefore = previousEnabled.TryGetValue(key, out var enabledBefore) && enabledBefore;

                if (ScheduledPrefillRunGates.ShouldAnchorFirstRunOnSave(
                        service.Enabled, service.IntervalHours, hasExistingLastRun, wasEnabledBefore, service.CustomSchedule))
                {
                    state.ScheduledPrefillServiceLastRunUtc[key] = anchoredAt;
                }
            }
        });
    }

    // Scheduled Prefill Per-Service Last-Run Methods (durable, keyed by PrefillPlatform name)
    public DateTime? GetScheduledPrefillServiceLastRun(string platform)
    {
        // Read under _lock: the backing dictionary is mutated by the save / reset / post-run stamp writers
        // (all under _lock), so an unlocked TryGetValue on the poll thread can tear or throw
        // InvalidOperationException if it races a concurrent resize. Match the write discipline.
        lock (_lock)
        {
            return GetState().ScheduledPrefillServiceLastRunUtc.TryGetValue(platform, out var value)
                ? value
                : null;
        }
    }

    public void SetScheduledPrefillServiceLastRun(string platform, DateTime lastRunUtc)
    {
        UpdateState(state =>
        {
            state.ScheduledPrefillServiceLastRunUtc[platform] = lastRunUtc;
        });
    }

    // Actual last-run (the honest "Last run" the schedule view shows): stamped ONLY when a service
    // genuinely runs, distinct from the anchor / advance-on-attempt semantics of the schedule-basis map.
    public DateTime? GetScheduledPrefillServiceLastActualRun(string platform)
    {
        // Match the locked read discipline of GetScheduledPrefillServiceLastRun (concurrent writers).
        lock (_lock)
        {
            return GetState().ScheduledPrefillServiceLastActualRunUtc.TryGetValue(platform, out var value)
                ? value
                : null;
        }
    }

    public void SetScheduledPrefillServiceLastActualRun(string platform, DateTime lastRunUtc)
    {
        UpdateState(state =>
        {
            state.ScheduledPrefillServiceLastActualRunUtc[platform] = lastRunUtc;
        });
    }

    /// <summary>
    /// Clears every persisted per-service last-run timestamp and immediately re-anchors the currently
    /// enabled, positive-interval services to now (the same initial-seed rule the load path applies via
    /// <see cref="SeedInitialFirstRunAnchors"/>). A bare clear would leave those services with a null
    /// last-run, which the next poll treats as never-run and instant-runs; reseeding returns the schedule
    /// to a "wait one full interval" baseline. Clear-and-reseed happens in a single <see cref="UpdateState"/>
    /// so the poll thread never observes the momentarily-empty map. Used by the Schedules reset-to-defaults path.
    /// </summary>
    public void ClearScheduledPrefillServiceLastRun()
    {
        UpdateState(state =>
        {
            state.ScheduledPrefillServiceLastRunUtc.Clear();
            SeedInitialFirstRunAnchors(state, DateTime.UtcNow);
            // The genuine-run history is NOT reseeded: after a reset nothing has actually run, so the
            // schedule view must read "Never" for every service until its next real run.
            state.ScheduledPrefillServiceLastActualRunUtc.Clear();
        });
    }

    /// <summary>
    /// One-time migration: seed the per-service notification mode for the cache-reconciliation
    /// (eviction) schedule from the legacy global <c>EvictionScanNotifications</c> flag. Seeds only
    /// when the legacy flag was explicitly turned on (true -> All): a user who once opted in keeps
    /// that choice. When the flag is false - the legacy default, indistinguishable from "never
    /// touched" - nothing is seeded, so the schedule falls back to its own hardcoded default (Silent)
    /// instead of pinning every existing install to Manual.
    /// Gated on <see cref="AppState.EvictionNotificationsMigrated"/>, NOT on the per-service key's
    /// presence - Reset to Defaults removes the key to restore the hardcoded default, and re-running
    /// this migration on the next load (key absent again) would silently undo that reset by
    /// resurrecting the legacy flag's value. Returns true when the state changed, so the caller can
    /// persist the marker (and any seeded entry).
    /// </summary>
    private static bool MigrateEvictionNotificationsToServiceMode(AppState state)
    {
        if (state.EvictionNotificationsMigrated)
        {
            return false;
        }

        // Must match the ServiceKey the cache-reconciliation schedule registers under (see
        // ServiceScheduleRegistry's allowlist), so the seeded value is the one that service loads.
        const string cacheReconciliationKey = "cacheReconciliation";

        if (state.EvictionScanNotifications)
        {
            state.ServiceNotificationMode[cacheReconciliationKey] = NotificationMode.All;
        }

        state.EvictionNotificationsMigrated = true;
        return true;
    }

    /// <summary>
    /// The clock formats an install could hold before the UTC clock existed. Written out rather than
    /// derived from <see cref="TimeFormats.All"/> because it is a historical value, not a second copy of
    /// the current list: deriving it would quietly change what counts as an untouched install the next
    /// time a format is added.
    /// </summary>
    private static readonly string[] _timeFormatsBeforeUtc =
        { "server-24h", "server-12h", "local-24h", "local-12h" };

    /// <summary>
    /// One-time migration: offer "utc" to an install that predates it. The allowed list is written back on
    /// every save, so an install created before the UTC clock keeps its four-item list forever and the load
    /// path's default never applies - no guest there can pick UTC until an admin re-saves the list by hand.
    /// Acts only when the stored list is exactly those four, the one value an install that never narrowed
    /// the choice can hold; an admin who picked a shorter list is left alone, because a stored list cannot
    /// say whether UTC was excluded deliberately.
    /// Gated on <see cref="AppState.UtcTimeFormatMigrated"/> rather than on the list's contents, so an admin
    /// who later removes "utc" on purpose does not find it back after the next restart. Returns true when
    /// the state changed, so the caller can persist the marker (and any added format).
    /// </summary>
    private static bool MigrateAllowedTimeFormatsForUtc(AppState state)
    {
        if (state.UtcTimeFormatMigrated)
        {
            return false;
        }

        if (state.AllowedTimeFormats.Count == _timeFormatsBeforeUtc.Length &&
            _timeFormatsBeforeUtc.All(format => state.AllowedTimeFormats.Contains(format)))
        {
            state.AllowedTimeFormats.Add("utc");
        }

        state.UtcTimeFormatMigrated = true;
        return true;
    }

    /// <summary>
    /// Anchors the initial first-run for each enabled service that has no last-run key yet and runs on
    /// either a positive interval or a custom schedule, stamping it to <paramref name="nowUtc"/> so the
    /// next poll schedules it one interval (or one occurrence) out instead of running it immediately.
    /// Only seeds MISSING keys via
    /// <see cref="ScheduledPrefillRunGates.ShouldAnchorFirstRunOnLoad"/> — a genuine persisted last-run is
    /// never clobbered, so a normal restart (whose last-run map already round-tripped) never shifts its
    /// schedule. Shared by the load path and the reset path; the explicit-save path keeps its own
    /// transition-aware anchor (<see cref="ScheduledPrefillRunGates.ShouldAnchorFirstRunOnSave"/>).
    /// Returns true when it added at least one anchor, so the caller can persist the change.
    /// </summary>
    private static bool SeedInitialFirstRunAnchors(AppState state, DateTime nowUtc)
    {
        var seeded = false;
        foreach (var service in state.ScheduledPrefill.GetServicesInRunOrder())
        {
            var key = service.ServiceId.ToString();
            if (ScheduledPrefillRunGates.ShouldAnchorFirstRunOnLoad(
                    service.Enabled,
                    service.IntervalHours,
                    state.ScheduledPrefillServiceLastRunUtc.ContainsKey(key),
                    service.CustomSchedule))
            {
                state.ScheduledPrefillServiceLastRunUtc[key] = nowUtc;
                seeded = true;
            }
        }

        return seeded;
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
                state.SetupCompleted = content == "1" || string.Equals(content, "true", StringComparison.OrdinalIgnoreCase);
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
    /// Normalizes a persisted scheduled prefill config: default-constructs when missing (migration
    /// from a pre-feature state.json) and falls back to defaults when an existing config fails
    /// validation, so a corrupt block never blocks state load. All rules live in
    /// <see cref="ScheduledPrefillConfigFactory"/>.
    /// </summary>
    private ScheduledPrefillConfigDto ResolveScheduledPrefillConfig(
        ScheduledPrefillConfigDto? config,
        double? legacyGlobalIntervalHours)
    {
        if (config is null)
        {
            return ScheduledPrefillConfigFactory.CreateDefault();
        }

        try
        {
            // Migrate an older-versioned config up to the current schema (e.g. v1 had no per-service
            // IntervalHours) BEFORE validation, seeding each service's interval from the legacy global
            // ServiceIntervals["scheduledPrefill"] value. A current-version config passes through.
            var migrated = ScheduledPrefillConfigFactory.Migrate(config, legacyGlobalIntervalHours);
            return ScheduledPrefillConfigFactory.Validate(migrated);
        }
        catch (ScheduledPrefillConfigValidationException ex)
        {
            _logger.LogWarning(ex, "Invalid scheduled prefill config in state; reverting to defaults");
            return ScheduledPrefillConfigFactory.CreateDefault();
        }
    }

    // The persisted sections the JSON serializer binds, resolved once from serializer metadata (not raw CLR
    // reflection) so the corrupt-load recovery path uses the SAME effective JSON names and setters as the
    // main deserialize. A future [JsonPropertyName] or naming-policy change can't silently desync the two
    // paths, and a newly added persisted field is bound automatically rather than dropped.
    private static readonly JsonPropertyInfo[] _persistedStateSections =
        JsonSerializerOptions.Default.GetTypeInfo(typeof(AppState)).Properties
            .Where(section => section.Set is not null)
            .ToArray();

    /// <summary>The result of binding one JSON node to a target type via <see cref="TryBindJsonValue"/>.</summary>
    private enum JsonBindOutcome
    {
        /// <summary>A value was produced (null only for an explicit JSON null on a nullable/reference target).</summary>
        Bound,

        /// <summary>An explicit JSON null on a non-nullable value type: not a usable value.</summary>
        NullRejected,

        /// <summary>The node did not deserialize into the target type (see the out error).</summary>
        Failed
    }

    /// <summary>
    /// Binds one TOP-LEVEL section through a single-property wrapper deserialize of
    /// <see cref="AppState"/>, so property-level attributes (the enum wire converters,
    /// most importantly) apply exactly as they do on a whole-file deserialize. The nested
    /// dictionary-entry and compound-member salvage keep binding bare types via
    /// <see cref="TryBindJsonValue"/>, where no property attributes exist.
    /// </summary>
    private static JsonBindOutcome TryBindSectionValue(JsonPropertyInfo section, JsonNode? node, out object? value, out JsonException? error)
    {
        value = null;
        error = null;

        if (node is null)
        {
            // Explicit JSON null: valid only for a nullable/reference target; a null on a non-nullable value
            // type is itself an invalid value. (The enum wire converters accept null on a whole-file
            // deserialize and fall back to their defaults, which is also what the NullRejected path keeps.)
            return !section.PropertyType.IsValueType || Nullable.GetUnderlyingType(section.PropertyType) is not null
                ? JsonBindOutcome.Bound
                : JsonBindOutcome.NullRejected;
        }

        try
        {
            var wrapper = new JsonObject { [section.Name] = node.DeepClone() };
            var probe = wrapper.Deserialize<AppState>(JsonSerializerOptions.Default)!;
            value = section.Get!(probe);
            return JsonBindOutcome.Bound;
        }
        catch (JsonException ex)
        {
            error = ex;
            return JsonBindOutcome.Failed;
        }
    }

    /// <summary>
    /// Binds a single JSON node to <paramref name="targetType"/> using the SAME options and null semantics as
    /// the main deserialize, without storing anything. The bare-type bind kernel behind the compound-member
    /// salvage and the dictionary-entry salvage; each caller maps the outcome to its own failure accounting
    /// (dropped-member count, dropped-entry count). On <see cref="JsonBindOutcome.Bound"/>,
    /// <paramref name="value"/> is the result to store (null only for an explicit JSON null on a
    /// nullable/reference target).
    /// </summary>
    private static JsonBindOutcome TryBindJsonValue(JsonNode? node, Type targetType, out object? value, out JsonException? error)
    {
        value = null;
        error = null;

        if (node is null)
        {
            // Explicit JSON null: valid only for a nullable/reference target; a null on a non-nullable value
            // type is itself an invalid value.
            return !targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null
                ? JsonBindOutcome.Bound
                : JsonBindOutcome.NullRejected;
        }

        try
        {
            value = node.Deserialize(targetType, JsonSerializerOptions.Default);
            return JsonBindOutcome.Bound;
        }
        catch (JsonException ex)
        {
            error = ex;
            return JsonBindOutcome.Failed;
        }
    }

    /// <summary>
    /// Recovers as much persisted state as possible when a whole-file deserialize fails because ONE section
    /// holds an invalid value (e.g. an out-of-range enum rejected by a strict converter). Parses the file
    /// once into a <see cref="JsonObject"/> and binds each top-level section independently, so a single
    /// corrupt block degrades to its own default (its existing downstream repair path, such as
    /// <see cref="ResolveScheduledPrefillConfig"/> for the scheduled-prefill block) while every other
    /// persisted setting is preserved. Keyed maps salvage their valid entries (see
    /// <see cref="TrySalvageDictionarySection"/>) instead of resetting wholesale. Each reset is logged with
    /// the section name and the underlying cause. A truly unparseable file, or a non-object root, is not
    /// recoverable per-section, so <paramref name="originalError"/> (the whole-file deserialize failure) is
    /// re-thrown to let the caller's outer fallback handle it exactly as before.
    /// </summary>
    private AppState DeserializePersistedStateWithSectionIsolation(string json, JsonException originalError)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw new JsonException("Persisted state root is not a JSON object.");
        }
        catch (JsonException)
        {
            // Unparseable file or non-object root: there are no sections to isolate. Re-throw the ORIGINAL
            // whole-file deserialize error (not the re-parse error) so the outer catch reports today's cause
            // and falls back to a default state exactly as it did before section isolation existed.
            throw originalError;
        }

        // Only warn about per-section recovery once we know the file is structurally recoverable.
        _logger.LogWarning(originalError, "State file has an invalid section; recovering per-section so only the corrupt block(s) reset");

        var result = new AppState();
        foreach (var section in _persistedStateSections)
        {
            if (!root.TryGetPropertyValue(section.Name, out var node))
            {
                // Absent key: keep the DTO default, matching a normal deserialize of a missing property.
                continue;
            }

            switch (TryBindSectionValue(section, node, out var value, out var error))
            {
                case JsonBindOutcome.Bound:
                    // Store an explicit null (nullable target) and any deserialized value. A non-null node
                    // never yields a null value here, preserving the original `if (value is not null)` guard.
                    if (node is null || value is not null)
                    {
                        section.Set!(result, value);
                    }

                    break;

                case JsonBindOutcome.NullRejected:
                    _logger.LogWarning(
                        "Persisted state section '{Section}' was null where a value is required; reset to its default, other settings preserved",
                        section.Name);
                    break;

                default:
                    // Recover as much of the section as is safe before falling back to its bare initializer:
                    //  - keyed maps keep the entries that bind (a reset schedule-basis/last-run map silently
                    //    re-anchors runs to "now" or erases genuine-run history, which is real data loss);
                    //  - durable-cursor compounds keep the members that bind (one bad field must not discard the
                    //    whole log/depot checkpoint);
                    //  - a security-sensitive value degrades fail-closed rather than to a weaker fail-open default.
                    // node is non-null on Failed (the null cases return above), so the salvage calls are safe.
                    if (TrySalvageDictionarySection(section, node!, result, error!)
                        || TrySalvageCompoundSection(section, node!, result, error!)
                        || TryApplyFailClosedSecurityDefault(section, result, error!))
                    {
                        break;
                    }

                    _logger.LogWarning(
                        error,
                        "Persisted state section '{Section}' could not be read (invalid value) and was reset to its default; other settings preserved",
                        section.Name);
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Attempts per-entry recovery of a <c>Dictionary&lt;string, T&gt;</c> section whose whole-section
    /// deserialize failed: keeps every entry that binds and drops only the invalid ones (logged), so one bad
    /// entry never discards the rest of the map. Returns false for a non-dictionary (or non-object) section
    /// so the caller applies the bare-default degrade instead.
    /// </summary>
    private bool TrySalvageDictionarySection(JsonPropertyInfo section, JsonNode node, AppState target, JsonException originalError)
    {
        if (node is not JsonObject map)
        {
            return false;
        }

        var sectionType = section.PropertyType;
        if (!sectionType.IsGenericType
            || sectionType.GetGenericTypeDefinition() != typeof(Dictionary<,>)
            || sectionType.GetGenericArguments()[0] != typeof(string))
        {
            return false;
        }

        var valueType = sectionType.GetGenericArguments()[1];
        var salvaged = (IDictionary)Activator.CreateInstance(sectionType)!;
        var droppedCount = 0;
        foreach (var entry in map)
        {
            switch (TryBindJsonValue(entry.Value, valueType, out var value, out _))
            {
                case JsonBindOutcome.Bound:
                    salvaged[entry.Key] = value;
                    break;

                default:
                    // Invalid, or a JSON null on a non-nullable value entry: drop just this entry.
                    droppedCount++;
                    break;
            }
        }

        section.Set!(target, salvaged);
        _logger.LogWarning(
            originalError,
            "Persisted state section '{Section}' had {DroppedCount} invalid entries dropped; {KeptCount} preserved",
            section.Name,
            droppedCount,
            salvaged.Count);
        return true;
    }

    /// <summary>
    /// Attempts member-by-member recovery of a durable-cursor COMPOUND section (the log/depot processing
    /// checkpoints) whose whole-section deserialize failed: keeps every member that binds and defaults only
    /// the invalid one(s), so a single malformed field can't discard the entire cursor (e.g. every log
    /// position). Scoped to those durable cursors; any other object section keeps the bare-default degrade.
    /// </summary>
    private bool TrySalvageCompoundSection(JsonPropertyInfo section, JsonNode node, AppState target, JsonException originalError)
    {
        if (section.PropertyType != typeof(LogProcessingState) && section.PropertyType != typeof(DepotProcessingState))
        {
            return false;
        }

        if (node is not JsonObject memberObject)
        {
            return false;
        }

        var salvaged = Activator.CreateInstance(section.PropertyType)!;
        var droppedCount = 0;
        foreach (var member in JsonSerializerOptions.Default.GetTypeInfo(section.PropertyType).Properties)
        {
            if (member.Set is null || !memberObject.TryGetPropertyValue(member.Name, out var memberNode))
            {
                continue;
            }

            switch (TryBindJsonValue(memberNode, member.PropertyType, out var value, out _))
            {
                case JsonBindOutcome.Bound:
                    // Store an explicit null (nullable member) and any deserialized value; a non-null node
                    // never yields a null value here, matching the original per-member guard.
                    if (memberNode is null || value is not null)
                    {
                        member.Set(salvaged, value);
                    }

                    break;

                default:
                    // Invalid, or a JSON null on a non-nullable member: leave it at the compound's default.
                    droppedCount++;
                    break;
            }
        }

        section.Set!(target, salvaged);
        _logger.LogWarning(
            originalError,
            "Persisted state section '{Section}' had {DroppedCount} invalid member(s) reset to default; the rest were preserved",
            section.Name,
            droppedCount);
        return true;
    }

    /// <summary>
    /// Applies a conservative fail-closed default when a SECURITY-sensitive section is present but invalid,
    /// instead of the bare initializer. An invalid metrics-auth value degrades to "authentication required"
    /// (true) so a corrupt value can never silently weaken the policy to the fail-open configuration default.
    /// </summary>
    private bool TryApplyFailClosedSecurityDefault(JsonPropertyInfo section, AppState target, JsonException originalError)
    {
        // Identifies the metrics-auth toggle by its (attribute-free) persisted name.
        if (section.Name != nameof(AppState.RequireAuthForMetrics))
        {
            return false;
        }

        section.Set!(target, true);
        _logger.LogWarning(
            originalError,
            "Persisted state section '{Section}' had an invalid value; failing closed (metrics authentication required) instead of the fail-open default",
            section.Name);
        return true;
    }

    private static Dictionary<string, long> NormalizeDatasourceCacheSizeOverrides(
        Dictionary<string, long>? overrides)
    {
        var normalized = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (overrides == null)
        {
            return normalized;
        }

        foreach (var (datasourceName, bytes) in overrides)
        {
            if (!string.IsNullOrWhiteSpace(datasourceName) && bytes > 0)
            {
                normalized[datasourceName] = bytes;
            }
        }

        return normalized;
    }

    /// <summary>
    /// Extracts the legacy single scheduled-prefill schedule interval (hours) from the per-service
    /// interval map. Pre-v2 state.json stored one global cadence under "scheduledPrefill"; it is the
    /// migration seed for every service's new per-service <c>IntervalHours</c>.
    /// </summary>
    private static double? GetLegacyScheduledPrefillInterval(Dictionary<string, double>? intervals)
    {
        if (intervals is not null && intervals.TryGetValue("scheduledPrefill", out var hours))
        {
            return hours;
        }

        return null;
    }

    /// <summary>
    /// Repairs a freshly deserialized state in place: null-coalesces list/map blocks a hand-edited
    /// or ancient state.json may carry as JSON null, folds legacy exclusion IPs into rules,
    /// validates the scheduled-prefill block, and decrypts the legacy SteamAuth token. The enum
    /// wire formats are handled by the converters on <see cref="AppState"/> itself.
    /// </summary>
    private AppState NormalizeLoadedState(AppState state)
    {
        state.DefaultGuestTheme ??= "dark-default";
        state.AllowedTimeFormats ??= new List<string>(TimeFormats.All);
        state.DefaultPrefillOperatingSystems ??= new List<string> { "windows", "linux", "macos" };
        state.DefaultPrefillMaxConcurrency ??= "default";
        state.EpicDefaultPrefillMaxConcurrency ??= "default";
        state.ExcludedClientIps ??= new List<string>();
        state.ExcludedClientRules = ResolveExcludedClientRules(state);
        state.DatasourceCacheSizeOverrides = NormalizeDatasourceCacheSizeOverrides(state.DatasourceCacheSizeOverrides);
        state.ServiceIntervals ??= new Dictionary<string, double>();
        state.ServiceRunOnStartup ??= new Dictionary<string, bool>();
        state.ServiceNotificationMode ??= new Dictionary<string, NotificationMode>();
        state.ServiceCustomSchedule ??= new Dictionary<string, CustomSchedule>();
        state.ServiceNotificationDisplayMode ??= new Dictionary<string, NotificationDisplayMode>();
        state.ScheduledPrefill = ResolveScheduledPrefillConfig(
            state.ScheduledPrefill,
            GetLegacyScheduledPrefillInterval(state.ServiceIntervals));
        state.ScheduledPrefillServiceLastRunUtc ??= new Dictionary<string, DateTime>();
        state.ScheduledPrefillServiceLastActualRunUtc ??= new Dictionary<string, DateTime>();

        // LEGACY: decrypt the migrated-away Steam auth token (present only in a pre-migration file)
        if (state.SteamAuth != null)
        {
            state.SteamAuth.RefreshToken = _encryption.Decrypt(state.SteamAuth.RefreshToken);
        }

        return state;
    }

    /// <summary>
    /// Builds the copy of <paramref name="state"/> that goes to disk: the legacy IP list rebuilt
    /// from the rules, overrides normalized, the scheduled-prefill block validated, and the legacy
    /// SteamAuth token encrypted. Works on a shallow clone so the cached in-memory state (decrypted
    /// token included) is never mutated by a save.
    /// </summary>
    private AppState PrepareWireState(AppState state)
    {
        var wire = state.ShallowClone();
        wire.DefaultPrefillOperatingSystems = state.DefaultPrefillOperatingSystems ?? new List<string> { "windows", "linux", "macos" };
        wire.DefaultPrefillMaxConcurrency = state.DefaultPrefillMaxConcurrency ?? "default";
        wire.EpicDefaultPrefillMaxConcurrency = state.EpicDefaultPrefillMaxConcurrency ?? "default";
        wire.ExcludedClientIps = BuildExcludedIpList(state.ExcludedClientRules, state.ExcludedClientIps);
        wire.ExcludedClientRules = state.ExcludedClientRules ?? new List<ClientExclusionRule>();
        wire.DatasourceCacheSizeOverrides = NormalizeDatasourceCacheSizeOverrides(state.DatasourceCacheSizeOverrides);
        wire.ServiceIntervals = state.ServiceIntervals ?? new Dictionary<string, double>();
        wire.ServiceRunOnStartup = state.ServiceRunOnStartup ?? new Dictionary<string, bool>();
        wire.ServiceNotificationMode = state.ServiceNotificationMode ?? new Dictionary<string, NotificationMode>();
        wire.ServiceCustomSchedule = state.ServiceCustomSchedule ?? new Dictionary<string, CustomSchedule>();
        wire.ServiceNotificationDisplayMode = state.ServiceNotificationDisplayMode ?? new Dictionary<string, NotificationDisplayMode>();
        // The in-memory config is already current-version, so Migrate is a no-op here.
        wire.ScheduledPrefill = ResolveScheduledPrefillConfig(
            state.ScheduledPrefill,
            GetLegacyScheduledPrefillInterval(state.ServiceIntervals));
        wire.ScheduledPrefillServiceLastRunUtc = state.ScheduledPrefillServiceLastRunUtc ?? new Dictionary<string, DateTime>();
        wire.ScheduledPrefillServiceLastActualRunUtc = state.ScheduledPrefillServiceLastActualRunUtc ?? new Dictionary<string, DateTime>();
        // LEGACY: encrypt the token on a fresh object so the cached state keeps the decrypted one.
        // JsonIgnore(WhenWritingNull) on the property excludes it from JSON once migrated away.
        wire.SteamAuth = state.SteamAuth != null
            ? new SteamAuthState
            {
                Mode = state.SteamAuth.Mode,
                Username = state.SteamAuth.Username,
                RefreshToken = _encryption.Encrypt(state.SteamAuth.RefreshToken),
                LastAuthenticated = state.SteamAuth.LastAuthenticated
            }
            : null;
        return wire;
    }

    /// <summary>
    /// Cleans up stale operations that were left in processing state
    /// </summary>
    private void CleanupStaleOperations(AppState state)
    {
        var staleOperations = state.OperationStates
            .Where(o => o.Type == OperationType.LogProcessing && o.Status == OperationStatus.Running)
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
    public SteamAuthMode? GetSteamAuthMode()
    {
        var raw = _steamAuthStorage.GetAuthData().Mode;
        return SteamAuthModeExtensions.TryParseWire(raw);
    }

    public void SetSteamAuthMode(SteamAuthMode mode)
    {
        var wire = mode.ToWireString();
        _steamAuthStorage.UpdateAuthData(data => data.Mode = wire);
    }

    public string? GetSteamUsername()
    {
        return _steamAuthStorage.GetAuthData().Username;
    }

    public void SetSteamUsername(string? username)
    {
        _steamAuthStorage.UpdateAuthData(data => data.Username = username);
    }

    public string? GetSteamRefreshToken()
    {
        return _steamAuthStorage.GetAuthData().RefreshToken;
    }

    public void SetSteamRefreshToken(string? token)
    {
        _steamAuthStorage.UpdateAuthData(data =>
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
        return !string.IsNullOrEmpty(_steamAuthStorage.GetAuthData().RefreshToken);
    }

    // NOTE: GuardData methods removed - modern Steam auth uses refresh tokens only
    // GetSteamGuardData() and SetSteamGuardData() are no longer needed

    // Guest Session Duration Methods
    public int? GetGuestSessionDurationHours()
    {
        return GetState().GuestSessionDurationHours;
    }

    public void SetGuestSessionDurationHours(int? hours)
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
        return GetState().RefreshRate.ToWireString();
    }

    public void SetRefreshRate(string rate)
    {
        // Validate the rate is a valid option; fall back to STANDARD when unrecognised.
        var parsed = RefreshRateExtensions.TryParseWire(rate) ?? RefreshRate.Standard;
        UpdateState(state => state.RefreshRate = parsed);
    }

    // Default Guest Refresh Rate Methods
    public string GetDefaultGuestRefreshRate()
    {
        return GetState().DefaultGuestRefreshRate.ToWireString();
    }

    public void SetDefaultGuestRefreshRate(string rate)
    {
        // Validate the rate is a valid option; fall back to STANDARD when unrecognised.
        var parsed = RefreshRateExtensions.TryParseWire(rate) ?? RefreshRate.Standard;
        UpdateState(state => state.DefaultGuestRefreshRate = parsed);
    }

    // Guest Refresh Rate Lock Methods
    public bool GetGuestRefreshRateLocked()
    {
        return GetState().GuestRefreshRateLocked;
    }

    public void SetGuestRefreshRateLocked(bool locked)
    {
        UpdateState(state => state.GuestRefreshRateLocked = locked);
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
    /// Note: For row visibility filtering, use GetHiddenClientIps() instead to only filter hide mode.
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
        var state = GetState();
        var rules = ResolveExcludedClientRules(state);
        return rules
            .Where(rule => NormalizeMode(rule.Mode) == ClientExclusionModes.Exclude)
            .Select(rule => rule.Ip)
            .Distinct()
            .ToList();
    }

    public void SetExcludedClientIps(List<string> ips)
    {
        UpdateState(state =>
        {
            var normalizedIps = ips ?? new List<string>();
            var existingHiddenRules = ResolveExcludedClientRules(state)
                .Where(rule => NormalizeMode(rule.Mode) == ClientExclusionModes.Hide)
                .Where(rule => !normalizedIps.Contains(rule.Ip))
                .ToList();

            state.ExcludedClientRules = existingHiddenRules
                .Concat(normalizedIps.Select(ip => new ClientExclusionRule
                {
                    Ip = ip,
                    Mode = ClientExclusionModes.Exclude
                }))
                .ToList();
            state.ExcludedClientIps = BuildExcludedIpList(state.ExcludedClientRules, normalizedIps);
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
        var state = GetState();
        var rules = ResolveExcludedClientRules(state);
        return rules
            .Where(rule => NormalizeMode(rule.Mode) == ClientExclusionModes.Hide)
            .Select(rule => rule.Ip)
            .Distinct()
            .ToList();
    }

    public string GetEvictedDataMode()
    {
        return GetState().EvictedDataMode.ToWireString();
    }

    public void SetEvictedDataMode(string mode)
    {
        var parsed = EvictedDataModeExtensions.TryParseWire(mode) ?? EvictedDataMode.Show;
        UpdateState(state => state.EvictedDataMode = parsed);
    }

    public bool GetEvictionScanNotifications()
    {
        return GetState().EvictionScanNotifications;
    }

    public bool GetPruneOrphanedDownloads()
    {
        return GetState().PruneOrphanedDownloads;
    }

    public void SetPruneOrphanedDownloads(bool enabled)
    {
        UpdateState(state => state.PruneOrphanedDownloads = enabled);
    }

    public void SetEvictionScanNotifications(bool enabled)
    {
        UpdateState(state => state.EvictionScanNotifications = enabled);
    }

    public bool GetClientHostnameLookup()
    {
        return GetState().ClientHostnameLookup;
    }

    public void SetClientHostnameLookup(bool enabled)
    {
        UpdateState(state => state.ClientHostnameLookup = enabled);
    }

    public string? GetClientHostnameResolver()
    {
        return GetState().ClientHostnameResolver;
    }

    public void SetClientHostnameResolver(string? resolverIp)
    {
        UpdateState(state => state.ClientHostnameResolver = resolverIp);
    }

    public bool GetClientHostnameGuestAccess()
    {
        return GetState().ClientHostnameGuestAccess;
    }

    public void SetClientHostnameGuestAccess(bool allowed)
    {
        UpdateState(state => state.ClientHostnameGuestAccess = allowed);
    }

    public bool GetClientHostnameRouterLookup()
    {
        return GetState().ClientHostnameRouterLookup;
    }

    public void SetClientHostnameRouterLookup(bool enabled)
    {
        UpdateState(state => state.ClientHostnameRouterLookup = enabled);
    }

    public bool GetClientHostnameDockerLookup()
    {
        return GetState().ClientHostnameDockerLookup;
    }

    public void SetClientHostnameDockerLookup(bool enabled)
    {
        UpdateState(state => state.ClientHostnameDockerLookup = enabled);
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

                // Legacy ExcludedClientIps predates the Hide/Exclude split and meant
                // "hide everywhere + exclude from stats", so migrate them to Hide to
                // preserve the original behavior for existing installs.
                normalized.Add(new ClientExclusionRule { Ip = trimmed, Mode = ClientExclusionModes.Hide });
            }
        }

        return normalized;
    }

    private static bool IsStatsExcludedMode(string? mode)
    {
        var normalized = NormalizeMode(mode);
        return normalized == ClientExclusionModes.Hide || normalized == ClientExclusionModes.Exclude;
    }

    private static string NormalizeMode(string? mode)
    {
        if (string.Equals(mode, ClientExclusionModes.Exclude, StringComparison.OrdinalIgnoreCase))
            return ClientExclusionModes.Exclude;
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
        if (hours is < 1 or > 3)
        {
            hours = 2;
        }
        UpdateState(state => state.GuestPrefillDurationHours = hours);
    }

    /// <summary>
    /// Validity window (in days) for a persistent admin login before re-authentication is required.
    /// </summary>
    public int GetAdminPersistentLoginValidityDays()
    {
        var days = GetState().AdminPersistentLoginValidityDays;
        // Clamp on read so a legacy/out-of-range persisted value (e.g. 0 from an older state.json
        // that predates this field) can never produce a zero/negative validity. Range 1-365.
        if (days < 1)
        {
            return 1;
        }
        if (days > 365)
        {
            return 365;
        }
        return days;
    }

    /// <summary>
    /// Persists the admin persistent login validity. Validates the allowed range 1-365 (inclusive);
    /// out-of-range values are clamped (no ||/?? defaulting).
    /// </summary>
    public void SetAdminPersistentLoginValidityDays(int days)
    {
        if (days < 1)
        {
            days = 1;
        }
        else if (days > 365)
        {
            days = 365;
        }
        UpdateState(state => state.AdminPersistentLoginValidityDays = days);
    }

    // Prefill Panel Default Settings
    public List<string> GetDefaultPrefillOperatingSystems()
    {
        return GetState().DefaultPrefillOperatingSystems ?? new List<string> { "windows", "linux", "macos" };
    }

    public void SetDefaultPrefillOperatingSystems(List<string> operatingSystems)
    {
        var valid = new[] { "windows", "linux", "macos" };
        var filtered = operatingSystems?.Where(os => valid.Contains(os.ToLowerInvariant())).ToList()
                       ?? new List<string> { "windows", "linux", "macos" };
        if (filtered.Count == 0) filtered = new List<string> { "windows", "linux", "macos" };
        UpdateState(state => state.DefaultPrefillOperatingSystems = filtered);
    }

    public string GetDefaultPrefillMaxConcurrency()
    {
        var value = GetState().DefaultPrefillMaxConcurrency ?? "auto";
        // Backwards compat: migrate saved "default" to "auto"
        return value.Equals("default", StringComparison.OrdinalIgnoreCase) ? "auto" : value;
    }

    public void SetDefaultPrefillMaxConcurrency(string maxConcurrency)
    {
        var normalized = maxConcurrency?.Trim().ToLowerInvariant() ?? "auto";

        // Migrate legacy "default" to "auto"
        if (normalized == "default") normalized = "auto";

        if (normalized == "auto" || normalized == "max")
        {
            UpdateState(state => state.DefaultPrefillMaxConcurrency = normalized);
            return;
        }

        if (int.TryParse(normalized, out var numericValue) && numericValue > 0)
        {
            // Cap at UI max (these are HTTP connections, not CPU threads)
            var capped = Math.Min(numericValue, 256);
            UpdateState(state => state.DefaultPrefillMaxConcurrency = capped.ToString());
            return;
        }

        // Invalid value - fall back to auto
        UpdateState(state => state.DefaultPrefillMaxConcurrency = "auto");
    }

    // Default Guest Max Thread Count Methods
    public int? GetDefaultGuestMaxThreadCount()
    {
        return GetState().DefaultGuestMaxThreadCount;
    }

    public void SetDefaultGuestMaxThreadCount(int? value)
    {
        if (value.HasValue)
        {
            if (value.Value < 1) value = 1;
            value = Math.Min(value.Value, 256);
        }
        UpdateState(state => state.DefaultGuestMaxThreadCount = value);
    }

    // Epic Guest Prefill Permission Methods
    public bool GetEpicGuestPrefillEnabledByDefault()
    {
        return GetState().EpicGuestPrefillEnabledByDefault;
    }

    public void SetEpicGuestPrefillEnabledByDefault(bool enabled)
    {
        UpdateState(state => state.EpicGuestPrefillEnabledByDefault = enabled);
    }

    public int GetEpicGuestPrefillDurationHours()
    {
        return GetState().EpicGuestPrefillDurationHours;
    }

    public void SetEpicGuestPrefillDurationHours(int hours)
    {
        if (hours is < 1 or > 3)
        {
            hours = 2;
        }
        UpdateState(state => state.EpicGuestPrefillDurationHours = hours);
    }

    // Battle.net Guest Prefill Permission Methods
    public bool GetBattleNetGuestPrefillEnabledByDefault()
    {
        return GetState().BattleNetGuestPrefillEnabledByDefault;
    }

    public void SetBattleNetGuestPrefillEnabledByDefault(bool enabled)
    {
        UpdateState(state => state.BattleNetGuestPrefillEnabledByDefault = enabled);
    }

    public int GetBattleNetGuestPrefillDurationHours()
    {
        return GetState().BattleNetGuestPrefillDurationHours;
    }

    public void SetBattleNetGuestPrefillDurationHours(int hours)
    {
        if (hours is < 1 or > 3)
        {
            hours = 2;
        }
        UpdateState(state => state.BattleNetGuestPrefillDurationHours = hours);
    }

    // Riot Guest Prefill Permission Methods
    public bool GetRiotGuestPrefillEnabledByDefault()
    {
        return GetState().RiotGuestPrefillEnabledByDefault;
    }

    public void SetRiotGuestPrefillEnabledByDefault(bool enabled)
    {
        UpdateState(state => state.RiotGuestPrefillEnabledByDefault = enabled);
    }

    public int GetRiotGuestPrefillDurationHours()
    {
        return GetState().RiotGuestPrefillDurationHours;
    }

    public void SetRiotGuestPrefillDurationHours(int hours)
    {
        if (hours is < 1 or > 3)
        {
            hours = 2;
        }
        UpdateState(state => state.RiotGuestPrefillDurationHours = hours);
    }

    // Xbox Guest Prefill Permission Methods (login-required - mirrors Epic, has a thread limit)
    public bool GetXboxGuestPrefillEnabledByDefault()
    {
        return GetState().XboxGuestPrefillEnabledByDefault;
    }

    public void SetXboxGuestPrefillEnabledByDefault(bool enabled)
    {
        UpdateState(state => state.XboxGuestPrefillEnabledByDefault = enabled);
    }

    public int GetXboxGuestPrefillDurationHours()
    {
        return GetState().XboxGuestPrefillDurationHours;
    }

    public void SetXboxGuestPrefillDurationHours(int hours)
    {
        if (hours is < 1 or > 3)
        {
            hours = 2;
        }
        UpdateState(state => state.XboxGuestPrefillDurationHours = hours);
    }

    public int? GetXboxDefaultGuestMaxThreadCount()
    {
        return GetState().XboxDefaultGuestMaxThreadCount;
    }

    public void SetXboxDefaultGuestMaxThreadCount(int? value)
    {
        if (value.HasValue)
        {
            if (value.Value < 1) value = 1;
            value = Math.Min(value.Value, 256);
        }
        UpdateState(state => state.XboxDefaultGuestMaxThreadCount = value);
    }

    public int? GetEpicDefaultGuestMaxThreadCount()
    {
        return GetState().EpicDefaultGuestMaxThreadCount;
    }

    public void SetEpicDefaultGuestMaxThreadCount(int? value)
    {
        if (value.HasValue)
        {
            if (value.Value < 1) value = 1;
            value = Math.Min(value.Value, 256);
        }
        UpdateState(state => state.EpicDefaultGuestMaxThreadCount = value);
    }

    public string GetEpicDefaultPrefillMaxConcurrency()
    {
        var value = GetState().EpicDefaultPrefillMaxConcurrency ?? "auto";
        // Backwards compat: migrate saved "default" to "auto"
        return value.Equals("default", StringComparison.OrdinalIgnoreCase) ? "auto" : value;
    }

    public void SetEpicDefaultPrefillMaxConcurrency(string maxConcurrency)
    {
        var normalized = maxConcurrency?.Trim().ToLowerInvariant() ?? "auto";

        // Migrate legacy "default" to "auto"
        if (normalized == "default") normalized = "auto";

        if (normalized == "auto" || normalized == "max")
        {
            UpdateState(state => state.EpicDefaultPrefillMaxConcurrency = normalized);
            return;
        }

        if (int.TryParse(normalized, out var numericValue) && numericValue > 0)
        {
            // Cap at UI max (these are HTTP connections, not CPU threads)
            var capped = Math.Min(numericValue, 256);
            UpdateState(state => state.EpicDefaultPrefillMaxConcurrency = capped.ToString());
            return;
        }

        // Invalid value - fall back to auto
        UpdateState(state => state.EpicDefaultPrefillMaxConcurrency = "auto");
    }
}
