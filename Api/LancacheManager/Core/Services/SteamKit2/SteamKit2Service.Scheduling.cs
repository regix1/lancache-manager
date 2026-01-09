using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Core.Services.SteamKit2;

public partial class SteamKit2Service
{
    private void SetupPeriodicCrawls()
    {
        // Don't set up timer if interval is 0 (disabled)
        if (_crawlInterval.TotalHours == 0)
        {
            _logger.LogInformation("Periodic crawls are disabled (interval = 0)");
            return;
        }

        // Check if a scan is overdue - log it but don't automatically trigger
        var timeSinceLastCrawl = DateTime.UtcNow - _lastCrawlTime;
        var isDue = timeSinceLastCrawl >= _crawlInterval;

        if (isDue && _lastCrawlTime != DateTime.MinValue)
        {
            _logger.LogInformation("Scan is overdue by {Minutes} minutes - scheduled scan will run at next check (within 1 minute)",
                (int)(timeSinceLastCrawl - _crawlInterval).TotalMinutes);
            // Don't automatically trigger overdue scans on startup - let the periodic timer handle it
            // This prevents automatic downloads/scans when the user just started the application
        }

        // Set up the periodic timer to check every minute if a scan is due
        // This ensures scans trigger even if interval changes or system restarts
        _periodicTimer = new Timer(OnPeriodicCrawlTimer, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _logger.LogInformation($"Scheduled PICS updates every {_crawlInterval.TotalHours} hour(s) (checking every minute)");
    }

    private void OnPeriodicCrawlTimer(object? state)
    {
        if (_cancellationTokenSource.Token.IsCancellationRequested || !_isRunning)
        {
            return;
        }

        // Skip if interval is 0 (disabled)
        if (_crawlInterval.TotalHours == 0)
        {
            return;
        }

        // Check if enough time has elapsed since last crawl
        var timeSinceLastCrawl = DateTime.UtcNow - _lastCrawlTime;
        var isDue = timeSinceLastCrawl >= _crawlInterval;

        if (!isDue)
        {
            return;
        }

        // Use configured scan mode for automatic scheduled scans
        _ = Task.Run(async () =>
        {
            if (!IsRebuildRunning)
            {
                var scanType = GetCrawlModeString(_crawlIncrementalMode);
                _logger.LogInformation("Starting scheduled {ScanType} PICS update (due: last crawl was {Minutes} minutes ago)",
                    scanType, (int)timeSinceLastCrawl.TotalMinutes);

                // Check if GitHub mode - download from GitHub instead of connecting to Steam
                if (IsGithubMode(_crawlIncrementalMode))
                {
                    _logger.LogInformation("[GitHub Mode] Downloading depot data from GitHub (no Steam connection)");
                    var success = await DownloadAndImportGitHubDataAsync(_cancellationTokenSource.Token);

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
                        var viability = await CheckIncrementalViabilityAsync(_cancellationTokenSource.Token);

                        // Check if there was a connection/network error during viability check
                        if (!string.IsNullOrEmpty(viability.Error))
                        {
                            _logger.LogWarning("Scheduled incremental scan skipped - failed to connect to Steam: {Error}", viability.Error);
                            _logger.LogInformation("Will retry on next scheduled check. If this persists, check network connectivity and Steam service status.");
                            // Don't set _automaticScanSkipped flag - this is a transient connection issue, not a "requires full scan" situation
                            return;
                        }

                        // Check if Steam requires a full scan (change gap too large)
                        if (viability.WillTriggerFullScan)
                        {
                            _logger.LogWarning("Scheduled incremental scan skipped - Steam requires full scan (change gap: {ChangeGap}). User must manually trigger a full scan.", viability.ChangeGap);
                            _automaticScanSkipped = true;

                            // Send SignalR notification
                            try
                            {
                                await _hubContext.Clients.All.SendAsync("AutomaticScanSkipped", new
                                {
                                    message = "Scheduled scan skipped - full scan required",
                                    timestamp = DateTime.UtcNow
                                });
                            }
                            catch (Exception signalREx)
                            {
                                _logger.LogWarning(signalREx, "Failed to send AutomaticScanSkipped notification via SignalR");
                            }

                            return;
                        }

                        // Viability check passed - reset the flag since incremental is now viable
                        _automaticScanSkipped = false;
                        _logger.LogInformation("Incremental scan is viable, proceeding with scheduled scan");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Unexpected exception during viability check, skipping scheduled scan");
                        // Don't set _automaticScanSkipped flag - this is a transient error, not a "requires full scan" situation
                        return;
                    }
                }

                if (TryStartRebuild(_cancellationTokenSource.Token, incrementalOnly: IsIncrementalMode(_crawlIncrementalMode)))
                {
                    _lastCrawlTime = DateTime.UtcNow;
                    SaveLastCrawlTime(); // Persist to state.json
                }
            }
        });
    }

    /// <summary>
    /// Enable periodic PICS crawls after initial depot data has been set up
    /// </summary>
    public void EnablePeriodicCrawls()
    {
        if (_periodicTimer != null)
        {
            _logger.LogInformation("Periodic crawls already enabled");
            return;
        }

        _logger.LogInformation("Enabling periodic PICS crawls");
        SetupPeriodicCrawls();
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
