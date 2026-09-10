using LancacheManager.Models;


namespace LancacheManager.Core.Services.SteamKit2;

public partial class SteamKit2Service
{
    /// <summary>
    /// Check if incremental scan is viable or if change gap is too large (will trigger full scan)
    /// Uses cached result from state.json if available and recent (< 1 hour old) to avoid repeated Steam API calls
    /// </summary>
    public async Task<IncrementalViabilityCheck> CheckViabilityAsync(CancellationToken ct)
    {
        try
        {
            long version;
            lock (_baselineLock) version = _baselineVersion;
            var baseline = await GetDepotBaselineAsync();
            bool superseded;
            lock (_baselineLock) superseded = version != _baselineVersion;
            if (superseded) return await CheckViabilityAsync(ct);
            // Check for a cached viability result in state (prevents repeated Steam API calls).
            var state = _stateService.GetState();
            var cachedAge = state.LastViabilityCheck.HasValue
                ? DateTime.UtcNow - state.LastViabilityCheck.Value
                : TimeSpan.MaxValue;
            var cacheIsFresh = state.LastViabilityCheck.HasValue && cachedAge < TimeSpan.FromHours(1);

            IncrementalViabilityCheck ReuseCachedResult()
            {
                _logger.LogInformation("Using cached viability check result (age: {Minutes} minutes, requires full scan: {RequiresFullScan})",
                    (int)cachedAge.TotalMinutes, state.RequiresFullScan);

                return new IncrementalViabilityCheck
                {
                    IsViable = !state.RequiresFullScan,
                    LastChangeNumber = state.LastViabilityCheckChangeNumber,
                    CurrentChangeNumber = state.LastViabilityCheckChangeNumber, // Use cached value since we didn't check Steam
                    ChangeGap = state.ViabilityChangeGap,
                    IsLargeGap = state.RequiresFullScan,
                    WillTriggerFullScan = state.RequiresFullScan,
                    EstimatedAppsToScan = state.RequiresFullScan ? 270000 : (int)Math.Min(state.ViabilityChangeGap * 2, 50000),
                    Error = null
                };
            }

            // Both cached outcomes belong to the cursor and catalog that produced them.
            uint changeNumberToCheck = baseline.LastChangeNumber;

            // Never reuse a cached "viable" answer once the baseline is gone (e.g. depot data was
            // reset): it was computed from mappings and a change number that no longer exist, and
            // reusing it would green-light a crawl with nothing to diff against.
            if (cacheIsFresh && ShouldReuseCachedViability(state.RequiresFullScan, baseline.HasUsableBaseline)
                && state.LastViabilityCheckChangeNumber == baseline.LastChangeNumber)
            {
                lock (_baselineLock)
                {
                    if (version == _baselineVersion) return ReuseCachedResult();
                }
                return await CheckViabilityAsync(ct);
            }

            // A fresh install (or a reset that wiped depot data) has no baseline to run an
            // incremental crawl from. Asking Steam for changes since 0 always returns a required
            // full update, so a crawl started here would throw. Report a required full scan so the
            // scheduler routes it through the existing graceful skip instead of a doomed crawl.
            // This is an expected precondition, not an error, so no exception and no error field.
            if (!baseline.HasUsableBaseline)
            {
                _logger.LogWarning(
                    "Incremental depot scan is not viable yet - no depot baseline found (database mappings: {DbCount}, JSON mappings: {JsonCount}, saved change number: {ChangeNumber}). Initial depot data must be downloaded or a full scan run before incremental updates can start.",
                    baseline.DatabaseMappingCount, baseline.JsonMappingCount, baseline.LastChangeNumber);

                return BuildNeedsInitialDataResult();
            }

            _logger.LogInformation("No valid cached viability result found - checking with Steam (cache age: {Minutes} minutes)",
                cachedAge == TimeSpan.MaxValue ? -1 : (int)cachedAge.TotalMinutes);

            if (changeNumberToCheck > 0)
            {
                _logger.LogInformation("Viability check will use change number {ChangeNumber} from JSON file", changeNumberToCheck);
            }

            var (currentChangeNumber, willRequireFullScan) = await CheckChangesAsync(changeNumberToCheck, ct);
            var currentBaseline = await GetDepotBaselineAsync();
            if (baseline != currentBaseline) return await CheckViabilityAsync(ct);
            uint changeGap = currentChangeNumber - Math.Min(changeNumberToCheck, currentChangeNumber);
            if (!CacheViabilityResult(willRequireFullScan, changeNumberToCheck, changeGap, version))
                return await CheckViabilityAsync(ct);

            return new IncrementalViabilityCheck
            {
                IsViable = !willRequireFullScan,
                LastChangeNumber = changeNumberToCheck,
                CurrentChangeNumber = currentChangeNumber,
                ChangeGap = changeGap,
                IsLargeGap = willRequireFullScan,
                WillTriggerFullScan = willRequireFullScan,
                EstimatedAppsToScan = willRequireFullScan ? 270000 : (int)Math.Min((long)changeGap * 2, 50000)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException tex)
        {
            _logger.LogWarning(tex, "Steam connection timed out while checking incremental viability");

            // Clean up connection state on timeout to prevent stale connections
            if (_steamClient?.IsConnected == true)
            {
                _intentionalDisconnect = true;
                _steamClient.Disconnect();
            }
            _isLoggedOn = false;

            var changeNumberForError = await TryGetLastChangeNumberAsync() ?? 0;

            // If we can't check viability, assume full scan is required for safety
            return new IncrementalViabilityCheck
            {
                IsViable = false,
                LastChangeNumber = changeNumberForError,
                CurrentChangeNumber = 0,
                ChangeGap = 0,
                IsLargeGap = true,
                WillTriggerFullScan = true,
                EstimatedAppsToScan = 270000,
                Error = tex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check incremental viability - connection or network error");

            // Clean up connection state on failure to prevent stale connections
            if (_steamClient?.IsConnected == true)
            {
                _intentionalDisconnect = true;
                _steamClient.Disconnect();
            }
            _isLoggedOn = false;

            var changeNumberForError = await TryGetLastChangeNumberAsync() ?? 0;

            // Return viability check with error - caller will handle as connection failure
            return new IncrementalViabilityCheck
            {
                IsViable = false,
                LastChangeNumber = changeNumberForError,
                CurrentChangeNumber = 0,
                ChangeGap = 0,
                IsLargeGap = false, // Not a large gap - it's a connection error
                WillTriggerFullScan = false, // Don't trigger full scan for connection errors
                EstimatedAppsToScan = 0,
                Error = $"Connection failed: {ex.Message}",
                StageKey = "errors.steam.connectionFailed",
                Context = new() { ["detail"] = ex.Message }
            };
        }
    }

    /// <summary>
    /// Gathers the evidence that decides whether an incremental depot crawl has a baseline to
    /// build on: the persisted database mappings, the JSON snapshot's mappings, and the saved PICS
    /// change number. Incremental updates diff against a previously saved change number, so without
    /// any of these there is nothing to build on.
    /// </summary>
    private async Task<DepotBaseline> GetDepotBaselineAsync()
    {
        var databaseMappingCount = await GetDepotMappingCountAsync();

        var jsonMappingCount = 0;
        uint lastChangeNumber = 0;
        var picsData = await _picsDataService.LoadFromJsonAsync();
        if (picsData is not null)
        {
            jsonMappingCount = picsData.DepotMappings?.Count ?? 0;
            if (picsData.Metadata?.LastChangeNumber > 0)
            {
                lastChangeNumber = picsData.Metadata.LastChangeNumber;
            }
        }

        var committed = _stateService.GetState().LastPicsChangeNumber;
        return new DepotBaseline(databaseMappingCount, jsonMappingCount, committed ?? lastChangeNumber,
            committed == null || committed == lastChangeNumber, committed.HasValue);
    }

    internal virtual async Task<(uint CurrentChangeNumber, bool RequiresFullScan)> CheckChangesAsync(uint changeNumberToCheck, CancellationToken ct)
    {
        bool wasConnected = _isLoggedOn && _steamClient?.IsConnected == true;
        if (!wasConnected) await EnsureSessionAsync(ct);
        var currentChangeNumber = await GetPicsChangeNumberAsync(ct);
        var incrementalChanges = await RunPicsWithRecoveryAsync(async () =>
        {
            var incrementalJob = _steamApps!.PICSGetChangesSince(changeNumberToCheck, true, true);
            return await WaitForCallbackAsync(incrementalJob, ct);
        }, "PICS viability check", ct);
        _logger.LogInformation("Steam RequiresFullUpdate: {Full}, RequiresFullAppUpdate: {App}",
            incrementalChanges.RequiresFullUpdate, incrementalChanges.RequiresFullAppUpdate);
        return (currentChangeNumber, incrementalChanges.RequiresFullUpdate || incrementalChanges.RequiresFullAppUpdate);
    }

    private async Task PinBaselineAsync()
    {
        if (_stateService.GetState().LastPicsChangeNumber.HasValue) return;
        var baseline = await GetDepotBaselineAsync();
        lock (_baselineLock)
        {
            _stateService.UpdateState(state => state.LastPicsChangeNumber ??= baseline.HasUsableBaseline ? baseline.LastChangeNumber : 0);
        }
    }

    /// <summary>
    /// Either cached outcome requires a usable baseline; the caller also checks cursor identity.
    /// </summary>
    internal static bool ShouldReuseCachedViability(bool cachedRequiresFullScan, bool hasUsableBaseline)
        => hasUsableBaseline;

    /// <summary>
    /// Builds the outcome for a fresh install with no depot baseline: not viable and flagged as a
    /// required full scan so the scheduler skips gracefully, with no error set so it is not mistaken
    /// for a Steam connection failure.
    /// </summary>
    internal static IncrementalViabilityCheck BuildNeedsInitialDataResult() => new()
    {
        IsViable = false,
        LastChangeNumber = 0,
        CurrentChangeNumber = 0,
        ChangeGap = 0,
        IsLargeGap = false,
        WillTriggerFullScan = true,
        EstimatedAppsToScan = 270000,
        Error = null
    };

    /// <summary>
    /// Persists the viability outcome to state so repeated checks within the cache window reuse it
    /// instead of contacting Steam again.
    /// </summary>
    private bool CacheViabilityResult(bool requiresFullScan, uint lastChangeNumber, uint changeGap, long version)
    {
        lock (_baselineLock)
        {
            if (version != _baselineVersion) return false;
            _stateService.UpdateState(state =>
            {
                state.RequiresFullScan = requiresFullScan;
                state.LastViabilityCheck = DateTime.UtcNow;
                state.LastViabilityCheckChangeNumber = lastChangeNumber;
                state.ViabilityChangeGap = changeGap;
            });
        }

        _logger.LogInformation("Cached viability check result in state.json (requires full scan: {RequiresFullScan}, change gap: {ChangeGap})",
            requiresFullScan, changeGap);
        return true;
    }

    /// <summary>
    /// Snapshot of the depot data available to seed an incremental crawl. With no mappings in the
    /// database or JSON snapshot and no saved change number there is nothing to diff against, so a
    /// full scan is required before incremental updates can start.
    /// </summary>
    internal readonly record struct DepotBaseline(int DatabaseMappingCount, int JsonMappingCount, uint LastChangeNumber, bool CursorMatches = true, bool Committed = false)
    {
        public bool HasUsableBaseline => LastChangeNumber > 0 && CursorMatches
            && (Committed ? DatabaseMappingCount > 0 && JsonMappingCount > 0 : DatabaseMappingCount > 0 || JsonMappingCount > 0);
    }

    /// <summary>
    /// Reads the committed PICS cursor, falling back to JSON only for legacy state.
    /// Returns null when no positive cursor is available.
    /// Used in error paths to populate LastChangeNumber for informational reporting.
    /// </summary>
    private async Task<uint?> TryGetLastChangeNumberAsync()
    {
        try
        {
            var picsData = await _picsDataService.LoadFromJsonAsync();
            var changeNumber = _stateService.GetState().LastPicsChangeNumber ?? picsData?.Metadata?.LastChangeNumber;
            return changeNumber > 0 ? changeNumber : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the committed PICS change number");
            return null;
        }
    }
}
