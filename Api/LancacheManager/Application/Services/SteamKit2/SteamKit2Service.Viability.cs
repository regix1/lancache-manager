using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;

namespace LancacheManager.Application.Services;

public partial class SteamKit2Service
{
    /// <summary>
    /// Check if incremental scan is viable or if change gap is too large (will trigger full scan)
    /// Uses cached result from state.json if available and recent (< 1 hour old) to avoid repeated Steam API calls
    /// </summary>
    public async Task<IncrementalViabilityCheck> CheckIncrementalViabilityAsync(CancellationToken ct)
    {
        try
        {
            // Check for cached viability result in state (prevents repeated Steam API calls)
            var state = _stateService.GetState();
            var cachedAge = state.LastViabilityCheck.HasValue
                ? DateTime.UtcNow - state.LastViabilityCheck.Value
                : TimeSpan.MaxValue;

            // Use cached result if it's less than 1 hour old
            if (state.LastViabilityCheck.HasValue && cachedAge < TimeSpan.FromHours(1))
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

            _logger.LogInformation("No valid cached viability result found - checking with Steam (cache age: {Minutes} minutes)",
                cachedAge == TimeSpan.MaxValue ? -1 : (int)cachedAge.TotalMinutes);

            // Always load the latest change number from JSON to ensure viability check matches what the scan will use
            // (scan always reloads from JSON, so viability check must too)
            var picsData = await _picsDataService.LoadPicsDataFromJsonAsync();
            uint changeNumberToCheck = 0;
            if (picsData?.Metadata?.LastChangeNumber > 0)
            {
                changeNumberToCheck = picsData.Metadata.LastChangeNumber;
                _logger.LogInformation("Viability check will use change number {ChangeNumber} from JSON file", changeNumberToCheck);
            }

            // Need to be connected to check current change number
            bool wasConnected = _isLoggedOn && _steamClient?.IsConnected == true;

            if (!wasConnected)
            {
                await ConnectAndLoginAsync(ct);
            }

            try
            {
                // Get current change number from Steam
                var job = _steamApps!.PICSGetChangesSince(0, false, false);
                var changes = await WaitForCallbackAsync(job, ct);
                var currentChangeNumber = changes.CurrentChangeNumber;

                uint changeGap = changeNumberToCheck > 0
                    ? currentChangeNumber - changeNumberToCheck
                    : currentChangeNumber;

                // Actually check with Steam if it will accept incremental update
                bool willRequireFullScan = false;
                if (changeNumberToCheck > 0)
                {
                    _logger.LogInformation("Checking with Steam if incremental update is viable (last: {Last}, current: {Current}, gap: {Gap})",
                        changeNumberToCheck, currentChangeNumber, changeGap);

                    var incrementalJob = _steamApps!.PICSGetChangesSince(changeNumberToCheck, true, true);
                    var incrementalChanges = await WaitForCallbackAsync(incrementalJob, ct);

                    // Steam will tell us if it requires a full update
                    willRequireFullScan = incrementalChanges.RequiresFullUpdate || incrementalChanges.RequiresFullAppUpdate;

                    _logger.LogInformation("Steam RequiresFullUpdate: {Full}, RequiresFullAppUpdate: {App}",
                        incrementalChanges.RequiresFullUpdate, incrementalChanges.RequiresFullAppUpdate);
                }

                // Cache the viability check result in state to avoid repeated Steam API calls
                var updatedState = _stateService.GetState();
                updatedState.RequiresFullScan = willRequireFullScan;
                updatedState.LastViabilityCheck = DateTime.UtcNow;
                updatedState.LastViabilityCheckChangeNumber = changeNumberToCheck;
                updatedState.ViabilityChangeGap = changeGap;
                _stateService.SaveState(updatedState);

                _logger.LogInformation("Cached viability check result in state.json (requires full scan: {RequiresFullScan}, change gap: {ChangeGap})",
                    willRequireFullScan, changeGap);

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
                // Keep connection alive for a short period if we just connected
                // This allows reuse if a crawl starts immediately after viability check
                if (!wasConnected && _steamClient?.IsConnected == true)
                {
                    _lastConnectionActivity = SteamKit2Helpers.UpdateConnectionActivity();
                    StartIdleDisconnectTimer();
                }
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

            // Try to get the change number from JSON for error reporting
            uint changeNumberForError = 0;
            try
            {
                var picsData = await _picsDataService.LoadPicsDataFromJsonAsync();
                changeNumberForError = picsData?.Metadata?.LastChangeNumber ?? 0;
            }
            catch { }

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

            // Try to get the change number from JSON for error reporting
            uint changeNumberForError = 0;
            try
            {
                var picsData = await _picsDataService.LoadPicsDataFromJsonAsync();
                changeNumberForError = picsData?.Metadata?.LastChangeNumber ?? 0;
            }
            catch { }

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
                await ConnectAndLoginAsync(ct);
            }

            try
            {
                // Get current change number from Steam
                var job = _steamApps!.PICSGetChangesSince(0, false, false);
                var changes = await WaitForCallbackAsync(job, ct);
                return changes.CurrentChangeNumber;
            }
            finally
            {
                // Keep connection alive for a short period if we just connected
                if (!wasConnected && _steamClient?.IsConnected == true)
                {
                    _lastConnectionActivity = SteamKit2Helpers.UpdateConnectionActivity();
                    StartIdleDisconnectTimer();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get current change number from Steam");
            throw;
        }
    }
}
