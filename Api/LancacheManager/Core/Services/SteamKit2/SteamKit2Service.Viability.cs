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
            // Check for a cached viability result in state (prevents repeated Steam API calls).
            var state = _stateService.GetState();
            var cachedAge = state.LastViabilityCheck.HasValue
                ? DateTime.UtcNow - state.LastViabilityCheck.Value
                : TimeSpan.MaxValue;
            var cacheIsFresh = state.LastViabilityCheck.HasValue && cachedAge < TimeSpan.FromHours(1);

            // The cached result has the same shape whether it is reused before or after the baseline
            // load below.
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

            // A cached "requires full scan" answer routes to the graceful skip regardless of whether a
            // baseline exists now, so reuse it without loading the depot baseline (which reads the
            // depot-mappings JSON). Only a cached "viable" answer depends on the baseline still existing.
            if (cacheIsFresh && state.RequiresFullScan)
            {
                return ReuseCachedResult();
            }

            // From here the baseline is needed: to trust a cached "viable" answer, to detect a fresh
            // install, and to supply the change number the scan will diff against. The scan always
            // reloads the change number from JSON, so the viability check reads the same value.
            var baseline = await GetDepotBaselineAsync();
            uint changeNumberToCheck = baseline.LastChangeNumber;

            // Never reuse a cached "viable" answer once the baseline is gone (e.g. depot data was
            // reset): it was computed from mappings and a change number that no longer exist, and
            // reusing it would green-light a crawl with nothing to diff against.
            if (cacheIsFresh && ShouldReuseCachedViability(state.RequiresFullScan, baseline.HasUsableBaseline))
            {
                return ReuseCachedResult();
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

                CacheViabilityResult(requiresFullScan: true, lastChangeNumber: 0, changeGap: 0);

                return BuildNeedsInitialDataResult();
            }

            _logger.LogInformation("No valid cached viability result found - checking with Steam (cache age: {Minutes} minutes)",
                cachedAge == TimeSpan.MaxValue ? -1 : (int)cachedAge.TotalMinutes);

            if (changeNumberToCheck > 0)
            {
                _logger.LogInformation("Viability check will use change number {ChangeNumber} from JSON file", changeNumberToCheck);
            }

            // Need to be connected to check current change number
            bool wasConnected = _isLoggedOn && _steamClient?.IsConnected == true;

            if (!wasConnected)
            {
                await EnsureSessionAsync(ct);
            }

            try
            {
                // Get current change number from Steam
                var currentChangeNumber = await GetPicsChangeNumberAsync(ct);

                uint changeGap = changeNumberToCheck > 0
                    ? currentChangeNumber - changeNumberToCheck
                    : currentChangeNumber;

                // Actually check with Steam if it will accept incremental update
                bool willRequireFullScan = false;
                if (changeNumberToCheck > 0)
                {
                    _logger.LogInformation("Checking with Steam if incremental update is viable (last: {Last}, current: {Current}, gap: {Gap})",
                        changeNumberToCheck, currentChangeNumber, changeGap);

                    var incrementalChanges = await RunPicsWithRecoveryAsync(async () =>
                    {
                        var incrementalJob = _steamApps!.PICSGetChangesSince(changeNumberToCheck, true, true);
                        return await WaitForCallbackAsync(incrementalJob, ct);
                    }, "PICS viability check", ct);

                    // Steam will tell us if it requires a full update
                    willRequireFullScan = incrementalChanges.RequiresFullUpdate || incrementalChanges.RequiresFullAppUpdate;

                    _logger.LogInformation("Steam RequiresFullUpdate: {Full}, RequiresFullAppUpdate: {App}",
                        incrementalChanges.RequiresFullUpdate, incrementalChanges.RequiresFullAppUpdate);
                }

                // Cache the viability check result in state to avoid repeated Steam API calls
                CacheViabilityResult(willRequireFullScan, changeNumberToCheck, changeGap);

                return new IncrementalViabilityCheck
                {
                    IsViable = !willRequireFullScan,
                    LastChangeNumber = changeNumberToCheck,
                    CurrentChangeNumber = currentChangeNumber,
                    ChangeGap = changeGap,
                    IsLargeGap = willRequireFullScan,
                    WillTriggerFullScan = willRequireFullScan,
                    EstimatedAppsToScan = willRequireFullScan ? 270000 : (int)Math.Min(changeGap * 2, 50000) // Rough estimate
                };
            }
            finally
            {
                // Connection will be reused if a crawl starts immediately after viability check
            }
        }
        catch (TimeoutException tex)
        {
            _logger.LogWarning("Steam connection timed out while checking incremental viability: {Message}", tex.Message);

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
                Error = $"Connection failed: {ex.Message}"
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
            jsonMappingCount = Math.Max(picsData.Metadata?.TotalMappings ?? 0, picsData.DepotMappings?.Count ?? 0);
            if (picsData.Metadata?.LastChangeNumber > 0)
            {
                lastChangeNumber = picsData.Metadata.LastChangeNumber;
            }
        }

        return new DepotBaseline(databaseMappingCount, jsonMappingCount, lastChangeNumber);
    }

    /// <summary>
    /// A cached viability result may be reused when it already requires a full scan (that answer
    /// stays safe and routes to the graceful skip) or when a usable baseline still exists. A cached
    /// "viable" answer must not be reused once the baseline is gone, since it was computed from
    /// mappings and a change number that no longer exist.
    /// </summary>
    internal static bool ShouldReuseCachedViability(bool cachedRequiresFullScan, bool hasUsableBaseline)
        => cachedRequiresFullScan || hasUsableBaseline;

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
    private void CacheViabilityResult(bool requiresFullScan, uint lastChangeNumber, uint changeGap)
    {
        var updatedState = _stateService.GetState();
        updatedState.RequiresFullScan = requiresFullScan;
        updatedState.LastViabilityCheck = DateTime.UtcNow;
        updatedState.LastViabilityCheckChangeNumber = lastChangeNumber;
        updatedState.ViabilityChangeGap = changeGap;
        _stateService.SaveState(updatedState);

        _logger.LogInformation("Cached viability check result in state.json (requires full scan: {RequiresFullScan}, change gap: {ChangeGap})",
            requiresFullScan, changeGap);
    }

    /// <summary>
    /// Snapshot of the depot data available to seed an incremental crawl. With no mappings in the
    /// database or JSON snapshot and no saved change number there is nothing to diff against, so a
    /// full scan is required before incremental updates can start.
    /// </summary>
    internal readonly record struct DepotBaseline(int DatabaseMappingCount, int JsonMappingCount, uint LastChangeNumber)
    {
        public bool HasUsableBaseline => DatabaseMappingCount > 0 || JsonMappingCount > 0 || LastChangeNumber > 0;
    }

    /// <summary>
    /// Reads the last known PICS change number from the cached JSON file.
    /// Returns null if the file is unavailable or contains no valid change number.
    /// Used in error paths to populate LastChangeNumber for informational reporting.
    /// </summary>
    private async Task<uint?> TryGetLastChangeNumberAsync()
    {
        try
        {
            var picsData = await _picsDataService.LoadFromJsonAsync();
            var changeNumber = picsData?.Metadata?.LastChangeNumber;
            return changeNumber > 0 ? changeNumber : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get the current Steam change number (used to update metadata after GitHub downloads)
    /// </summary>
    public async Task<uint> GetCurrentChangeNumberAsync(CancellationToken ct = default)
    {
        try
        {
            // Ensure we're connected
            bool wasConnected = _isLoggedOn && _steamClient?.IsConnected == true;
            if (!wasConnected)
            {
                await EnsureSessionAsync(ct);
            }

            try
            {
                return await GetPicsChangeNumberAsync(ct);
            }
            finally
            {
                // Connection will be reused if a crawl starts immediately
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get current change number from Steam");
            throw;
        }
    }
}
