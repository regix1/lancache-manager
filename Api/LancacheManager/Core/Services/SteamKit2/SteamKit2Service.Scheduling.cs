using LancacheManager.Hubs;

namespace LancacheManager.Core.Services.SteamKit2;

public partial class SteamKit2Service
{
    /// <summary>
    /// Called by the ConfigurableScheduledService base class on each interval tick.
    /// Checks preconditions and triggers a PICS crawl if appropriate.
    /// </summary>
    protected override async Task ExecuteScheduledWorkAsync(CancellationToken stoppingToken)
    {
        // If initialization failed (e.g. DB was unavailable), retry it before doing any work
        if (!_initialized)
        {
            _logger.LogInformation("SteamKit2Service was not initialized — retrying initialization");
            await InitializeAsync(stoppingToken);
            if (!_initialized)
            {
                _logger.LogWarning("SteamKit2Service initialization retry failed — will try again on next tick");
                return;
            }
            _logger.LogInformation("SteamKit2Service initialization succeeded on retry");
        }

        if (_cancellationTokenSource.Token.IsCancellationRequested || !_isRunning)
        {
            return;
        }

        // Skip if setup hasn't been completed yet (fresh install)
        if (!_stateService.GetSetupCompleted())
        {
            return;
        }

        // Use configured scan mode for automatic scheduled scans
        if (!IsRebuildRunning)
        {
            var scanType = GetCrawlModeString(_crawlIncrementalMode);
            _logger.LogInformation("Starting scheduled {ScanType} PICS update", scanType);

            // Check if GitHub mode - download from GitHub instead of connecting to Steam
            if (IsGithubMode(_crawlIncrementalMode))
            {
                _logger.LogInformation("[GitHub Mode] Downloading depot data from GitHub (no Steam connection)");
                var success = await DownloadAndImportGitHubDataAsync(stoppingToken);

                if (success)
                {
                    _lastCrawlTime = DateTime.UtcNow;
                    SaveLastCrawlTime(); // Persist to state.json
                    _logger.LogInformation("[GitHub Mode] Depot data updated successfully and last crawl time persisted");
                }
                else
                {
                    _logger.LogWarning("[GitHub Mode] Failed to download depot data - will retry on next scheduled check");
                }

                return;
            }

            // For automatic incremental scans, check viability first
            if (IsIncrementalMode(_crawlIncrementalMode))
            {
                try
                {
                    _logger.LogInformation("Checking incremental scan viability before starting scheduled scan");
                    var viability = await CheckIncrementalViabilityAsync(stoppingToken);

                    // Check if there was a connection/network error during viability check
                    if (!string.IsNullOrEmpty(viability.Error))
                    {
                        _logger.LogWarning("Scheduled incremental scan skipped - failed to connect to Steam: {Error}", viability.Error);
                        _logger.LogInformation("Will retry on next scheduled check. If this persists, check network connectivity and Steam service status.");
                        return;
                    }

                    // Check if Steam requires a full scan (change gap too large)
                    if (viability.WillTriggerFullScan)
                    {
                        _logger.LogWarning("Scheduled incremental scan skipped - Steam requires full scan (change gap: {ChangeGap}). User must manually trigger a full scan.", viability.ChangeGap);
                        _automaticScanSkipped = true;

                        // Send SignalR notification
                        await _notifications.NotifyAllAsync(SignalREvents.AutomaticScanSkipped, new
                        {
                            message = "Scheduled scan skipped - full scan required",
                            timestamp = DateTime.UtcNow
                        });

                        return;
                    }

                    // Viability check passed - reset the flag since incremental is now viable
                    _automaticScanSkipped = false;
                    _logger.LogInformation("Incremental scan is viable, proceeding with scheduled scan");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unexpected exception during viability check, skipping scheduled scan");
                    return;
                }
            }

            if (TryStartRebuild(_cancellationTokenSource.Token, incrementalOnly: IsIncrementalMode(_crawlIncrementalMode)))
            {
                _lastCrawlTime = DateTime.UtcNow;
                SaveLastCrawlTime(); // Persist to state.json
            }
        }
    }

    /// <summary>
    /// Enable periodic PICS crawls after initial depot data has been set up.
    /// Now simply ensures the scheduling interval is non-zero so the base class loop runs.
    /// </summary>
    public void EnablePeriodicCrawls()
    {
        if (ConfiguredInterval.TotalHours > 0)
        {
            _logger.LogInformation("Periodic crawls already enabled (interval: {Hours} hour(s))", ConfiguredInterval.TotalHours);
            return;
        }

        // Re-enable with default interval if currently disabled
        var savedInterval = _stateService.GetCrawlIntervalHours();
        var interval = savedInterval > 0 ? savedInterval : 1.0;
        _logger.LogInformation("Enabling periodic PICS crawls with interval: {Hours} hour(s)", interval);
        UpdateInterval(TimeSpan.FromHours(interval));
    }

    /// <summary>
    /// Update the last crawl time to now (used after manual data imports like GitHub downloads)
    /// </summary>
    public void UpdateLastCrawlTime()
    {
        _lastCrawlTime = DateTime.UtcNow;
        SaveLastCrawlTime(); // Persist to state.json
        _logger.LogInformation("Updated last crawl time to {Time} and persisted to state (prevents automatic scan from triggering)", _lastCrawlTime);
    }

    /// <summary>
    /// Clear the automatic scan skipped flag (used after manual actions like GitHub downloads or forced scans)
    /// </summary>
    public void ClearAutomaticScanSkippedFlag()
    {
        if (_automaticScanSkipped)
        {
            _automaticScanSkipped = false;
            _logger.LogInformation("Cleared automatic scan skipped flag");
        }
    }
}
